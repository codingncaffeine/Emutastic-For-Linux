using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace Emutastic.Services
{
    /// <summary>
    /// Player 1's keyboard: the keys built into the game, with the binds saved in
    /// Preferences → Controls laid over them. The game host, the in-process game window and
    /// Preferences all resolve through <see cref="Resolve"/>, so the key a row shows is the
    /// key that plays it. Same resolution as the Windows build; the built-in keys are this
    /// build's own (they predate the binds and stay as they were).
    ///
    /// Targets are <see cref="LibretroInput"/> ids: 0-15 RetroPad buttons, 16-23 stick
    /// directions.
    ///
    /// A saved bind takes its key away from any built-in use, and retires the built-in keys
    /// of the button it binds: rebinding B from Z to Space leaves Z doing nothing, as in any
    /// other emulator. Buttons nobody rebound keep their built-in keys.
    /// </summary>
    public static class KeyboardBindings
    {
        // The built-in keys, in the order a row lists them (a row shows the first key that
        // reaches its target).
        private static readonly Key[] BuiltInOrder =
        {
            Key.Up, Key.Down, Key.Left, Key.Right,
            Key.Z, Key.X, Key.A, Key.S,
            Key.Enter, Key.RightShift, Key.Q, Key.W,
        };

        /// <summary>The target <paramref name="key"/> presses when nothing is bound over it,
        /// or <c>uint.MaxValue</c> for a key with no built-in use.</summary>
        public static uint BuiltInTarget(Key key) => key switch
        {
            Key.Up         => LibretroInput.JOYPAD_UP,
            Key.Down       => LibretroInput.JOYPAD_DOWN,
            Key.Left       => LibretroInput.JOYPAD_LEFT,
            Key.Right      => LibretroInput.JOYPAD_RIGHT,
            Key.Z          => LibretroInput.JOYPAD_B,
            Key.X          => LibretroInput.JOYPAD_A,
            Key.A          => LibretroInput.JOYPAD_Y,
            Key.S          => LibretroInput.JOYPAD_X,
            Key.Enter      => LibretroInput.JOYPAD_START,
            Key.RightShift => LibretroInput.JOYPAD_SELECT,
            Key.Q          => LibretroInput.JOYPAD_L,
            Key.W          => LibretroInput.JOYPAD_R,
            _              => uint.MaxValue,
        };

        /// <summary>True for a RetroPad button or a stick direction.</summary>
        public static bool IsTarget(uint id) => id <= LibretroInput.ANALOG_RIGHT_RIGHT;

        /// <summary>
        /// Every key that plays <paramref name="console"/>, with the target it presses: the
        /// binds first, in their order, then the built-in keys that are still free. A key bound
        /// to two buttons appears twice. <paramref name="binds"/> are button names from the
        /// controller definition with the key bound to each; names that reach no target (the
        /// frontend chords) are skipped.
        /// </summary>
        public static List<(Key Key, uint Target)> Resolve(
            string console, IEnumerable<(string ButtonName, Key Key)> binds)
        {
            var result = new List<(Key Key, uint Target)>();
            var boundKeys = new HashSet<Key>();
            var rebound = new HashSet<uint>();
            foreach (var (buttonName, key) in binds)
            {
                if (key == Key.None) continue;
                uint target = LibretroInput.GetButtonId(buttonName, console);
                if (!IsTarget(target)) continue;
                if (!result.Contains((key, target))) result.Add((key, target));
                boundKeys.Add(key);
                rebound.Add(target);
            }
            foreach (var key in BuiltInOrder)
            {
                uint target = BuiltInTarget(key);
                if (boundKeys.Contains(key) || rebound.Contains(target)) continue;
                result.Add((key, target));
            }
            return result;
        }

        /// <summary>
        /// A saved keyboard list as binds: the identifiers that name a key. Chords ("A+B", the
        /// frontend rows) are not button binds and are left out.
        /// </summary>
        public static IEnumerable<(string ButtonName, Key Key)> SavedBinds(
            IEnumerable<Configuration.ButtonMapping> saved)
        {
            foreach (var m in saved)
            {
                string id = m.InputIdentifier ?? "";
                if (id.Length == 0 || id.Contains('+') || char.IsDigit(id[0])) continue;
                if (Enum.TryParse<Key>(id, out var key) && key != Key.None)
                    yield return (m.ButtonName, key);
            }
        }

        // ── The game host: SDL key codes ─────────────────────────────────────────────────

        /// <summary>Marks a raw SDL scancode in a key map, beside SDL keycodes. SDL keycodes are
        /// code points or scancode | 0x40000000, so this bit never collides with one.</summary>
        public const int RawScancode = 0x20000000;

        /// <summary>
        /// <see cref="Resolve"/> as the game host reads it. Each key is entered twice: by its SDL
        /// keycode (the key with that label on the player's layout — what SDL's window reports)
        /// and by its US-layout scancode | <see cref="RawScancode"/> (for a window that reports
        /// only physical keys).
        /// </summary>
        public static Dictionary<int, uint[]> ToSdlMap(List<(Key Key, uint Target)> resolved)
        {
            var lists = new Dictionary<int, List<uint>>();
            void Add(int code, uint target)
            {
                if (!lists.TryGetValue(code, out var list)) lists[code] = list = new List<uint>();
                if (!list.Contains(target)) list.Add(target);
            }
            foreach (var (key, target) in resolved)
            {
                if (!SdlCodes(key, out int scancode, out int keycode)) continue;
                Add(keycode, target);
                Add(RawScancode | scancode, target);
            }
            var map = new Dictionary<int, uint[]>(lists.Count);
            foreach (var kv in lists) map[kv.Key] = kv.Value.ToArray();
            return map;
        }

        /// <summary>
        /// The SDL scancode of <paramref name="key"/> on a US layout, and its SDL keycode (a
        /// character key's lower-case character, else scancode | 0x40000000). False for a key
        /// SDL has no code for.
        /// </summary>
        public static bool SdlCodes(Key key, out int scancode, out int keycode)
        {
            scancode = -1; keycode = -1;
            if (key >= Key.A && key <= Key.Z)
            {
                scancode = 4 + (key - Key.A);
                keycode = 'a' + (key - Key.A);
                return true;
            }
            if (key >= Key.D0 && key <= Key.D9)
            {
                scancode = key == Key.D0 ? 39 : 30 + (key - Key.D1);
                keycode = '0' + (key - Key.D0);
                return true;
            }
            if (key >= Key.F1 && key <= Key.F12)
            {
                scancode = 58 + (key - Key.F1);
                keycode = 0x40000000 | scancode;
                return true;
            }
            if (key >= Key.NumPad1 && key <= Key.NumPad9)
            {
                scancode = 89 + (key - Key.NumPad1);
                keycode = 0x40000000 | scancode;
                return true;
            }
            (scancode, keycode) = key switch
            {
                Key.Enter            => (40, 0x0D),
                Key.Escape           => (41, 0x1B),
                Key.Back             => (42, 0x08),
                Key.Tab              => (43, 0x09),
                Key.Space            => (44, (int)' '),
                Key.OemMinus         => (45, (int)'-'),
                Key.OemPlus          => (46, (int)'='),
                Key.OemOpenBrackets  => (47, (int)'['),
                Key.OemCloseBrackets => (48, (int)']'),
                Key.OemPipe          => (49, (int)'\\'),
                Key.OemSemicolon     => (51, (int)';'),
                Key.OemQuotes        => (52, (int)'\''),
                Key.OemTilde         => (53, (int)'`'),
                Key.OemComma         => (54, (int)','),
                Key.OemPeriod        => (55, (int)'.'),
                Key.OemQuestion      => (56, (int)'/'),
                Key.Delete           => (76, 0x7F),
                Key.NumPad0          => (98, 0x40000000 | 98),
                _ => (-1, -1),
            };
            if (scancode >= 0) return true;
            scancode = key switch
            {
                Key.CapsLock => 57, Key.PrintScreen => 70, Key.Scroll => 71, Key.Pause => 72,
                Key.Insert => 73, Key.Home => 74, Key.PageUp => 75, Key.End => 77, Key.PageDown => 78,
                Key.Right => 79, Key.Left => 80, Key.Down => 81, Key.Up => 82,
                Key.NumLock => 83, Key.Divide => 84, Key.Multiply => 85, Key.Subtract => 86,
                Key.Add => 87, Key.Decimal => 99, Key.OemBackslash => 100,
                Key.LeftCtrl => 224, Key.LeftShift => 225, Key.LeftAlt => 226, Key.LWin => 227,
                Key.RightCtrl => 228, Key.RightShift => 229, Key.RightAlt => 230, Key.RWin => 231,
                _ => -1,
            };
            if (scancode < 0) return false;
            keycode = 0x40000000 | scancode;
            return true;
        }
    }

    /// <summary>
    /// Player 1's keyboard pad: which targets are held, and the stick positions they make.
    /// Fed every key-down and key-up by a code from <see cref="KeyboardBindings.ToSdlMap"/>; a
    /// target stays pressed while any of its keys is down, and a stick direction pair held
    /// together cancels out. Written by whichever window has the keyboard, read by the emu
    /// thread.
    /// </summary>
    public sealed class KeyboardPad
    {
        private const short Full = 32767;

        private Dictionary<int, uint[]> _map = new();
        private readonly HashSet<int> _held = new();
        private readonly int[] _holds = new int[LibretroInput.ANALOG_RIGHT_RIGHT + 1];

        /// <summary>RetroPad buttons 0-15.</summary>
        public bool[] Buttons { get; } = new bool[16];

        // libretro convention: up and left negative, down and right positive.
        public short LeftX  { get; private set; }
        public short LeftY  { get; private set; }
        public short RightX { get; private set; }
        public short RightY { get; private set; }

        /// <summary>Installs a new key map and lets go of everything held — a key held across
        /// a rebind never saw its release here.</summary>
        public void SetMap(Dictionary<int, uint[]> map)
        {
            _map = map;
            ReleaseAll();
        }

        public void ReleaseAll()
        {
            _held.Clear();
            Array.Clear(_holds);
            Array.Clear(Buttons);
            LeftX = LeftY = RightX = RightY = 0;
        }

        /// <summary>A key went down or up. A second down of a key already held (auto-repeat)
        /// changes nothing. Returns whether the key is one the map uses.</summary>
        public bool Set(int code, bool pressed)
        {
            if (!_map.TryGetValue(code, out var targets)) return false;
            if (pressed ? !_held.Add(code) : !_held.Remove(code)) return true;
            foreach (uint t in targets)
            {
                if (t >= (uint)_holds.Length) continue;
                _holds[t] = Math.Max(0, _holds[t] + (pressed ? 1 : -1));
                if (t < (uint)Buttons.Length) Buttons[t] = _holds[t] > 0;
            }
            LeftX  = Axis(LibretroInput.ANALOG_LEFT_LEFT,  LibretroInput.ANALOG_LEFT_RIGHT);
            LeftY  = Axis(LibretroInput.ANALOG_LEFT_UP,    LibretroInput.ANALOG_LEFT_DOWN);
            RightX = Axis(LibretroInput.ANALOG_RIGHT_LEFT, LibretroInput.ANALOG_RIGHT_RIGHT);
            RightY = Axis(LibretroInput.ANALOG_RIGHT_UP,   LibretroInput.ANALOG_RIGHT_DOWN);
            return true;
        }

        private short Axis(uint negative, uint positive) =>
            (short)((_holds[positive] > 0 ? Full : 0) - (_holds[negative] > 0 ? Full : 0));
    }
}
