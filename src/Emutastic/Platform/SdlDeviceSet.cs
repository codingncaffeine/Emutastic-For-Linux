using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// The one place SDL3 controllers are enumerated, opened, identified and read. Shared by the
    /// two readers in this port — <see cref="SdlInput"/> (the game session, in the game-host
    /// process) and <c>Services.ControllerManager</c> (the Preferences capture panel, in the
    /// library process) — so both see the same devices under the same ids and read them the same
    /// way. Before this, each kept its own <c>SDL_GetGamepads</c> list and the "which pad is
    /// player 2" question had a different answer in each.
    ///
    /// DEVICE IDENTITY
    /// ---------------
    /// <see cref="Device.Id"/> is <c>"product name#occurrence"</c> — the occurrence disambiguates
    /// two identical pads by SDL enumeration order ("Retrolink SNES controller#0" / "#1").
    /// That is the same key upstream Windows persists in <c>InputConfiguration.ControllerDeviceId</c>,
    /// so a config file moves between the two apps. SDL's joystick GUID is NOT used: it encodes
    /// vendor/product and is therefore identical for two units of the same model, which is
    /// exactly the case that needs telling apart. Unplugging the first of two identical pads
    /// renumbers the second — inherent to any index scheme, and what every other frontend does.
    ///
    /// JOYSTICKS WITHOUT A GAMEPAD MAPPING
    /// -----------------------------------
    /// This enumerates <c>SDL_GetJoysticks</c> — every joystick — not just <c>SDL_GetGamepads</c>.
    /// A pad SDL has no mapping for (cheap SNES/NES USB adapters, arcade sticks, most console
    /// adapters) has <see cref="Device.Gamepad"/> == Zero and is read positionally through the
    /// raw joystick API, using the conventional HID gamepad layout: buttons 0..3 = the face
    /// cluster, 4/5 = shoulders, 6/7 = select/start, 8/9 = stick clicks, hat 0 = d-pad, axes 0..3
    /// = the two sticks. It only has to be deterministic — users rebind in Preferences. Such a pad
    /// was previously invisible on Linux: not listed, not bindable, not playable.
    ///
    /// RAW CONTROL ID SPACE
    /// --------------------
    /// <see cref="ReadControl"/> takes the id space the Preferences panel stores in
    /// <c>ControllerMappings.InputIdentifier</c>: 0..20 = SDL_GamepadButton, 100/101 = L2/R2,
    /// 110..117 = stick halves (LX-,LX+,LY-,LY+,RX-,RX+,RY-,RY+). For an unmapped pad the
    /// gamepad button ids are translated to the positional layout above, so a capture in the panel
    /// and a read in the session agree, and the default RetroPad table works unchanged.
    ///
    /// AXIS CONVENTION
    /// ---------------
    /// SDL reports stick Y as down-positive; libretro's analog convention is also down-positive,
    /// so axes pass through unchanged — there is NO negation here, unlike the upstream Windows
    /// port which has to convert to XInput's up-positive.
    ///
    /// THREADING
    /// ---------
    /// Not thread-safe; every call is made on the thread that pumps SDL for that process (the
    /// emu thread in the game host, the UI timer in the library). SDL3's event pump is the
    /// owner's job — this class only reads.
    /// </summary>
    internal sealed class SdlDeviceSet : IDisposable
    {
        // ── P/Invoke ─────────────────────────────────────────────────────────────────────────
        // Entry-point names are not verified by the compiler: a typo builds green and throws
        // EntryPointNotFoundException at first use. Every name here was checked against SDL3's
        // SDL_joystick.h / SDL_gamepad.h. Note SDL_JoystickConnected — NOT SDL_GetJoystickConnected,
        // unlike its neighbours. SDL3 bools are 1-byte C99 bool, hence UnmanagedType.I1 on every
        // bool return (the default 4-byte BOOL marshalling reads garbage upper bits).

        [DllImport("SDL3")] static extern IntPtr SDL_GetJoysticks(out int count);
        [DllImport("SDL3")] static extern void   SDL_free(IntPtr mem);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_IsGamepad(uint instance_id);
        [DllImport("SDL3")] static extern IntPtr SDL_GetGamepadNameForID(uint instance_id);
        [DllImport("SDL3")] static extern IntPtr SDL_GetJoystickNameForID(uint instance_id);
        [DllImport("SDL3")] static extern IntPtr SDL_OpenJoystick(uint instance_id);
        [DllImport("SDL3")] static extern void   SDL_CloseJoystick(IntPtr joystick);
        [DllImport("SDL3")] static extern IntPtr SDL_OpenGamepad(uint instance_id);
        [DllImport("SDL3")] static extern void   SDL_CloseGamepad(IntPtr gamepad);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_JoystickConnected(IntPtr joystick);
        [DllImport("SDL3")] static extern int    SDL_GetNumJoystickButtons(IntPtr joystick);
        [DllImport("SDL3")] static extern int    SDL_GetNumJoystickAxes(IntPtr joystick);
        [DllImport("SDL3")] static extern int    SDL_GetNumJoystickHats(IntPtr joystick);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_GetJoystickButton(IntPtr joystick, int button);
        [DllImport("SDL3")] static extern short  SDL_GetJoystickAxis(IntPtr joystick, int axis);
        [DllImport("SDL3")] static extern byte   SDL_GetJoystickHat(IntPtr joystick, int hat);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
        [DllImport("SDL3")] static extern short  SDL_GetGamepadAxis(IntPtr gamepad, int axis);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_RumbleGamepad(IntPtr gamepad, ushort low, ushort high, uint duration_ms);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_RumbleJoystick(IntPtr joystick, ushort low, ushort high, uint duration_ms);

        // SDL_GamepadButton (header order). Only the ones the positional layout needs by name.
        public const int BTN_SOUTH = 0, BTN_EAST = 1, BTN_WEST = 2, BTN_NORTH = 3, BTN_BACK = 4, BTN_GUIDE = 5,
                         BTN_START = 6, BTN_LEFT_STICK = 7, BTN_RIGHT_STICK = 8, BTN_LEFT_SHOULDER = 9,
                         BTN_RIGHT_SHOULDER = 10, BTN_DPAD_UP = 11, BTN_DPAD_DOWN = 12, BTN_DPAD_LEFT = 13,
                         BTN_DPAD_RIGHT = 14, BTN_COUNT = 21;
        // SDL_GamepadAxis (header order).
        public const int AXIS_LEFTX = 0, AXIS_LEFTY = 1, AXIS_RIGHTX = 2, AXIS_RIGHTY = 3, AXIS_LTRIG = 4, AXIS_RTRIG = 5;
        // The panel's raw control id space.
        public const int RAW_L2 = 100, RAW_R2 = 101, RAW_STICK_FIRST = 110, RAW_STICK_LAST = 117;
        // Digital thresholds shared by both readers (were duplicated in each before).
        public const short STICK_THRESHOLD = 18000, TRIG_THRESHOLD = 12000;

        const byte SDL_HAT_UP = 0x01, SDL_HAT_RIGHT = 0x02, SDL_HAT_DOWN = 0x04, SDL_HAT_LEFT = 0x08;

        /// <summary>One open controller.</summary>
        public sealed class Device
        {
            public string Id          { get; internal set; } = "";
            public string Name        { get; internal set; } = "";
            /// <summary>Name, suffixed " (2)", " (3)" … for the second, third … identical pad.</summary>
            public string DisplayName { get; internal set; } = "";
            public uint   InstanceId  { get; internal set; }
            /// <summary>Always open.</summary>
            public IntPtr Joystick    { get; internal set; }
            /// <summary>Open only when SDL has a gamepad mapping for the device; Zero otherwise.</summary>
            public IntPtr Gamepad     { get; internal set; }
            public bool   IsGamepad   => Gamepad != IntPtr.Zero;
            public int    NumButtons  { get; internal set; }
            public int    NumAxes     { get; internal set; }
            public int    NumHats     { get; internal set; }
            public override string ToString() => Id;
        }

        private readonly List<Device> _devices = new();                                // SDL enumeration order
        private readonly Dictionary<string, Device> _byId = new(StringComparer.Ordinal);

        /// <summary>Open devices, in SDL enumeration order. Rebuilt by <see cref="Reconcile"/>.</summary>
        public IReadOnlyList<Device> Devices => _devices;

        public Device? Get(string? id) =>
            !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var d) ? d : null;

        public static string MakeId(string name, int occurrence) => $"{name}#{occurrence}";

        /// <summary>
        /// Enumerates every joystick, opens new ones, closes departed ones, and re-labels ids so the
        /// occurrence numbers stay dense. Returns true when the device set changed. A device that is
        /// merely re-labelled (its "#n" shifted because an identical pad in front of it left) keeps
        /// its <see cref="Device"/> object and handles — callers holding the object follow the
        /// physical pad, callers holding an id string follow the label. Both are intended.
        /// </summary>
        public bool Reconcile(List<Device>? added = null, List<Device>? removed = null)
        {
            var present = new List<(uint InstanceId, string Name)>();
            IntPtr arr = SDL_GetJoysticks(out int count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    uint id = (uint)Marshal.ReadInt32(arr, i * 4);
                    IntPtr namePtr = SDL_IsGamepad(id) ? SDL_GetGamepadNameForID(id) : SDL_GetJoystickNameForID(id);
                    present.Add((id, Marshal.PtrToStringUTF8(namePtr) ?? $"Controller {i + 1}"));
                }
            }
            finally { if (arr != IntPtr.Zero) SDL_free(arr); }

            bool changed = false;

            // Close departed (match by SDL instance id, which is unique per connection).
            var presentIds = new HashSet<uint>();
            foreach (var p in present) presentIds.Add(p.InstanceId);
            for (int i = _devices.Count - 1; i >= 0; i--)
            {
                var d = _devices[i];
                if (presentIds.Contains(d.InstanceId) && SDL_JoystickConnected(d.Joystick)) continue;
                Close(d);
                _devices.RemoveAt(i);
                removed?.Add(d);
                changed = true;
            }

            // Open new, and rebuild the ordered list + ids in the fresh enumeration order.
            var byInstance = new Dictionary<uint, Device>();
            foreach (var d in _devices) byInstance[d.InstanceId] = d;
            var fresh = new List<Device>(present.Count);
            var seen  = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (instanceId, name) in present)
            {
                if (!byInstance.TryGetValue(instanceId, out var d))
                {
                    d = Open(instanceId, name);
                    if (d == null) continue;
                    added?.Add(d);
                    changed = true;
                }
                seen.TryGetValue(name, out int n);
                seen[name] = n + 1;
                string newId = MakeId(name, n);
                if (d.Id != newId) { d.Id = newId; d.DisplayName = n == 0 ? name : $"{name} ({n + 1})"; changed = true; }
                fresh.Add(d);
            }

            _devices.Clear(); _devices.AddRange(fresh);
            _byId.Clear();
            foreach (var d in _devices) _byId[d.Id] = d;
            return changed;
        }

        private static Device? Open(uint instanceId, string name)
        {
            IntPtr joy = SDL_OpenJoystick(instanceId);
            if (joy == IntPtr.Zero)
            {
                Services.ControllerDiagLog.Write($"[devices] SDL_OpenJoystick FAILED for id={instanceId} \"{name}\"");
                return null;
            }
            IntPtr pad = SDL_IsGamepad(instanceId) ? SDL_OpenGamepad(instanceId) : IntPtr.Zero;
            var d = new Device
            {
                Name = name, InstanceId = instanceId, Joystick = joy, Gamepad = pad,
                NumButtons = SDL_GetNumJoystickButtons(joy),
                NumAxes    = SDL_GetNumJoystickAxes(joy),
                NumHats    = SDL_GetNumJoystickHats(joy),
            };
            Services.ControllerDiagLog.Write(
                $"[devices] Opened id={instanceId} \"{name}\" {(d.IsGamepad ? "gamepad-mapped" : "RAW joystick (no SDL mapping)")} " +
                $"buttons={d.NumButtons} axes={d.NumAxes} hats={d.NumHats}");
            return d;
        }

        private static void Close(Device d)
        {
            Services.ControllerDiagLog.Write($"[devices] Closed \"{d.Id}\"");
            try { if (d.Gamepad  != IntPtr.Zero) SDL_CloseGamepad(d.Gamepad); }   catch { }
            try { if (d.Joystick != IntPtr.Zero) SDL_CloseJoystick(d.Joystick); } catch { }
            d.Gamepad = IntPtr.Zero; d.Joystick = IntPtr.Zero;
        }

        // ── Reading ──────────────────────────────────────────────────────────────────────────

        /// <summary>Digital read of one raw control id (see class remarks for the id space).</summary>
        public bool ReadControl(Device d, int rawId)
        {
            if (rawId < 0) return false;
            if (rawId < BTN_COUNT) return ReadButton(d, rawId);
            switch (rawId)
            {
                case RAW_L2: return ReadAxis(d, AXIS_LTRIG) > TRIG_THRESHOLD;
                case RAW_R2: return ReadAxis(d, AXIS_RTRIG) > TRIG_THRESHOLD;
                case >= RAW_STICK_FIRST and <= RAW_STICK_LAST:
                {
                    int axis = (rawId - RAW_STICK_FIRST) / 2;          // LEFTX, LEFTY, RIGHTX, RIGHTY
                    bool neg = ((rawId - RAW_STICK_FIRST) & 1) == 0;   // even = negative half
                    short v = ReadAxis(d, axis);
                    return neg ? v < -STICK_THRESHOLD : v > STICK_THRESHOLD;
                }
                default: return false;
            }
        }

        /// <summary>A gamepad button by SDL_GamepadButton index — translated positionally for a raw pad.</summary>
        public bool ReadButton(Device d, int gamepadButton)
        {
            if (d.IsGamepad) return SDL_GetGamepadButton(d.Gamepad, gamepadButton);
            return ReadRawButton(d, gamepadButton);
        }

        /// <summary>
        /// A gamepad axis (0..5) in SDL's -32768..32767 range. Raw pad: axes 0..3 are the sticks;
        /// the trigger axes are driven digitally (0 / 32767) from the spare buttons, never guessed
        /// from a physical axis — a raw pad has no documented axis order, and a stick axis mistaken
        /// for a trigger sits half-pressed at rest, which is worse than an absent trigger.
        /// </summary>
        public short ReadAxis(Device d, int axis)
        {
            if (d.IsGamepad) return SDL_GetGamepadAxis(d.Gamepad, axis);
            switch (axis)
            {
                case AXIS_LEFTX: case AXIS_LEFTY: case AXIS_RIGHTX: case AXIS_RIGHTY:
                    return axis < d.NumAxes ? SDL_GetJoystickAxis(d.Joystick, axis) : (short)0;
                case AXIS_LTRIG: return RawBtn(d, RawSpareL2) ? (short)32767 : (short)0;
                case AXIS_RTRIG: return RawBtn(d, RawSpareR2) ? (short)32767 : (short)0;
                default: return 0;
            }
        }

        // Positional layout for a pad without an SDL mapping. Physical button indices:
        //   0..3 face (SOUTH, EAST, WEST, NORTH), 4/5 shoulders, 6/7 select/start, 8/9 stick clicks.
        // Spare buttons from 10 up are claimed in a fixed order — TRIGGERS FIRST, because L2/R2 are
        // only two slots, always land, and are the least damaging default for an unlabelled extra
        // button; without them no raw pad could ever bind L2/R2. The d-pad comes from hat 0 when
        // there is one; from the next spare buttons ONLY when the pad reports neither a hat nor any
        // axes. "No hat" alone is not enough: the cheap SNES/NES adapters this path exists for report
        // no hat and put the d-pad on axes 0/1 (already read as the left stick), and stealing buttons
        // for a d-pad there invents phantom Up/Down from whatever those buttons really are.
        const int RawSpareL2 = 10, RawSpareR2 = 11, RawSpareDpad = 12;

        private static bool RawBtn(Device d, int physical) =>
            physical >= 0 && physical < d.NumButtons && SDL_GetJoystickButton(d.Joystick, physical);

        private static bool ReadRawButton(Device d, int gamepadButton)
        {
            switch (gamepadButton)
            {
                case BTN_SOUTH:          return RawBtn(d, 0);
                case BTN_EAST:           return RawBtn(d, 1);
                case BTN_WEST:           return RawBtn(d, 2);
                case BTN_NORTH:          return RawBtn(d, 3);
                case BTN_LEFT_SHOULDER:  return RawBtn(d, 4);
                case BTN_RIGHT_SHOULDER: return RawBtn(d, 5);
                case BTN_BACK:           return RawBtn(d, 6);
                case BTN_START:          return RawBtn(d, 7);
                case BTN_LEFT_STICK:     return RawBtn(d, 8);
                case BTN_RIGHT_STICK:    return RawBtn(d, 9);
                case BTN_DPAD_UP:    case BTN_DPAD_DOWN:
                case BTN_DPAD_LEFT:  case BTN_DPAD_RIGHT:
                {
                    if (d.NumHats > 0)
                    {
                        byte hat = SDL_GetJoystickHat(d.Joystick, 0);
                        return gamepadButton switch
                        {
                            BTN_DPAD_UP    => (hat & SDL_HAT_UP)    != 0,
                            BTN_DPAD_DOWN  => (hat & SDL_HAT_DOWN)  != 0,
                            BTN_DPAD_LEFT  => (hat & SDL_HAT_LEFT)  != 0,
                            _              => (hat & SDL_HAT_RIGHT) != 0,
                        };
                    }
                    if (d.NumAxes == 0)
                        return RawBtn(d, RawSpareDpad + (gamepadButton - BTN_DPAD_UP));
                    return false;   // d-pad is on the axes — the stick-half ids (110..113) read it
                }
                case BTN_GUIDE:          return false;
                default:
                    // MISC1, paddles, touchpad … (15..20): the spare tail after the d-pad block, if
                    // the pad has that many buttons. Deterministic, and bindable in Preferences.
                    return RawBtn(d, RawSpareDpad + 4 + (gamepadButton - 15));
            }
        }

        public void Rumble(Device d, ushort low, ushort high, uint durationMs)
        {
            try
            {
                if (d.IsGamepad) SDL_RumbleGamepad(d.Gamepad, low, high, durationMs);
                else             SDL_RumbleJoystick(d.Joystick, low, high, durationMs);
            }
            catch { }
        }

        public void Dispose()
        {
            foreach (var d in _devices) Close(d);
            _devices.Clear();
            _byId.Clear();
        }
    }
}
