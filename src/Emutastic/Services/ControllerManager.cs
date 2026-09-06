using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Emutastic.Emulator;
using Emutastic.Platform;

namespace Emutastic.Services
{
    /// <summary>
    /// SDL3-native controller manager for the Preferences → Controls panel (replaces the upstream
    /// XInput ControllerManager). Polls connected controllers on a UI DispatcherTimer and raises
    /// <see cref="ButtonChanged"/> on press/release edges so the panel can "press a button to bind".
    ///
    /// Devices come from <see cref="SdlDeviceSet"/>, the same layer the game session reads through,
    /// so the ids this panel offers ("product name#occurrence") are the ids the session binds to —
    /// and a joystick SDL has no gamepad mapping for is listed and capturable here, not invisible.
    ///
    /// Thread model (per audit): SDL gamepad pumping isn't multi-thread safe, and the emu loop
    /// (EmulatorSession) pumps on its own thread while a game runs. So this manager only calls
    /// SDL_PumpEvents/Update* when <see cref="EmulatorSession.AnyActive"/> is false; while a
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
        const uint SDL_INIT_JOYSTICK = 0x00000200, SDL_INIT_GAMEPAD = 0x00002000;

        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_InitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_QuitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_PumpEvents();
        [DllImport("SDL3")] static extern void SDL_UpdateJoysticks();
        [DllImport("SDL3")] static extern void SDL_UpdateGamepads();

        /// <summary>Fires on a control press/release edge while <see cref="RawMode"/> is on:
        /// (raw control id, isPressed). Used by the Controls panel's bind capture.</summary>
        public event Action<uint, bool>? ButtonChanged;

        /// <summary>Fires when the connected-device set changes (hotplug).</summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>When true, poll edges are reported via <see cref="ButtonChanged"/> (capture mode).</summary>
        public bool RawMode { get; set; }

        public bool IsConnected => _set.Devices.Count > 0;

        // ── EmuTV raw-poll adapter ───────────────────────────────────────────────
        // EmuTV polls button state continuously (it does NOT use the capture/ButtonChanged flow), so we
        // keep an XInput-layout snapshot of the active/first pad refreshed every tick, independent of
        // RawMode. The XInput bit layout and the RAW_*/ANALOG_* names mirror the upstream (XInput)
        // ControllerManager so the EmuTV window's raw-input code ports across unchanged.
        private const ushort XI_DPAD_UP = 0x0001, XI_DPAD_DOWN = 0x0002, XI_DPAD_LEFT = 0x0004, XI_DPAD_RIGHT = 0x0008,
                             XI_START = 0x0010, XI_BACK = 0x0020, XI_LTHUMB = 0x0040, XI_RTHUMB = 0x0080,
                             XI_LB = 0x0100, XI_RB = 0x0200, XI_A = 0x1000, XI_B = 0x2000, XI_X = 0x4000, XI_Y = 0x8000;

        public const ushort RAW_A = XI_A, RAW_B = XI_B, RAW_X = XI_X, RAW_Y = XI_Y,
                            RAW_BACK = XI_BACK, RAW_START = XI_START,
                            RAW_DPAD_UP = XI_DPAD_UP, RAW_DPAD_DOWN = XI_DPAD_DOWN,
                            RAW_DPAD_LEFT = XI_DPAD_LEFT, RAW_DPAD_RIGHT = XI_DPAD_RIGHT,
                            RAW_LB = XI_LB, RAW_RB = XI_RB;

        // Analog-stick direction ids for GetButtonState (aligned to the capture raw-id stick space).
        public const uint ANALOG_LEFT_LEFT = 110, ANALOG_LEFT_RIGHT = 111, ANALOG_LEFT_UP = 112, ANALOG_LEFT_DOWN = 113,
                          ANALOG_RIGHT_LEFT = 114, ANALOG_RIGHT_RIGHT = 115, ANALOG_RIGHT_UP = 116, ANALOG_RIGHT_DOWN = 117;

        private ushort _lastRawButtons;            // XInput-layout snapshot of the polled pad
        private short _lx, _ly, _rx, _ry;          // its live stick axes
        private short _lt, _rt;                     // its live trigger axes (0..32767)

        /// <summary>True if the given raw XInput button bit (e.g. <see cref="RAW_A"/>) is currently down on
        /// the active/first pad. Continuous poll — used by the EmuTV couch shell, not the capture flow.</summary>
        public bool IsRawXInputButtonDown(ushort mask) => (_lastRawButtons & mask) != 0;

        /// <summary>True if the given analog-stick direction (ANALOG_* id) is currently deflected past the
        /// threshold on the polled pad. Lets EmuTV nav accept stick input alongside the d-pad.</summary>
        public bool GetButtonState(uint analogId) => analogId switch
        {
            ANALOG_LEFT_LEFT   => _lx < -SdlDeviceSet.STICK_THRESHOLD,
            ANALOG_LEFT_RIGHT  => _lx >  SdlDeviceSet.STICK_THRESHOLD,
            ANALOG_LEFT_UP     => _ly < -SdlDeviceSet.STICK_THRESHOLD,
            ANALOG_LEFT_DOWN   => _ly >  SdlDeviceSet.STICK_THRESHOLD,
            ANALOG_RIGHT_LEFT  => _rx < -SdlDeviceSet.STICK_THRESHOLD,
            ANALOG_RIGHT_RIGHT => _rx >  SdlDeviceSet.STICK_THRESHOLD,
            ANALOG_RIGHT_UP    => _ry < -SdlDeviceSet.STICK_THRESHOLD,
            ANALOG_RIGHT_DOWN  => _ry >  SdlDeviceSet.STICK_THRESHOLD,
            _ => false,
        };

        /// <summary>True while a trigger is pressed past the threshold on the polled pad.</summary>
        public bool IsRawTriggerDown(bool rightTrigger) => (rightTrigger ? _rt : _lt) > SdlDeviceSet.TRIG_THRESHOLD;

        /// <summary>Raw snapshot, for chord diagnostics.</summary>
        public string RawDebug =>
            $"pads={_set.Devices.Count} active={_activeDeviceId ?? "(first)"} btns=0x{_lastRawButtons:X4} " +
            $"L3={(_lastRawButtons & XI_LTHUMB) != 0} R3={(_lastRawButtons & XI_RTHUMB) != 0} lt={_lt} rt={_rt} chord={IsTvModeChordHeld}";

        /// <summary>The EmuTV launch chord — both triggers + both thumbsticks clicked (L2+R2+L3+R3).
        /// Chosen to avoid colliding with normal in-game input and desktop gestures.</summary>
        public bool IsTvModeChordHeld =>
            IsRawTriggerDown(false) && IsRawTriggerDown(true) &&
            (_lastRawButtons & XI_LTHUMB) != 0 && (_lastRawButtons & XI_RTHUMB) != 0;

        // Refresh the raw snapshot from the active pad (or the first connected one) every poll tick.
        private void UpdateRawSnapshot()
        {
            var d = ActiveDevice();
            if (d == null) { _lastRawButtons = 0; _lx = _ly = _rx = _ry = _lt = _rt = 0; return; }
            ushort raw = 0;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_SOUTH))          raw |= XI_A;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_EAST))           raw |= XI_B;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_WEST))           raw |= XI_X;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_NORTH))          raw |= XI_Y;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_BACK))           raw |= XI_BACK;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_START))          raw |= XI_START;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_LEFT_STICK))     raw |= XI_LTHUMB;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_RIGHT_STICK))    raw |= XI_RTHUMB;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_LEFT_SHOULDER))  raw |= XI_LB;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_RIGHT_SHOULDER)) raw |= XI_RB;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_DPAD_UP))        raw |= XI_DPAD_UP;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_DPAD_DOWN))      raw |= XI_DPAD_DOWN;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_DPAD_LEFT))      raw |= XI_DPAD_LEFT;
            if (_set.ReadButton(d, SdlDeviceSet.BTN_DPAD_RIGHT))     raw |= XI_DPAD_RIGHT;
            _lastRawButtons = raw;
            _lx = _set.ReadAxis(d, SdlDeviceSet.AXIS_LEFTX);  _ly = _set.ReadAxis(d, SdlDeviceSet.AXIS_LEFTY);
            _rx = _set.ReadAxis(d, SdlDeviceSet.AXIS_RIGHTX); _ry = _set.ReadAxis(d, SdlDeviceSet.AXIS_RIGHTY);
            _lt = _set.ReadAxis(d, SdlDeviceSet.AXIS_LTRIG);  _rt = _set.ReadAxis(d, SdlDeviceSet.AXIS_RTRIG);
        }

        // Detection/hot-plug diagnostics → Logs/controller-diag.log (see ControllerDiagLog).
        private static void CtrlLog(string msg) => ControllerDiagLog.Write($"[panel] {msg}");

        private readonly SdlDeviceSet _set = new();
        private readonly Dictionary<uint, bool> _prev = new();    // raw id -> last pressed (active device)
        private string? _activeDeviceId;                          // id the panel is capturing from; null = first
        private readonly DispatcherTimer _timer;
        private bool _initialized;
        private bool _disposed;

        public ControllerManager()
        {
            _initialized = SDL_InitSubSystem(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD);   // refcounted — safe alongside a session's SdlInput
            CtrlLog(_initialized ? "SDL joystick+gamepad subsystems initialized"
                                 : "SDL joystick+gamepad subsystem init FAILED");
            Refresh();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
        }

        /// <summary>Connected controllers as (binding id, display name), in SDL enumeration order.
        /// The display name is what the dropdown shows; the id is what gets persisted.</summary>
        public List<(string Id, string DisplayName)> GetDevices()
        {
            var list = new List<(string, string)>(_set.Devices.Count);
            foreach (var d in _set.Devices) list.Add((d.Id, d.DisplayName));
            return list;
        }

        /// <summary>Connected controller display names, in enumeration order (panel prepends "Keyboard").</summary>
        public List<string> GetDeviceNames()
        {
            var names = new List<string>(_set.Devices.Count);
            foreach (var d in _set.Devices) names.Add(d.DisplayName);
            return names;
        }

        /// <summary>Select which connected controller capture/state reads from, by binding id.
        /// Null = the first connected one. Resolved on every tick, so it follows hot-plug.</summary>
        public void SetActiveDevice(string? id) { _activeDeviceId = id; _prev.Clear(); }

        /// <summary>Select the capture device by enumeration index (legacy callers).</summary>
        public void SetActiveDevice(int index) =>
            SetActiveDevice(index >= 0 && index < _set.Devices.Count ? _set.Devices[index].Id : null);

        private SdlDeviceSet.Device? ActiveDevice()
        {
            if (_activeDeviceId != null) return _set.Get(_activeDeviceId);   // null while it is unplugged
            return _set.Devices.Count > 0 ? _set.Devices[0] : null;
        }

        private void Refresh()
        {
            if (!_set.Reconcile()) return;
            _prev.Clear();
            var names = new List<string>();
            foreach (var d in _set.Devices) names.Add($"\"{d.Id}\"{(d.IsGamepad ? "" : " (raw)")}");
            CtrlLog($"Device set changed: count={_set.Devices.Count} [{string.Join(", ", names)}]");
            ConnectionChanged?.Invoke(IsConnected);
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
            if (!EmulatorSession.AnyActive) { SDL_PumpEvents(); SDL_UpdateJoysticks(); SDL_UpdateGamepads(); }
            if (++_refreshCounter >= 60) { _refreshCounter = 0; Refresh(); }   // ~1Hz hotplug rescan

            // EmuTV raw-poll snapshot — kept fresh every tick regardless of RawMode (the couch shell
            // polls IsRawXInputButtonDown/GetButtonState; it doesn't use the capture/ButtonChanged flow).
            UpdateRawSnapshot();

            if (!RawMode) return;
            var d = ActiveDevice();
            if (d == null) return;

            for (int b = 0; b < SdlDeviceSet.BTN_COUNT; b++) Edge((uint)b, _set.ReadButton(d, b));
            Edge(SdlDeviceSet.RAW_L2, _set.ReadControl(d, SdlDeviceSet.RAW_L2));
            Edge(SdlDeviceSet.RAW_R2, _set.ReadControl(d, SdlDeviceSet.RAW_R2));
            for (int s = SdlDeviceSet.RAW_STICK_FIRST; s <= SdlDeviceSet.RAW_STICK_LAST; s++)
                Edge((uint)s, _set.ReadControl(d, s));
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
            _set.Dispose();
            if (_initialized) SDL_QuitSubSystem(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD);
        }
    }
}
