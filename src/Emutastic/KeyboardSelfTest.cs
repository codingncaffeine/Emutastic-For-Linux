using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Emutastic.Configuration;
using Emutastic.Platform;
using Emutastic.Services;

namespace Emutastic
{
    /// <summary>
    /// <c>Emutastic --selftest-keyboard [report.log]</c>: player 1's keyboard, from the Preferences
    /// rows to what a core reads. No window, never touches the user's configuration.
    ///
    /// 1. <see cref="KeyboardBindings"/>: with nothing bound the keys are exactly the old fixed keys;
    ///    a bind moves its button off its old key; a key taken for another button leaves its old
    ///    button with none; stick rows bind; every row of every console reaches a button or stick
    ///    direction.
    /// 2. The SDL codes the game host matches keys by: each keycode is the one SDL itself reports
    ///    for that key (US layout), and the Wayland window's key table delivers every bindable key.
    /// 3. <see cref="KeyboardPad"/>: two keys on one button, auto-repeat, opposite stick directions.
    /// 4. <see cref="SdlInput"/> with no pad attached: a console's saved binds loaded through
    ///    LoadConfiguration, keys pressed by keycode (the SDL window) and by scancode (the Wayland
    ///    window), read back through GetInputState — the call the core makes. Before, the game
    ///    host's window used a fixed key table and never read the binds at all.
    ///
    /// Exit code 0 = every check passed, 1 = a check failed.
    /// </summary>
    internal static class KeyboardSelfTest
    {
        static int _pass, _fail;
        static StreamWriter? _report;

        static void Line(string s)
        {
            Console.WriteLine(s);
            _report?.WriteLine(s);
        }

        static void Check(bool ok, string what)
        {
            if (ok) _pass++; else _fail++;
            Line($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        }

        public static int Run(string? reportPath = null)
        {
            _pass = _fail = 0;
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                try { _report = new StreamWriter(Path.GetFullPath(reportPath), append: false) { AutoFlush = true }; }
                catch (Exception ex) { Console.WriteLine($"cannot open report '{reportPath}': {ex.Message}"); }
            }
            Line("=== Keyboard self-test ===");
            try
            {
                BindingChecks();
                SdlCodeChecks();
                PadChecks();
                InputChecks();
                WindowChecks();
            }
            catch (Exception ex)
            {
                Line($"  [FAIL] unhandled: {ex}");
                _fail++;
            }
            Line(_fail == 0 ? $"=== PASS ({_pass}) ===" : $"=== FAIL ({_fail} of {_pass + _fail}) ===");
            _report?.Dispose();
            return _fail == 0 ? 0 : 1;
        }

        // ── 1. KeyboardBindings ───────────────────────────────────────────────────────

        static List<(Key Key, uint Target)> Resolve(string console, params (string, Key)[] binds) =>
            KeyboardBindings.Resolve(console, binds);

        static bool Only(List<(Key Key, uint Target)> r, Key key, uint target) =>
            r.Count(x => x.Key == key) == 1 && r.Contains((key, target));

        static void BindingChecks()
        {
            Line("-- KeyboardBindings --");
            // The fixed keys the game windows used before binds reached them (the game host's
            // _glKeyMap and EmulatorWindow.KeyMap), which stay the defaults.
            var old = new Dictionary<Key, uint>
            {
                [Key.Up] = LibretroInput.JOYPAD_UP,     [Key.Down] = LibretroInput.JOYPAD_DOWN,
                [Key.Left] = LibretroInput.JOYPAD_LEFT, [Key.Right] = LibretroInput.JOYPAD_RIGHT,
                [Key.Z] = LibretroInput.JOYPAD_B,       [Key.X] = LibretroInput.JOYPAD_A,
                [Key.A] = LibretroInput.JOYPAD_Y,       [Key.S] = LibretroInput.JOYPAD_X,
                [Key.Enter] = LibretroInput.JOYPAD_START, [Key.RightShift] = LibretroInput.JOYPAD_SELECT,
                [Key.Q] = LibretroInput.JOYPAD_L,       [Key.W] = LibretroInput.JOYPAD_R,
            };
            string Diff(List<(Key Key, uint Target)> r, Dictionary<Key, uint> want)
            {
                var d = want.Where(kv => !Only(r, kv.Key, kv.Value)).Select(kv => $"{kv.Key}").ToList();
                d.AddRange(r.Where(x => !want.ContainsKey(x.Key)).Select(x => $"{x.Key}: unexpected"));
                return string.Join(", ", d);
            }
            string snes = Diff(Resolve("SNES"), old);
            Check(snes.Length == 0, $"nothing bound: the old fixed keys {snes}");
            var planted = new Dictionary<Key, uint>(old) { [Key.S] = LibretroInput.JOYPAD_Y };
            Check(Diff(Resolve("SNES"), planted).Length > 0, "control: a table with one key changed is reported different");

            var r1 = Resolve("SNES", ("B", Key.Space));
            Check(Only(r1, Key.Space, LibretroInput.JOYPAD_B) && !r1.Any(x => x.Key == Key.Z)
                  && Only(r1, Key.X, LibretroInput.JOYPAD_A),
                "B bound to Space: Space presses B, Z (B's old key) is free, X still presses A");

            var r2 = Resolve("SNES", ("Start", Key.Z));
            Check(Only(r2, Key.Z, LibretroInput.JOYPAD_START) && !r2.Any(x => x.Key == Key.Enter)
                  && !r2.Any(x => x.Target == LibretroInput.JOYPAD_B),
                "Start bound to Z: Z presses only Start, Enter is free, B has no key");

            var r3 = Resolve("SNES", ("A", Key.K), ("B", Key.K));
            Check(r3.Contains((Key.K, LibretroInput.JOYPAD_A)) && r3.Contains((Key.K, LibretroInput.JOYPAD_B))
                  && !r3.Any(x => x.Key == Key.X || x.Key == Key.Z),
                "one key bound to two buttons presses both; their old keys are free");

            var r4 = Resolve("N64", ("Analog Up", Key.T), ("C Up", Key.Y));
            Check(Only(r4, Key.T, LibretroInput.ANALOG_LEFT_UP) && Only(r4, Key.Y, LibretroInput.ANALOG_RIGHT_UP),
                "N64 stick rows bind: T is Analog Up, Y is C Up");

            var saved = new List<ButtonMapping>
            {
                new() { ButtonName = "Up",        InputIdentifier = "W" },
                new() { ButtonName = "Disk Swap", InputIdentifier = "Enter+RightShift" },
                new() { ButtonName = "A",         InputIdentifier = "8" },
                new() { ButtonName = "Y",         InputIdentifier = "Oem4" },   // OemOpenBrackets saves as its alias
            };
            var binds = KeyboardBindings.SavedBinds(saved).ToList();
            Check(binds.Count == 2 && binds[0] == ("Up", Key.W) && binds[1] == ("Y", Key.OemOpenBrackets),
                "saved list: key names (aliases too) are binds; a chord and a stray number are not");

            var known = new Dictionary<string, string>
            {
                ["PS3"] = "RPCS3 reads its own pad config, not these binds",
                ["CDi:Analog"] = "analog left out on purpose: the core thresholds the stick itself",
                ["3DO:Left Analog"] = "the 3DO pad is digital; the translator has no stick",
                ["Jaguar:1"] = "keypad digit not mapped yet", ["Jaguar:2"] = "keypad digit not mapped yet",
                ["Jaguar:3"] = "keypad digit not mapped yet",
                ["PSP:Home"] = "not mapped yet",
            };
            string? Known(string console, string name) =>
                known.TryGetValue(console, out var why) || known.TryGetValue($"{console}:{name}", out why)
                || known.TryGetValue($"{console}:{string.Join(' ', name.Split(' ').SkipLast(1))}", out why)
                    ? why : null;
            var dead = new List<string>();
            foreach (var (console, def) in ControllerDefinitions.AllControllers)
                foreach (var b in def.Buttons)
                {
                    if (KeyboardBindings.IsTarget(LibretroInput.GetButtonId(b.Name, console))) continue;
                    if (Known(console, b.Name) is string why) Line($"  [INFO] {console}:{b.Name} reaches nothing — {why}");
                    else dead.Add($"{console}:{b.Name}");
                }
            Check(dead.Count == 0, "every other row of every console reaches a button or stick direction"
                + (dead.Count > 0 ? $" — these reach nothing: {string.Join(", ", dead)}" : ""));
        }

        // ── 2. SDL codes ──────────────────────────────────────────────────────────────

        static readonly Key[] Bindable = Enumerable.Range((int)Key.A, 26).Select(i => (Key)i)
            .Concat(Enumerable.Range((int)Key.D0, 10).Select(i => (Key)i))
            .Concat(Enumerable.Range((int)Key.F1, 12).Select(i => (Key)i))
            .Concat(Enumerable.Range((int)Key.NumPad0, 10).Select(i => (Key)i))
            .Concat(new[]
            {
                Key.Up, Key.Down, Key.Left, Key.Right, Key.Enter, Key.Space, Key.Tab, Key.Back, Key.Escape,
                Key.OemMinus, Key.OemPlus, Key.OemOpenBrackets, Key.OemCloseBrackets, Key.OemPipe,
                Key.OemSemicolon, Key.OemQuotes, Key.OemTilde, Key.OemComma, Key.OemPeriod, Key.OemQuestion,
                Key.CapsLock, Key.Insert, Key.Delete, Key.Home, Key.End, Key.PageUp, Key.PageDown,
                Key.Divide, Key.Multiply, Key.Subtract, Key.Add, Key.Decimal,
                Key.LeftShift, Key.RightShift, Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt,
            }).ToArray();

        static void SdlCodeChecks()
        {
            Line("-- SDL codes --");
            var noCode = Bindable.Where(k => !KeyboardBindings.SdlCodes(k, out _, out _)).ToList();
            Check(noCode.Count == 0, $"every bindable key has an SDL code{(noCode.Count > 0 ? $" — missing: {string.Join(", ", noCode)}" : "")}");

            // SDL's own answer for each key: the keycode its window reports for the scancode, on
            // the default (US) keymap it uses before any window exists.
            var wrong = new List<string>();
            foreach (var k in Bindable)
            {
                if (!KeyboardBindings.SdlCodes(k, out int sc, out int kc)) continue;
                int sdl = (int)Gl.SDL_GetKeyFromScancode(sc, 0, false);
                if (sdl != kc) wrong.Add($"{k}: table 0x{kc:X}, SDL 0x{sdl:X}");
            }
            Check(wrong.Count == 0, $"each key's keycode is the one SDL reports for its scancode ({Bindable.Length} keys)"
                + (wrong.Count > 0 ? $" — differ: {string.Join("; ", wrong.Take(12))}" : ""));

            var delivered = new HashSet<int>(WlToplevelPresenter.EvdevToScancode.Values);
            var lost = Bindable.Where(k => KeyboardBindings.SdlCodes(k, out int sc, out _) && !delivered.Contains(sc)).ToList();
            Check(lost.Count == 0, $"the Wayland window's key table delivers every bindable key{(lost.Count > 0 ? $" — lost: {string.Join(", ", lost)}" : "")}");

            var map = KeyboardBindings.ToSdlMap(Resolve("SNES", ("B", Key.Space)));
            KeyboardBindings.SdlCodes(Key.Space, out int spaceSc, out int spaceKc);
            Check(map.TryGetValue(spaceKc, out var a) && a.SequenceEqual(new[] { LibretroInput.JOYPAD_B })
                  && map.TryGetValue(KeyboardBindings.RawScancode | spaceSc, out var b) && b.SequenceEqual(a),
                "the game host's map holds each key by keycode and by scancode");
        }

        // ── 3. KeyboardPad ────────────────────────────────────────────────────────────

        static int Code(Key k) { KeyboardBindings.SdlCodes(k, out _, out int kc); return kc; }

        static void PadChecks()
        {
            Line("-- KeyboardPad --");
            var pad = new KeyboardPad();
            pad.SetMap(KeyboardBindings.ToSdlMap(Resolve("SNES", ("Up", Key.Up), ("Up", Key.I))));
            pad.Set(Code(Key.Up), true); pad.Set(Code(Key.I), true); pad.Set(Code(Key.Up), false);
            bool held = pad.Buttons[LibretroInput.JOYPAD_UP];
            pad.Set(Code(Key.I), false);
            Check(held && !pad.Buttons[LibretroInput.JOYPAD_UP],
                "Up bound to two keys stays down until the last of the two lets go");

            pad.Set(Code(Key.Z), true); pad.Set(Code(Key.Z), true); pad.Set(Code(Key.Z), false);
            Check(!pad.Buttons[LibretroInput.JOYPAD_B], "a second down of a held key is one press: one release lets go");

            pad.SetMap(KeyboardBindings.ToSdlMap(Resolve("N64", ("Analog Up", Key.T), ("Analog Down", Key.G))));
            pad.Set(Code(Key.T), true);
            short up = pad.LeftY;
            pad.Set(Code(Key.G), true);
            short both = pad.LeftY;
            pad.Set(Code(Key.T), false);
            short down = pad.LeftY;
            pad.Set(Code(Key.G), false);
            Check(up == -32767 && both == 0 && down == 32767 && pad.LeftY == 0,
                $"N64 stick: Analog Up (-32767), both (0), Analog Down (+32767) — got {up}/{both}/{down}/{pad.LeftY}");

            pad.Set(Code(Key.X), true);
            pad.SetMap(KeyboardBindings.ToSdlMap(Resolve("SNES")));
            pad.Set(Code(Key.X), false);
            Check(pad.Buttons.All(x => !x), "a new map lets go of every key held");
        }

        // ── 4. SdlInput, as the core reads it ─────────────────────────────────────────

        static void InputChecks()
        {
            Line("-- SdlInput (no pad) --");
            var configs = new Dictionary<string, InputConfiguration>
            {
                ["SNES_P1"] = new()
                {
                    ConsoleName = "SNES_P1",
                    KeyboardMappings = { new ButtonMapping { ButtonName = "B", InputIdentifier = "Space", DisplayName = "Space" } },
                },
                ["N64_P1"] = new()
                {
                    ConsoleName = "N64_P1",
                    KeyboardMappings = { new ButtonMapping { ButtonName = "Analog Up", InputIdentifier = "T", DisplayName = "T" } },
                },
            };
            InputConfiguration Lookup(string key) =>
                configs.TryGetValue(key, out var c) ? c : new InputConfiguration { ConsoleName = key };
            const uint JOYPAD = SdlInput.RETRO_DEVICE_JOYPAD, ANALOG = SdlInput.RETRO_DEVICE_ANALOG;

            var input = new SdlInput();
            input.LoadConfiguration("SNES", Lookup);
            short Read(uint id) => input.GetInputState(0, JOYPAD, 0, id);

            input.Keyboard.Set(Code(Key.Space), true);
            short spaceB = Read(LibretroInput.JOYPAD_B);
            input.Keyboard.Set(Code(Key.Space), false);
            input.Keyboard.Set(Code(Key.Z), true);
            short zB = Read(LibretroInput.JOYPAD_B);
            input.Keyboard.Set(Code(Key.Z), false);
            input.Keyboard.Set(Code(Key.X), true);
            short xA = Read(LibretroInput.JOYPAD_A);
            input.Keyboard.Set(Code(Key.X), false);
            Check(spaceB == 1 && zB == 0 && xA == 1 && Read(LibretroInput.JOYPAD_B) == 0,
                $"SNES, B saved as Space: the core reads B from Space ({spaceB}), not from Z ({zB}); X still A ({xA})");

            KeyboardBindings.SdlCodes(Key.Space, out int sc, out _);
            input.Keyboard.Set(KeyboardBindings.RawScancode | sc, true);
            short rawB = Read(LibretroInput.JOYPAD_B);
            input.Keyboard.Set(KeyboardBindings.RawScancode | sc, false);
            Check(rawB == 1, "the same bind by scancode, as the Wayland window reports keys");

            input.LoadConfiguration("N64", Lookup);
            input.UsesAnalogStick = true;
            input.Keyboard.Set(Code(Key.T), true);
            short y = input.GetInputState(0, ANALOG, 0, 1);
            input.Keyboard.Set(Code(Key.T), false);
            Check(y == -32767 && input.GetInputState(0, ANALOG, 0, 1) == 0,
                $"N64, Analog Up saved as T: the core reads the left stick up (Y={y}) and back to centre on release");

            input.LoadConfiguration("GBA", Lookup);
            input.UsesAnalogStick = false;
            input.Keyboard.Set(Code(Key.Z), true);
            short gbaB = Read(LibretroInput.JOYPAD_B);
            input.Keyboard.Set(Code(Key.Z), false);
            Check(gbaB == 1, "a console with nothing saved plays on the built-in keys (GBA: Z is B)");
        }

        // ── 5. The game host's SDL window ─────────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("SDL3")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
        static extern bool SDL_PushEvent(byte[] ev);

        // An SDL_KeyboardEvent in a 128-byte SDL_Event: type@0, windowID@16, scancode@24, key@28,
        // mod@32, down@36, repeat@37 (the layout GlPresenter reads).
        static void PushKey(int scancode, bool down, int reportedKey = 0, ushort mod = 0, bool repeat = false)
        {
            var ev = new byte[128];
            BitConverter.GetBytes(down ? 0x300u : 0x301u).CopyTo(ev, 0);
            BitConverter.GetBytes((uint)scancode).CopyTo(ev, 24);
            BitConverter.GetBytes((uint)reportedKey).CopyTo(ev, 28);
            BitConverter.GetBytes(mod).CopyTo(ev, 32);
            ev[36] = (byte)(down ? 1 : 0);
            ev[37] = (byte)(repeat ? 1 : 0);
            SDL_PushEvent(ev);
        }

        /// <summary>
        /// The real GlPresenter window, fed key events through SDL's own queue: what it reports is
        /// what OnGlKey receives. Needs a display (run it in a nested compositor); without one the
        /// section says so and counts nothing.
        /// </summary>
        static void WindowChecks()
        {
            Line("-- SDL game window --");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                Line("  [INFO] no display — the window checks did not run");
                return;
            }
            var gl = GlPresenter.TryCreate(320, 240, false, out string? err);
            if (gl == null) { Check(false, $"the SDL game window opens ({err})"); return; }
            try
            {
                var got = new List<(int Sc, int Kc, bool Down)>();
                gl.KeyEvent += (sc, kc, down) => got.Add((sc, kc, down));
                gl.PumpEvents();
                got.Clear();

                PushKey(44, down: true, reportedKey: ' ');
                PushKey(44, down: true, reportedKey: ' ', repeat: true);
                PushKey(44, down: false, reportedKey: ' ');
                gl.PumpEvents();
                Check(got.SequenceEqual(new[] { (44, (int)' ', true), (44, (int)' ', false) }),
                    $"Space down, a repeat, up: the window reports the down and the up with keycode ' ', no repeat (got {string.Join(" ", got)})");

                got.Clear();
                PushKey(4, down: true, reportedKey: 'A', mod: 0x0001);   // Shift held: SDL's own key is 'A'
                PushKey(4, down: false, reportedKey: 'A', mod: 0x0001);
                gl.PumpEvents();
                Check(got.Count == 2 && got.All(g => g.Sc == 4 && g.Kc == 'a'),
                    $"with Shift held the A key still reports 'a', so a bind on A fires (got {string.Join(" ", got)})");

                // The whole chain behind the window: its report, as OnGlKey forwards it, into the
                // pad a core reads — B saved as Space on SNES.
                var input = new SdlInput();
                input.LoadConfiguration("SNES", key => key == "SNES_P1"
                    ? new InputConfiguration { ConsoleName = key, KeyboardMappings = { new ButtonMapping { ButtonName = "B", InputIdentifier = "Space" } } }
                    : new InputConfiguration { ConsoleName = key });
                bool bDown = false;
                gl.KeyEvent += (sc, kc, down) =>
                {
                    input.Keyboard.Set(kc >= 0 ? kc : KeyboardBindings.RawScancode | sc, down);
                    if (down) bDown = input.GetInputState(0, SdlInput.RETRO_DEVICE_JOYPAD, 0, LibretroInput.JOYPAD_B) == 1;
                };
                PushKey(44, down: true);
                gl.PumpEvents();
                PushKey(44, down: false);
                gl.PumpEvents();
                Check(bDown && input.GetInputState(0, SdlInput.RETRO_DEVICE_JOYPAD, 0, LibretroInput.JOYPAD_B) == 0,
                    "a Space press in the window reaches the core as B, and its release lets go");
            }
            finally { gl.Dispose(); }
        }
    }
}
