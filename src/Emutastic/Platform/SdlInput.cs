using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Platform
{
    /// <summary>
    /// SDL3-backed gamepad input. Replaces the upstream Windows ControllerManager (XInput).
    /// The upstream codebase only had SDL3 P/Invoke for device naming; the actual state-polling
    /// layer here is new (written from scratch for the Linux port).
    ///
    /// Scope (M2 vertical slice): standard digital joypad mapping for connected gamepads + a
    /// keyboard fallback for player 1 so a ROM is playable without a controller. Per-console
    /// button remapping (LibretroInput tables), analog sticks, deadzones, turbo and rumble are
    /// refinements layered on when the full input/config UI is ported.
    /// </summary>
    public sealed class SdlInput : IDisposable
    {
        const uint SDL_INIT_GAMEPAD = 0x00002000;

        // SDL_GamepadButton
        const int SDL_GAMEPAD_BUTTON_SOUTH = 0, SDL_GAMEPAD_BUTTON_EAST = 1, SDL_GAMEPAD_BUTTON_WEST = 2,
                  SDL_GAMEPAD_BUTTON_NORTH = 3, SDL_GAMEPAD_BUTTON_BACK = 4, SDL_GAMEPAD_BUTTON_START = 6,
                  SDL_GAMEPAD_BUTTON_LEFT_STICK = 7, SDL_GAMEPAD_BUTTON_RIGHT_STICK = 8,
                  SDL_GAMEPAD_BUTTON_LEFT_SHOULDER = 9, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER = 10,
                  SDL_GAMEPAD_BUTTON_DPAD_UP = 11, SDL_GAMEPAD_BUTTON_DPAD_DOWN = 12,
                  SDL_GAMEPAD_BUTTON_DPAD_LEFT = 13, SDL_GAMEPAD_BUTTON_DPAD_RIGHT = 14;

        // libretro RETRO_DEVICE_ID_JOYPAD_*
        public const uint RETRO_DEVICE_JOYPAD = 1;
        const int RJ_B = 0, RJ_Y = 1, RJ_SELECT = 2, RJ_START = 3, RJ_UP = 4, RJ_DOWN = 5,
                  RJ_LEFT = 6, RJ_RIGHT = 7, RJ_A = 8, RJ_X = 9, RJ_L = 10, RJ_R = 11,
                  RJ_L3 = 14, RJ_R3 = 15;
        const int JOYPAD_COUNT = 16;
        public const uint RETRO_DEVICE_ANALOG = 5;
        // Set per-console from the handler: analog consoles (PS1/N64/GC…) report stick values; digital
        // consoles (NES/Genesis/arcade…) instead let the left stick drive the d-pad.
        public bool UsesAnalogStick;
        public bool PromoteAnalogStickToDpad;

        // libretro joypad id -> SDL gamepad button (-1 = unmapped for M2, e.g. L2/R2 triggers)
        static readonly int[] _retroToSdl = BuildMap();
        static int[] BuildMap()
        {
            var m = new int[JOYPAD_COUNT];
            for (int i = 0; i < JOYPAD_COUNT; i++) m[i] = -1;
            m[RJ_B] = SDL_GAMEPAD_BUTTON_SOUTH;   m[RJ_A] = SDL_GAMEPAD_BUTTON_EAST;
            m[RJ_Y] = SDL_GAMEPAD_BUTTON_WEST;    m[RJ_X] = SDL_GAMEPAD_BUTTON_NORTH;
            m[RJ_SELECT] = SDL_GAMEPAD_BUTTON_BACK; m[RJ_START] = SDL_GAMEPAD_BUTTON_START;
            m[RJ_UP] = SDL_GAMEPAD_BUTTON_DPAD_UP; m[RJ_DOWN] = SDL_GAMEPAD_BUTTON_DPAD_DOWN;
            m[RJ_LEFT] = SDL_GAMEPAD_BUTTON_DPAD_LEFT; m[RJ_RIGHT] = SDL_GAMEPAD_BUTTON_DPAD_RIGHT;
            m[RJ_L] = SDL_GAMEPAD_BUTTON_LEFT_SHOULDER; m[RJ_R] = SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER;
            m[RJ_L3] = SDL_GAMEPAD_BUTTON_LEFT_STICK; m[RJ_R3] = SDL_GAMEPAD_BUTTON_RIGHT_STICK;
            return m;
        }

        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_InitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_QuitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_PumpEvents();
        [DllImport("SDL3")] static extern IntPtr SDL_GetGamepads(out int count);
        [DllImport("SDL3")] static extern IntPtr SDL_OpenGamepad(uint instance_id);
        [DllImport("SDL3")] static extern void SDL_CloseGamepad(IntPtr gamepad);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
        [DllImport("SDL3")] static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
        [DllImport("SDL3")] static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
        [DllImport("SDL3")] static extern void SDL_UpdateGamepads();
        [DllImport("SDL3")] static extern void SDL_free(IntPtr mem);

        // open gamepads in player order
        private readonly List<(uint id, IntPtr handle)> _pads = new();
        private int _refreshCounter;

        // keyboard fallback state for player 1 (libretro joypad id -> pressed)
        private readonly bool[] _kbd = new bool[JOYPAD_COUNT];
        private bool _initialized;

        // ── Per-console configured mappings (from the Preferences → Controls panel). ──
        // _ctrlMap[port][libretroId] = raw control id to read (0..20 SDL button, 100/101 trigger,
        // 110..117 stick dir), or -1 if unmapped. A null port entry ⇒ fall back to the default
        // _retroToSdl mapping. _kbdRetro maps an Avalonia Key name (Key.ToString()) → libretro id
        // for player 1; consulted by EmulatorWindow before its built-in KeyMap.
        private readonly int[]?[] _ctrlMap = new int[4][];
        private readonly Dictionary<string, int> _kbdRetro = new(StringComparer.OrdinalIgnoreCase);

        // SDL_GamepadAxis indices + thresholds (mirror ControllerManager's raw id space).
        const int AXIS_LEFTX = 0, AXIS_LEFTY = 1, AXIS_RIGHTX = 2, AXIS_RIGHTY = 3, AXIS_LTRIG = 4, AXIS_RTRIG = 5;
        const short STICK_THRESHOLD = 18000, TRIG_THRESHOLD = 12000;

        // Cheap ctor (no SDL calls) so the XAML designer can construct an EmulatorSession
        // without a working SDL3 library. SDL is initialized lazily in Initialize().
        public SdlInput() { }

        /// <summary>Initialize the SDL gamepad subsystem. Called once before the emu loop starts.</summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            SDL_InitSubSystem(SDL_INIT_GAMEPAD);
            Refresh();
        }

        /// <summary>
        /// Load the per-console input mappings saved by the Controls panel. Builds, for each player
        /// port, a libretro-id → raw-control-id table from <c>ControllerMappings</c>, and a key-name →
        /// libretro-id table from player 1's <c>KeyboardMappings</c>. Ports with no controller mappings
        /// keep the built-in default. Safe to call with a null service (leaves defaults in place).
        /// </summary>
        public void LoadConfiguration(string console, IConfigurationService? cfg)
        {
            Array.Clear(_ctrlMap, 0, _ctrlMap.Length);
            _kbdRetro.Clear();
            if (cfg == null || string.IsNullOrEmpty(console)) return;

            for (int port = 0; port < 4; port++)
            {
                var config = cfg.GetInputConfiguration($"{console}_P{port + 1}");
                // Player 1 legacy fallback: pre-per-player saves used the bare console key.
                if (port == 0 && config.ControllerMappings.Count == 0 && config.KeyboardMappings.Count == 0)
                    config = cfg.GetInputConfiguration(console);

                if (config.ControllerMappings.Count > 0)
                {
                    var map = new int[JOYPAD_COUNT];
                    for (int i = 0; i < JOYPAD_COUNT; i++) map[i] = -1;
                    foreach (var m in config.ControllerMappings)
                    {
                        uint libretroId = LibretroInput.GetButtonId(m.ButtonName, console);
                        if (libretroId < JOYPAD_COUNT && int.TryParse(m.InputIdentifier, out var rawId))
                            map[libretroId] = rawId;
                    }
                    _ctrlMap[port] = map;
                }

                if (port == 0)
                    foreach (var m in config.KeyboardMappings)
                    {
                        uint libretroId = LibretroInput.GetButtonId(m.ButtonName, console);
                        if (libretroId < JOYPAD_COUNT && !string.IsNullOrEmpty(m.InputIdentifier))
                            _kbdRetro[m.InputIdentifier] = (int)libretroId;
                    }
            }
        }

        /// <summary>Configured player-1 libretro id for an Avalonia Key name, or -1 if not bound.</summary>
        public int KeyboardRetroId(string keyName) => _kbdRetro.TryGetValue(keyName, out var id) ? id : -1;

        /// <summary>True if the Controls panel has a saved player-1 keyboard mapping (else use defaults).</summary>
        public bool HasKeyboardConfig => _kbdRetro.Count > 0;

        // Read a raw control id (0..20 SDL button, 100/101 trigger, 110..117 stick dir) on a pad.
        private bool ReadRawControl(IntPtr h, int rawId)
        {
            if (rawId < 0) return false;
            if (rawId < 21) return SDL_GetGamepadButton(h, rawId);
            switch (rawId)
            {
                case 100: return SDL_GetGamepadAxis(h, AXIS_LTRIG) > TRIG_THRESHOLD;
                case 101: return SDL_GetGamepadAxis(h, AXIS_RTRIG) > TRIG_THRESHOLD;
                case 110: return SDL_GetGamepadAxis(h, AXIS_LEFTX)  < -STICK_THRESHOLD;
                case 111: return SDL_GetGamepadAxis(h, AXIS_LEFTX)  >  STICK_THRESHOLD;
                case 112: return SDL_GetGamepadAxis(h, AXIS_LEFTY)  < -STICK_THRESHOLD;
                case 113: return SDL_GetGamepadAxis(h, AXIS_LEFTY)  >  STICK_THRESHOLD;
                case 114: return SDL_GetGamepadAxis(h, AXIS_RIGHTX) < -STICK_THRESHOLD;
                case 115: return SDL_GetGamepadAxis(h, AXIS_RIGHTX) >  STICK_THRESHOLD;
                case 116: return SDL_GetGamepadAxis(h, AXIS_RIGHTY) < -STICK_THRESHOLD;
                case 117: return SDL_GetGamepadAxis(h, AXIS_RIGHTY) >  STICK_THRESHOLD;
                default:  return false;
            }
        }

        public int GamepadCount => _pads.Count;

        public string? FirstGamepadName =>
            _pads.Count > 0 ? Marshal.PtrToStringUTF8(SDL_GetGamepadName(_pads[0].handle)) : null;

        /// <summary>Open newly-connected gamepads, drop removed ones.</summary>
        private void Refresh()
        {
            IntPtr arr = SDL_GetGamepads(out int count);
            var present = new HashSet<uint>();
            for (int i = 0; i < count; i++) present.Add((uint)Marshal.ReadInt32(arr, i * 4));
            if (arr != IntPtr.Zero) SDL_free(arr);

            // close removed
            for (int i = _pads.Count - 1; i >= 0; i--)
                if (!present.Contains(_pads[i].id)) { SDL_CloseGamepad(_pads[i].handle); _pads.RemoveAt(i); }

            // open new
            foreach (uint id in present)
                if (!_pads.Exists(p => p.id == id))
                {
                    IntPtr h = SDL_OpenGamepad(id);
                    if (h != IntPtr.Zero) _pads.Add((id, h));
                }
        }

        /// <summary>Call once per emulation frame before reading input state.</summary>
        public void Poll()
        {
            if (!_initialized) return;
            SDL_PumpEvents();      // ensure hotplug add/remove events are processed
            SDL_UpdateGamepads();  // refresh open-gamepad button/axis state
            if (++_refreshCounter >= 60) { _refreshCounter = 0; Refresh(); } // re-scan ~1×/sec for hotplug
        }

        /// <summary>Set keyboard fallback state for player 1 (libretro joypad id).</summary>
        public void SetKeyboardButton(int retroId, bool pressed)
        {
            if (retroId >= 0 && retroId < JOYPAD_COUNT) _kbd[retroId] = pressed;
        }

        /// <summary>libretro retro_input_state_t backend.</summary>
        public short GetInputState(uint port, uint device, uint index, uint id)
        {
            // Analog consoles report the raw stick axis (SDL's Sint16 range == libretro's). Digital
            // consoles return 0 here — their stick is folded into the d-pad below instead.
            if (device == RETRO_DEVICE_ANALOG)
                return UsesAnalogStick ? ReadAnalog(port, index, id) : (short)0;

            if (device != RETRO_DEVICE_JOYPAD || id >= JOYPAD_COUNT) return 0;

            bool pressed = false;

            // gamepad for this player slot — configured mapping if present, else the default.
            if (port < (uint)_pads.Count)
            {
                IntPtr h = _pads[(int)port].handle;
                var map = port < 4 ? _ctrlMap[(int)port] : null;
                if (map != null)
                {
                    if (ReadRawControl(h, map[(int)id])) pressed = true;
                }
                else
                {
                    int sdlBtn = _retroToSdl[(int)id];
                    if (sdlBtn >= 0 && SDL_GetGamepadButton(h, sdlBtn)) pressed = true;
                }

                // Digital consoles: let the left analog stick drive the d-pad when no digital
                // direction is held (handler.PromoteAnalogStickToDpad).
                if (!pressed && PromoteAnalogStickToDpad)
                    pressed = (int)id switch
                    {
                        RJ_UP    => SDL_GetGamepadAxis(h, AXIS_LEFTY) < -STICK_THRESHOLD,
                        RJ_DOWN  => SDL_GetGamepadAxis(h, AXIS_LEFTY) >  STICK_THRESHOLD,
                        RJ_LEFT  => SDL_GetGamepadAxis(h, AXIS_LEFTX) < -STICK_THRESHOLD,
                        RJ_RIGHT => SDL_GetGamepadAxis(h, AXIS_LEFTX) >  STICK_THRESHOLD,
                        _        => false
                    };
            }

            // keyboard fallback only for player 1
            if (port == 0 && _kbd[(int)id]) pressed = true;

            return pressed ? (short)1 : (short)0;
        }

        // RETRO_DEVICE_ANALOG: index 0 = left stick, 1 = right; id 0 = X, 1 = Y. SDL_GetGamepadAxis
        // already returns the -32768..32767 range libretro expects.
        private short ReadAnalog(uint port, uint index, uint id)
        {
            if (port >= (uint)_pads.Count) return 0;
            IntPtr h = _pads[(int)port].handle;
            int axis = (index, id) switch
            {
                (0u, 0u) => AXIS_LEFTX,  (0u, 1u) => AXIS_LEFTY,
                (1u, 0u) => AXIS_RIGHTX, (1u, 1u) => AXIS_RIGHTY,
                _        => -1
            };
            return axis < 0 ? (short)0 : SDL_GetGamepadAxis(h, axis);
        }

        public void Dispose()
        {
            foreach (var p in _pads) SDL_CloseGamepad(p.handle);
            _pads.Clear();
            if (_initialized) { SDL_QuitSubSystem(SDL_INIT_GAMEPAD); _initialized = false; }
        }
    }
}
