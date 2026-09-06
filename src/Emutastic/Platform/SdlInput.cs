using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Platform
{
    /// <summary>
    /// SDL3-backed gamepad input for the game session. Replaces the upstream Windows
    /// ControllerManager (XInput). Runs in the game-host process and pumps SDL on the emu thread.
    ///
    /// WHICH PAD IS PLAYER N
    /// ---------------------
    /// Each of the four ports resolves to a device through <see cref="ResolvePorts"/>:
    ///
    ///   1. A port whose console+player config carries a <c>ControllerDeviceId</c> reads THAT
    ///      device. If it is not attached the port reads nothing — it is deliberately not handed
    ///      some other pad, because silently giving player 1 player 2's controller is the exact
    ///      confusion an explicit binding exists to remove.
    ///   2. An unbound port keeps the device it already has for as long as that device is attached
    ///      and no binding claims it.
    ///   3. An unbound port with no device takes the next unclaimed device in SDL enumeration order.
    ///
    /// Rule 2 is what makes couch play survive a disconnect: previously <c>_pads[port]</c> was a
    /// plain list, so when player 1's pad dropped out every later player shifted down a slot
    /// mid-game. Now a disconnect frees only the port that owned the departed pad.
    ///
    /// Devices, ids and reads come from <see cref="SdlDeviceSet"/>, shared with the Preferences
    /// capture panel so both agree on what "Xbox Elite Wireless Controller#1" means. That layer
    /// also covers joysticks SDL has no gamepad mapping for, which were invisible before.
    /// </summary>
    public sealed class SdlInput : IDisposable
    {
        const uint SDL_INIT_JOYSTICK = 0x00000200, SDL_INIT_GAMEPAD = 0x00002000;

        // libretro RETRO_DEVICE_ID_JOYPAD_*
        public const uint RETRO_DEVICE_JOYPAD = 1;
        const int RJ_B = 0, RJ_Y = 1, RJ_SELECT = 2, RJ_START = 3, RJ_UP = 4, RJ_DOWN = 5,
                  RJ_LEFT = 6, RJ_RIGHT = 7, RJ_A = 8, RJ_X = 9, RJ_L = 10, RJ_R = 11,
                  RJ_L2 = 12, RJ_R2 = 13, RJ_L3 = 14, RJ_R3 = 15;
        const int JOYPAD_COUNT = 16;
        // Cores that opt into RETRO_ENVIRONMENT_GET_INPUT_BITMASKS read the whole
        // joypad in one call with this id instead of 16 per-button calls (LRPS2/PS2
        // does this unconditionally). Without handling it, every button reads 0.
        public const uint RETRO_DEVICE_ID_JOYPAD_MASK = 256;
        public const uint RETRO_DEVICE_ANALOG = 5;
        // Set per-console from the handler: analog consoles (PS1/N64/GC…) report stick values; digital
        // consoles (NES/Genesis/arcade…) instead let the left stick drive the d-pad.
        public bool UsesAnalogStick;
        public bool PromoteAnalogStickToDpad;

        // libretro joypad id -> SDL gamepad button (-1 = no button; L2/R2 read the trigger axes)
        static readonly int[] _retroToSdl = BuildMap();
        static int[] BuildMap()
        {
            var m = new int[JOYPAD_COUNT];
            for (int i = 0; i < JOYPAD_COUNT; i++) m[i] = -1;
            m[RJ_B] = SdlDeviceSet.BTN_SOUTH;        m[RJ_A] = SdlDeviceSet.BTN_EAST;
            m[RJ_Y] = SdlDeviceSet.BTN_WEST;         m[RJ_X] = SdlDeviceSet.BTN_NORTH;
            m[RJ_SELECT] = SdlDeviceSet.BTN_BACK;    m[RJ_START] = SdlDeviceSet.BTN_START;
            m[RJ_UP] = SdlDeviceSet.BTN_DPAD_UP;     m[RJ_DOWN] = SdlDeviceSet.BTN_DPAD_DOWN;
            m[RJ_LEFT] = SdlDeviceSet.BTN_DPAD_LEFT; m[RJ_RIGHT] = SdlDeviceSet.BTN_DPAD_RIGHT;
            m[RJ_L] = SdlDeviceSet.BTN_LEFT_SHOULDER; m[RJ_R] = SdlDeviceSet.BTN_RIGHT_SHOULDER;
            m[RJ_L3] = SdlDeviceSet.BTN_LEFT_STICK;  m[RJ_R3] = SdlDeviceSet.BTN_RIGHT_STICK;
            return m;
        }

        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_InitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_QuitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_PumpEvents();
        [DllImport("SDL3")] static extern void SDL_UpdateJoysticks();
        [DllImport("SDL3")] static extern void SDL_UpdateGamepads();

        // De-dupes the unknown-button-name diagnostic (once per console+name pair).
        private static readonly HashSet<string> _unknownButtonNamesLogged = new();

        private readonly SdlDeviceSet _set = new();
        private int _refreshCounter;

        private sealed class Port
        {
            public string? BoundId;                 // from config; null = unbound
            public SdlDeviceSet.Device? Device;     // what this port reads right now
        }
        private readonly Port[] _ports = { new(), new(), new(), new() };

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

        // EMUTASTIC_INPUT_DIAG=1: NDS-touch input tracing (R2 wire edges + right-stick reach).
        private static readonly bool _inputDiag =
            Environment.GetEnvironmentVariable("EMUTASTIC_INPUT_DIAG") == "1";
        private bool _r2WireLast;
        private uint _rsLastId = 99; private short _rsLastVal;
        // EMUTASTIC_INPUT_DIAG=1: per-id press-edge tracing so we can see exactly which physical
        // control drives each RetroPad button the core reads (button-mapping audits).
        private readonly bool[] _wireLast = new bool[JOYPAD_COUNT];
        private static readonly string[] _rjName = {
            "B(0)","Y(1)","SELECT(2)","START(3)","UP(4)","DOWN(5)","LEFT(6)","RIGHT(7)",
            "A(8)","X(9)","L(10)","R(11)","L2(12)","R2(13)","L3(14)","R3(15)" };

        // Per-port analog-direction map (LibretroInput.ANALOG_* ids 16..23 → raw control id),
        // from the Controls panel. Slot = id - 16: [LU, LD, LL, LR, RU, RD, RL, RR]; -1 unbound.
        // When present, RETRO_DEVICE_ANALOG values are COMPOSED from the two per-direction
        // bindings (plus-half minus minus-half), the way RetroArch / DuckStation / Dolphin
        // treat sticks — direction comes from the binding captured in the Controls panel,
        // never from trusting the raw axis sign at play time. A null entry falls back to
        // reading the physical SDL axes directly (pre-binding default behavior).
        private readonly int[]?[] _analogMap = new int[4][];

        // Cheap ctor (no SDL calls) so the XAML designer can construct an EmulatorSession
        // without a working SDL3 library. SDL is initialized lazily in Initialize().
        public SdlInput() { }

        /// <summary>Initialize the SDL joystick + gamepad subsystems. Called once before the emu loop starts.</summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            SDL_InitSubSystem(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD);
            RefreshDevices();
            _announceChanges = true;   // pads found by the baseline scan above are not hot-plug events
        }

        /// <summary>
        /// Hot-plug feedback: (connected, name) for pads added/removed AFTER the baseline scan —
        /// controllers already present at game start are not events (mirrors upstream
        /// EmulatorWindow's silent first-tick prime). Raised on the emu thread from Poll().
        /// </summary>
        public event Action<bool, string>? DeviceChanged;
        private bool _announceChanges;

        /// <summary>
        /// Load the per-console input mappings and device bindings saved by the Controls panel.
        /// Safe to call with a null service (leaves defaults in place).
        /// </summary>
        public void LoadConfiguration(string console, IConfigurationService? cfg)
            => LoadConfiguration(console, cfg == null ? null : cfg.GetInputConfiguration);

        /// <summary>
        /// Same, from any config-key → configuration lookup. This is the real implementation; the
        /// self-test feeds it in-memory configurations so it never touches the user's config file.
        /// </summary>
        public void LoadConfiguration(string console, Func<string, InputConfiguration>? lookup)
        {
            Array.Clear(_ctrlMap, 0, _ctrlMap.Length);
            Array.Clear(_analogMap, 0, _analogMap.Length);
            _kbdRetro.Clear();
            foreach (var p in _ports) p.BoundId = null;
            if (lookup == null || string.IsNullOrEmpty(console)) { ResolvePorts(); return; }

            for (int port = 0; port < 4; port++)
            {
                var playerConfig = lookup($"{console}_P{port + 1}");

                // The device binding is authored per player under the "_P{N}" key and is read from
                // THERE, before the legacy fallback below can swap the config out. That fallback
                // keys off the mapping lists being empty — which is exactly the state of a player
                // who picked a pad and kept the default buttons — so reading the binding after it
                // would silently discard the pick (the upstream bug, commit 8e4b7fc).
                _ports[port].BoundId = string.IsNullOrWhiteSpace(playerConfig.ControllerDeviceId)
                    ? null : playerConfig.ControllerDeviceId;

                var config = playerConfig;
                // Player 1 legacy fallback: pre-per-player saves used the bare console key.
                if (port == 0 && config.ControllerMappings.Count == 0 && config.KeyboardMappings.Count == 0)
                    config = lookup(console);

                if (config.ControllerMappings.Count > 0)
                {
                    var map = new int[JOYPAD_COUNT];
                    for (int i = 0; i < JOYPAD_COUNT; i++) map[i] = -1;
                    foreach (var m in config.ControllerMappings)
                    {
                        uint libretroId = LibretroInput.GetButtonId(m.ButtonName, console);
                        // A saved binding whose name the translator doesn't know is a BUG
                        // (definition/translator drift — NeoGeo, CDi and the NDS Touch row have
                        // all hit this upstream). Surface it instead of silently ignoring the
                        // user's binding. Once per console+name (upstream fc55478).
                        if (libretroId == uint.MaxValue && _unknownButtonNamesLogged.Add($"{console}:{m.ButtonName}"))
                            ControllerDiagLog.Write(
                                $"[session] UNKNOWN BUTTON NAME '{m.ButtonName}' (console={console}) — binding ignored! LibretroInput.GetButtonId needs a case for it.");
                        if (!int.TryParse(m.InputIdentifier, out var rawId)) continue;
                        if (libretroId < JOYPAD_COUNT)
                            map[libretroId] = rawId;
                        else if (libretroId >= LibretroInput.ANALOG_LEFT_UP
                              && libretroId <= LibretroInput.ANALOG_RIGHT_RIGHT)
                        {
                            var amap = _analogMap[port];
                            if (amap == null)
                            {
                                amap = new int[8];
                                for (int i = 0; i < 8; i++) amap[i] = -1;
                                _analogMap[port] = amap;
                            }
                            amap[libretroId - LibretroInput.ANALOG_LEFT_UP] = rawId;
                        }
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

            ResolvePorts();
        }

        /// <summary>Configured player-1 libretro id for an Avalonia Key name, or -1 if not bound.</summary>
        public int KeyboardRetroId(string keyName) => _kbdRetro.TryGetValue(keyName, out var id) ? id : -1;

        /// <summary>True if the Controls panel has a saved player-1 keyboard mapping (else use defaults).</summary>
        public bool HasKeyboardConfig => _kbdRetro.Count > 0;

        /// <summary>Number of attached controllers (gamepad-mapped or raw).</summary>
        public int GamepadCount => _set.Devices.Count;

        /// <summary>All attached controllers, in SDL enumeration order.</summary>
        internal IReadOnlyList<SdlDeviceSet.Device> Devices => _set.Devices;

        /// <summary>The device id a port currently reads, or null (nothing attached / bound pad absent).</summary>
        public string? PortDeviceId(int port) => port is >= 0 and < 4 ? _ports[port].Device?.Id : null;

        /// <summary>The device id a port is bound to by configuration, or null when unbound.</summary>
        public string? PortBoundId(int port) => port is >= 0 and < 4 ? _ports[port].BoundId : null;

        /// <summary>
        /// Drive a pad's rumble motors (libretro: strong = low-freq/left, weak = high-freq/right).
        /// The 5s window is re-issued on every state change a core sends; a (0,0) call stops the
        /// motors. Called from the emu thread via the GET_RUMBLE_INTERFACE callback.
        /// </summary>
        public bool SetRumble(int port, ushort strong, ushort weak)
        {
            var d = DeviceFor(port);
            if (d == null) return false;
            _set.Rumble(d, strong, weak, 5000);
            return true;
        }

        public string? FirstGamepadName =>
            _set.Devices.Count > 0 ? _set.Devices[0].DisplayName : null;

        private SdlDeviceSet.Device? DeviceFor(int port) =>
            port is >= 0 and < 4 ? _ports[port].Device : null;

        /// <summary>Re-enumerate devices now (the 1 Hz hot-plug rescan does this; tests call it directly).</summary>
        public void RefreshDevices()
        {
            var added = new List<SdlDeviceSet.Device>();
            var removed = new List<SdlDeviceSet.Device>();
            if (!_set.Reconcile(added, removed)) return;

            foreach (var d in removed)
            {
                ControllerDiagLog.Write($"[session] Removed: \"{d.Id}\"");
                if (_announceChanges) DeviceChanged?.Invoke(false, d.DisplayName);
            }
            foreach (var d in added)
            {
                ControllerDiagLog.Write($"[session] Detected: \"{d.Id}\" {(d.IsGamepad ? "(gamepad)" : "(raw joystick)")}");
                if (_announceChanges) DeviceChanged?.Invoke(true, d.DisplayName);
            }
            ResolvePorts();
        }

        /// <summary>Assign a device to each port. See the class remarks for the three rules.</summary>
        private void ResolvePorts()
        {
            var attached = new HashSet<SdlDeviceSet.Device>(_set.Devices);
            var claimed  = new HashSet<SdlDeviceSet.Device>();
            var before   = new SdlDeviceSet.Device?[4];
            for (int i = 0; i < 4; i++) before[i] = _ports[i].Device;

            // 1. Bound ports claim their device — or read nothing if it is absent.
            foreach (var p in _ports)
            {
                if (p.BoundId == null) continue;
                p.Device = _set.Get(p.BoundId);
                if (p.Device != null) claimed.Add(p.Device);
            }
            // 2. Unbound ports keep what they have, if still attached and not claimed by a binding.
            foreach (var p in _ports)
            {
                if (p.BoundId != null) continue;
                if (p.Device != null && (!attached.Contains(p.Device) || claimed.Contains(p.Device)))
                    p.Device = null;
                if (p.Device != null) claimed.Add(p.Device);
            }
            // 3. Unbound, empty ports take the next unclaimed device in enumeration order.
            foreach (var d in _set.Devices)
            {
                if (claimed.Contains(d)) continue;
                Port? free = null;
                foreach (var p in _ports) if (p.BoundId == null && p.Device == null) { free = p; break; }
                if (free == null) break;
                free.Device = d;
                claimed.Add(d);
            }

            for (int i = 0; i < 4; i++)
            {
                if (ReferenceEquals(before[i], _ports[i].Device)) continue;
                var p = _ports[i];
                string source = p.BoundId != null ? $"bound \"{p.BoundId}\"" : "default (enumeration order)";
                ControllerDiagLog.Write(p.Device != null
                    ? $"[session] P{i + 1} <- \"{p.Device.Id}\"  [{source}]"
                    : $"[session] P{i + 1} <- (none)  [{source}{(p.BoundId != null ? " — NOT ATTACHED" : "")}]");
            }
        }

        /// <summary>Call once per emulation frame before reading input state.</summary>
        public void Poll()
        {
            if (!_initialized) return;
            SDL_PumpEvents();       // ensure hotplug add/remove events are processed
            SDL_UpdateJoysticks();  // refresh raw-joystick button/axis/hat state
            SDL_UpdateGamepads();   // refresh open-gamepad button/axis state
            if (++_refreshCounter >= 60) { _refreshCounter = 0; RefreshDevices(); } // re-scan ~1×/sec for hotplug
        }

        /// <summary>Set keyboard fallback state for player 1 (libretro joypad id).</summary>
        public void SetKeyboardButton(int retroId, bool pressed)
        {
            if (retroId >= 0 && retroId < JOYPAD_COUNT) _kbd[retroId] = pressed;
        }

        // Raw physical-button read on a pad, bypassing the per-console libretro mapping. Used for frontend
        // chords (Disk Swap = L3 + Start) that must register even on consoles that don't map L3/Start.
        public const int SdlButtonStart = SdlDeviceSet.BTN_START;
        public const int SdlButtonLeftStick = SdlDeviceSet.BTN_LEFT_STICK;
        public bool IsRawButtonDown(int sdlButton, int port = 0)
        {
            var d = DeviceFor(port);
            return d != null && _set.ReadButton(d, sdlButton);
        }

        /// <summary>Raw read in the panel's full id space (0..20 SDL button, 100/101 trigger,
        /// 110..117 stick dir) — for the user-configured Disk Swap chord, which may bind any
        /// capturable control, not just plain buttons.</summary>
        public bool IsRawControlDown(int rawId, int port = 0)
        {
            var d = DeviceFor(port);
            return d != null && _set.ReadControl(d, rawId);
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

            // the pad this port resolves to — configured mapping if present, else the default.
            var d = port < 4 ? _ports[port].Device : null;
            if (d != null)
            {
                var map = _ctrlMap[(int)port];
                if (map != null)
                {
                    if (_set.ReadControl(d, map[(int)id])) pressed = true;
                }
                else
                {
                    int sdlBtn = _retroToSdl[(int)id];
                    if (sdlBtn >= 0 && _set.ReadButton(d, sdlBtn)) pressed = true;
                    // Default L2/R2: the trigger axes (SDL has no digital trigger buttons).
                    // Matters out-of-the-box for NDS Touch — DeSmuME taps on the JOYPAD_R2
                    // wire, so the right trigger taps with no Controls-panel setup.
                    else if (sdlBtn < 0 && (int)id == RJ_L2)
                        pressed = _set.ReadAxis(d, SdlDeviceSet.AXIS_LTRIG) > SdlDeviceSet.TRIG_THRESHOLD;
                    else if (sdlBtn < 0 && (int)id == RJ_R2)
                        pressed = _set.ReadAxis(d, SdlDeviceSet.AXIS_RTRIG) > SdlDeviceSet.TRIG_THRESHOLD;
                }

                // Digital consoles: let the left analog stick drive the d-pad when no digital
                // direction is held (handler.PromoteAnalogStickToDpad).
                if (!pressed && PromoteAnalogStickToDpad)
                    pressed = (int)id switch
                    {
                        RJ_UP    => _set.ReadAxis(d, SdlDeviceSet.AXIS_LEFTY) < -SdlDeviceSet.STICK_THRESHOLD,
                        RJ_DOWN  => _set.ReadAxis(d, SdlDeviceSet.AXIS_LEFTY) >  SdlDeviceSet.STICK_THRESHOLD,
                        RJ_LEFT  => _set.ReadAxis(d, SdlDeviceSet.AXIS_LEFTX) < -SdlDeviceSet.STICK_THRESHOLD,
                        RJ_RIGHT => _set.ReadAxis(d, SdlDeviceSet.AXIS_LEFTX) >  SdlDeviceSet.STICK_THRESHOLD,
                        _        => false
                    };
            }

            // keyboard fallback only for player 1
            if (port == 0 && _kbd[(int)id]) pressed = true;

            // Button-mapping diagnostic (EMUTASTIC_INPUT_DIAG=1): log every RetroPad-id press edge
            // with the physical raw id it read, so a mapping bug (two ids reading the same control)
            // is visible at a glance.
            if (_inputDiag && port == 0 && pressed != _wireLast[(int)id])
            {
                _wireLast[(int)id] = pressed;
                int raw = _ctrlMap[0] != null ? _ctrlMap[0]![(int)id] : -2;   // -2 = no custom map (default table)
                ControllerDiagLog.Write(
                    $"[wire] {_rjName[(int)id]} -> {(pressed ? "DOWN" : "up")}  (raw={raw}; -1=unbound,-2=default-table)");
            }

            // EMUTASTIC_INPUT_DIAG=1: log the JOYPAD_R2 wire (NDS Touch tap) on each edge so we
            // can see whether a bound button is actually driving the wire the core reads.
            if (_inputDiag && device == RETRO_DEVICE_JOYPAD && (int)id == RJ_R2 && port == 0)
            {
                bool now = pressed;
                if (now != _r2WireLast)
                {
                    _r2WireLast = now;
                    int mapped = (_ctrlMap[0] != null) ? _ctrlMap[0]![RJ_R2] : -2;  // -2 = no custom map (default trigger path)
                    ControllerDiagLog.Write(
                        $"[nds-touch] JOYPAD_R2 wire -> {(now ? "DOWN" : "up")}  (map[R2]={mapped}; -1=unbound, -2=defaults)");
                }
            }

            return pressed ? (short)1 : (short)0;
        }

        // RETRO_DEVICE_ANALOG: index 0 = left stick, 1 = right; id 0 = X, 1 = Y. SDL's axis range is
        // the -32768..32767 libretro expects, and both are down-positive — no sign conversion.
        // index 2 = RETRO_DEVICE_INDEX_ANALOG_BUTTON, id = L2(12)/R2(13): analog trigger pressure.
        // Flycast queries Dreamcast L/R triggers this way (Crazy Taxi gas/brake); Dolphin queries
        // GC L/R the same way. SDL trigger axes already report libretro's 0..32767 range.
        private short ReadAnalog(uint port, uint index, uint id)
        {
            var d = port < 4 ? _ports[port].Device : null;
            if (d == null) return 0;
            if (index == 2)
                return id switch
                {
                    12u => _set.ReadAxis(d, SdlDeviceSet.AXIS_LTRIG),   // JOYPAD_L2
                    13u => _set.ReadAxis(d, SdlDeviceSet.AXIS_RTRIG),   // JOYPAD_R2
                    _   => (short)0
                };

            // EMUTASTIC_INPUT_DIAG: log right-stick magnitude reaching the core (the NDS emulated
            // pointer) so we can tell a dead pointer from a dead tap. Throttled to meaningful motion.
            if (_inputDiag && index == 1 && port == 0)
            {
                short ax = _set.ReadAxis(d, id == 0 ? SdlDeviceSet.AXIS_RIGHTX : SdlDeviceSet.AXIS_RIGHTY);
                if (Math.Abs(ax) > 8000 && (id != _rsLastId || Math.Abs(ax - _rsLastVal) > 6000))
                {
                    _rsLastId = id; _rsLastVal = ax;
                    ControllerDiagLog.Write($"[nds-touch] right-stick {(id==0?"X":"Y")} -> {ax} reaching core (pointer should move)");
                }
            }
            // Compose from the Controls panel's per-direction bindings when present
            // (slot order: LU, LD, LL, LR, RU, RD, RL, RR; id 0 = X → left/right pair,
            // id 1 = Y → up/down pair; libretro wants +X = right, +Y = down).
            var amap = _analogMap[(int)port];
            if (amap != null && index <= 1)
            {
                int slot   = (int)index * 4;
                int minus  = amap[slot + (id == 0 ? 2 : 0)];   // left / up
                int plus   = amap[slot + (id == 0 ? 3 : 1)];   // right / down
                if (minus >= 0 || plus >= 0)
                {
                    int v = HalfMagnitude(d, plus) - HalfMagnitude(d, minus);
                    return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                }
            }

            int axis = (index, id) switch
            {
                (0u, 0u) => SdlDeviceSet.AXIS_LEFTX,  (0u, 1u) => SdlDeviceSet.AXIS_LEFTY,
                (1u, 0u) => SdlDeviceSet.AXIS_RIGHTX, (1u, 1u) => SdlDeviceSet.AXIS_RIGHTY,
                _        => -1
            };
            if (axis < 0) return 0;
            return _set.ReadAxis(d, axis);
        }

        // Deflection magnitude (0..32767) of one bound direction: the matching half of a
        // stick axis (raw ids 110..117), trigger pressure (100/101), or a digital button
        // (0..20) at full scale. Unbound (-1) reads as 0.
        private short HalfMagnitude(SdlDeviceSet.Device d, int rawId)
        {
            switch (rawId)
            {
                case < 0:  return 0;
                case < SdlDeviceSet.BTN_COUNT: return _set.ReadButton(d, rawId) ? (short)32767 : (short)0;
                case SdlDeviceSet.RAW_L2: { short v = _set.ReadAxis(d, SdlDeviceSet.AXIS_LTRIG); return v > 0 ? v : (short)0; }
                case SdlDeviceSet.RAW_R2: { short v = _set.ReadAxis(d, SdlDeviceSet.AXIS_RTRIG); return v > 0 ? v : (short)0; }
                case >= SdlDeviceSet.RAW_STICK_FIRST and <= SdlDeviceSet.RAW_STICK_LAST:
                {
                    int axis  = (rawId - SdlDeviceSet.RAW_STICK_FIRST) / 2;        // LEFTX, LEFTY, RIGHTX, RIGHTY
                    bool neg  = ((rawId - SdlDeviceSet.RAW_STICK_FIRST) & 1) == 0; // even ids = negative half
                    int v     = _set.ReadAxis(d, axis);
                    if (neg) return v < 0 ? (short)Math.Min(-v, 32767) : (short)0;
                    return v > 0 ? (short)v : (short)0;
                }
                default:   return 0;
            }
        }

        public void Dispose()
        {
            _set.Dispose();
            foreach (var p in _ports) p.Device = null;
            if (_initialized) { SDL_QuitSubSystem(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD); _initialized = false; }
        }
    }
}
