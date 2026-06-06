using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Emutastic.Emulator;

namespace Emutastic.Services
{
    /// <summary>
    /// SDL3-native controller manager for the Preferences → Controls panel (replaces the upstream
    /// XInput ControllerManager). Polls connected gamepads on a UI DispatcherTimer and raises
    /// <see cref="ButtonChanged"/> on press/release edges so the panel can "press a button to bind".
    ///
    /// Thread model (per audit): SDL gamepad pumping isn't multi-thread safe, and the emu loop
    /// (EmulatorSession) pumps on its own thread while a game runs. So this manager only calls
    /// SDL_PumpEvents/UpdateGamepads when <see cref="EmulatorSession.AnyActive"/> is false; while a
    /// game is live it just reads the state the emu loop already pumped. UI-thread timer ⇒ the
    /// panel's capture handlers need no marshaling.
    ///
    /// Raw button id space (stable; used for capture + stored in InputConfiguration):
    ///   0..20   = SDL_GamepadButton index
    ///   100/101 = L2 / R2 trigger (axis &gt; threshold)
    ///   110..117 = left/right stick directions (LX-,LX+,LY-,LY+,RX-,RX+,RY-,RY+)
    /// </summary>
    public sealed class ControllerManager : IDisposable
    {
        const uint SDL_INIT_GAMEPAD = 0x00002000;
        const int BUTTON_COUNT = 21;         // SDL_GAMEPAD_BUTTON_COUNT (SDL3)
        const int AXIS_LEFTX = 0, AXIS_LEFTY = 1, AXIS_RIGHTX = 2, AXIS_RIGHTY = 3, AXIS_LTRIG = 4, AXIS_RTRIG = 5;
        const short STICK_THRESHOLD = 18000;
        const short TRIG_THRESHOLD  = 12000;

        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_InitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_QuitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_PumpEvents();
        [DllImport("SDL3")] static extern void SDL_UpdateGamepads();
        [DllImport("SDL3")] static extern IntPtr SDL_GetGamepads(out int count);
        [DllImport("SDL3")] static extern IntPtr SDL_OpenGamepad(uint instanceId);
        [DllImport("SDL3")] static extern void SDL_CloseGamepad(IntPtr gamepad);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
        [DllImport("SDL3")] static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
        [DllImport("SDL3")] static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
        [DllImport("SDL3")] static extern void SDL_free(IntPtr mem);

        /// <summary>Fires on a control press/release edge while <see cref="RawMode"/> is on:
        /// (raw control id, isPressed). Used by the Controls panel's bind capture.</summary>
        public event Action<uint, bool>? ButtonChanged;

        /// <summary>Fires when the connected-device set changes (hotplug).</summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>When true, poll edges are reported via <see cref="ButtonChanged"/> (capture mode).</summary>
        public bool RawMode { get; set; }

        public bool IsConnected => _pads.Count > 0;

        // Detection/hot-plug diagnostics → Logs/controller-diag.log (see ControllerDiagLog).
        private static void CtrlLog(string msg) => ControllerDiagLog.Write($"[panel] {msg}");

        private readonly List<(uint id, IntPtr handle)> _pads = new();
        private readonly Dictionary<uint, bool> _prev = new();    // raw id -> last pressed (active device)
        private int _activeDevice;                                // index into _pads for capture
        private readonly DispatcherTimer _timer;
        private bool _initialized;
        private bool _disposed;

        public ControllerManager()
        {
            _initialized = SDL_InitSubSystem(SDL_INIT_GAMEPAD);   // refcounted — safe alongside a session's SdlInput
            CtrlLog(_initialized ? "SDL gamepad subsystem initialized"
                                 : "SDL gamepad subsystem init FAILED");
            Refresh();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
        }

        /// <summary>Connected gamepad names, in player order (panel prepends "Keyboard").</summary>
        public List<string> GetDeviceNames()
        {
            var names = new List<string>();
            foreach (var p in _pads)
                names.Add(Marshal.PtrToStringUTF8(SDL_GetGamepadName(p.handle)) ?? "Controller");
            return names;
        }

        /// <summary>Select which connected gamepad capture/state reads from.</summary>
        public void SetActiveDevice(int index) { _activeDevice = index; _prev.Clear(); }

        private void Refresh()
        {
            IntPtr arr = SDL_GetGamepads(out int count);
            var present = new HashSet<uint>();
            for (int i = 0; i < count; i++) present.Add((uint)Marshal.ReadInt32(arr, i * 4));
            if (arr != IntPtr.Zero) SDL_free(arr);

            bool changed = false;
            for (int i = _pads.Count - 1; i >= 0; i--)
                if (!present.Contains(_pads[i].id)) { SDL_CloseGamepad(_pads[i].handle); _pads.RemoveAt(i); changed = true; }
            foreach (uint id in present)
                if (!_pads.Exists(p => p.id == id))
                {
                    IntPtr h = SDL_OpenGamepad(id);
                    if (h != IntPtr.Zero) { _pads.Add((id, h)); changed = true; }
                }
            if (changed)
            {
                _prev.Clear();
                CtrlLog($"Device set changed: count={_pads.Count} names=[{string.Join(", ", GetDeviceNames())}]");
                ConnectionChanged?.Invoke(IsConnected);
            }
        }

        private int _refreshCounter;
        private void Poll()
        {
            if (!_initialized || _disposed) return;
            // Only skip pumping for IN-PROCESS sessions (AnyActive): their SdlInput pumps the same
            // SDL instance from the emu thread, and two pumpers over one queue contend. A separate
            // --game-host child has its OWN SDL in its own process — evdev serves concurrent
            // readers, so pumping here can't fight it. (Gating on ExternalGameActive starved this
            // process's SDL during any game session: a controller connected mid-session stayed
            // listed-but-frozen — the Controls panel went unresponsive until an app restart.
            // Windows never hit this; XInput state reads need no pump.)
            if (!EmulatorSession.AnyActive) { SDL_PumpEvents(); SDL_UpdateGamepads(); }
            if (++_refreshCounter >= 60) { _refreshCounter = 0; Refresh(); }   // ~1Hz hotplug rescan

            if (!RawMode || _activeDevice < 0 || _activeDevice >= _pads.Count) return;
            IntPtr h = _pads[_activeDevice].handle;

            for (int b = 0; b < BUTTON_COUNT; b++) Edge((uint)b, SDL_GetGamepadButton(h, b));
            Edge(100, SDL_GetGamepadAxis(h, AXIS_LTRIG) > TRIG_THRESHOLD);
            Edge(101, SDL_GetGamepadAxis(h, AXIS_RTRIG) > TRIG_THRESHOLD);
            short lx = SDL_GetGamepadAxis(h, AXIS_LEFTX), ly = SDL_GetGamepadAxis(h, AXIS_LEFTY);
            short rx = SDL_GetGamepadAxis(h, AXIS_RIGHTX), ry = SDL_GetGamepadAxis(h, AXIS_RIGHTY);
            Edge(110, lx < -STICK_THRESHOLD); Edge(111, lx > STICK_THRESHOLD);
            Edge(112, ly < -STICK_THRESHOLD); Edge(113, ly > STICK_THRESHOLD);
            Edge(114, rx < -STICK_THRESHOLD); Edge(115, rx > STICK_THRESHOLD);
            Edge(116, ry < -STICK_THRESHOLD); Edge(117, ry > STICK_THRESHOLD);
        }

        private void Edge(uint rawId, bool pressed)
        {
            bool was = _prev.TryGetValue(rawId, out var p) && p;
            if (pressed == was) return;
            _prev[rawId] = pressed;
            ButtonChanged?.Invoke(rawId, pressed);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            foreach (var p in _pads) SDL_CloseGamepad(p.handle);
            _pads.Clear();
            if (_initialized) SDL_QuitSubSystem(SDL_INIT_GAMEPAD);
        }
    }
}
