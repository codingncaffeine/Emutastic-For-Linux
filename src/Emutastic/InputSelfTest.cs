using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Emutastic.Configuration;
using Emutastic.Platform;

namespace Emutastic
{
    /// <summary>
    /// Headless self-test for controller → player routing: <c>Emutastic --selftest-input</c>.
    ///
    /// Uses SDL3 virtual joysticks, so it proves the whole path — enumeration, stable ids,
    /// per-player binding, the disconnect rule, and the raw-joystick fallback — with no hardware
    /// attached and no window. Real controllers that ARE attached are listed for information.
    /// Feeds the session in-memory configurations; never reads or writes the user's config file.
    /// Exit code 0 = every check passed.
    /// </summary>
    internal static class InputSelfTest
    {
        const uint SDL_INIT_JOYSTICK = 0x00000200, SDL_INIT_GAMEPAD = 0x00002000;
        const ushort SDL_JOYSTICK_TYPE_UNKNOWN = 0, SDL_JOYSTICK_TYPE_GAMEPAD = 1;
        const byte SDL_HAT_UP = 0x01;

        // SDL_VirtualJoystickDesc, field-for-field from SDL3/SDL_joystick.h. Sequential layout with
        // natural alignment gives 136 bytes on x86-64; SDL validates `version` against that size.
        [StructLayout(LayoutKind.Sequential)]
        struct SDL_VirtualJoystickDesc
        {
            public uint   version;
            public ushort type;
            public ushort padding;
            public ushort vendor_id;
            public ushort product_id;
            public ushort naxes;
            public ushort nbuttons;
            public ushort nballs;
            public ushort nhats;
            public ushort ntouchpads;
            public ushort nsensors;
            public ushort padding2_0;
            public ushort padding2_1;
            public uint   button_mask;
            public uint   axis_mask;
            public IntPtr name;
            public IntPtr touchpads;
            public IntPtr sensors;
            public IntPtr userdata;
            public IntPtr Update;
            public IntPtr SetPlayerIndex;
            public IntPtr Rumble;
            public IntPtr RumbleTriggers;
            public IntPtr SetLED;
            public IntPtr SendEffect;
            public IntPtr SetSensorsEnabled;
            public IntPtr Cleanup;
        }

        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_InitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_QuitSubSystem(uint flags);
        [DllImport("SDL3")] static extern IntPtr SDL_GetError();
        [DllImport("SDL3")] static extern uint SDL_AttachVirtualJoystick(ref SDL_VirtualJoystickDesc desc);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_DetachVirtualJoystick(uint instance_id);
        [DllImport("SDL3")] static extern IntPtr SDL_GetJoystickFromID(uint instance_id);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_SetJoystickVirtualButton(IntPtr joystick, int button, [MarshalAs(UnmanagedType.I1)] bool down);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_SetJoystickVirtualAxis(IntPtr joystick, int axis, short value);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_SetJoystickVirtualHat(IntPtr joystick, int hat, byte value);

        static int _failures;
        static void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) _failures++;
        }
        static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

        sealed class Virtual : IDisposable
        {
            public uint Id; public IntPtr Joystick; readonly IntPtr _name;
            public Virtual(string name, ushort type, ushort axes, ushort buttons, ushort hats)
            {
                _name = Marshal.StringToCoTaskMemUTF8(name);
                var desc = new SDL_VirtualJoystickDesc
                {
                    version = (uint)Marshal.SizeOf<SDL_VirtualJoystickDesc>(),
                    type = type, naxes = axes, nbuttons = buttons, nhats = hats, name = _name,
                };
                Id = SDL_AttachVirtualJoystick(ref desc);
                if (Id == 0) throw new InvalidOperationException($"SDL_AttachVirtualJoystick('{name}') failed: {Err()}");
                // The joystick must be OPEN for SDL_SetJoystickVirtual* to accept it. SdlDeviceSet
                // opens it on Reconcile; SDL_GetJoystickFromID returns that open handle.
            }
            public IntPtr Handle => Joystick != IntPtr.Zero ? Joystick : (Joystick = SDL_GetJoystickFromID(Id));
            public void Button(int b, bool down) { if (!SDL_SetJoystickVirtualButton(Handle, b, down)) Console.WriteLine($"    (virtual button set failed: {Err()})"); }
            public void Axis(int a, short v)     { if (!SDL_SetJoystickVirtualAxis(Handle, a, v))     Console.WriteLine($"    (virtual axis set failed: {Err()})"); }
            public void Hat(int h, byte v)       { if (!SDL_SetJoystickVirtualHat(Handle, h, v))      Console.WriteLine($"    (virtual hat set failed: {Err()})"); }
            public void Detach() { if (Id != 0) { SDL_DetachVirtualJoystick(Id); Id = 0; } }
            public void Dispose() { Detach(); if (_name != IntPtr.Zero) Marshal.FreeCoTaskMem(_name); }
        }

        public static int Run()
        {
            Console.WriteLine("=== input self-test: controller -> player routing ===");
            _failures = 0;

            Check(Marshal.SizeOf<SDL_VirtualJoystickDesc>() == 136,
                  $"SDL_VirtualJoystickDesc marshals to 136 bytes (got {Marshal.SizeOf<SDL_VirtualJoystickDesc>()})");

            if (!SDL_InitSubSystem(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD))
            {
                Console.WriteLine($"  [FAIL] SDL_InitSubSystem: {Err()}");
                return 1;
            }

            using var input = new SdlInput();
            input.Initialize();
            input.Poll();

            Console.WriteLine($"--- real controllers attached right now: {input.Devices.Count}");
            foreach (var d in input.Devices)
                Console.WriteLine($"    \"{d.Id}\"  {(d.IsGamepad ? "gamepad-mapped" : "RAW joystick")}  buttons={d.NumButtons} axes={d.NumAxes} hats={d.NumHats}");
            int realCount = input.Devices.Count;

            // Two IDENTICAL virtual gamepads (tests the "#0"/"#1" occurrence ids) and one virtual
            // joystick SDL has no mapping for (tests the raw positional path).
            using var padA = new Virtual("Virtual Pad", SDL_JOYSTICK_TYPE_GAMEPAD, axes: 6, buttons: 21, hats: 0);
            using var padB = new Virtual("Virtual Pad", SDL_JOYSTICK_TYPE_GAMEPAD, axes: 6, buttons: 21, hats: 0);
            using var raw  = new Virtual("Virtual Raw Stick", SDL_JOYSTICK_TYPE_UNKNOWN, axes: 4, buttons: 12, hats: 1);

            Pump(input);
            input.RefreshDevices();

            Console.WriteLine("--- enumeration + identity");
            Check(input.Devices.Count == realCount + 3, $"three virtual devices enumerated (total {input.Devices.Count})");
            var byId = new Dictionary<string, SdlDeviceSet.Device>();
            foreach (var d in input.Devices) byId[d.Id] = d;
            Check(byId.ContainsKey("Virtual Pad#0") && byId.ContainsKey("Virtual Pad#1"),
                  "identical pads get ids \"Virtual Pad#0\" and \"Virtual Pad#1\"");
            Check(byId.TryGetValue("Virtual Pad#1", out var b1) && b1.DisplayName == "Virtual Pad (2)",
                  "the second identical pad displays as \"Virtual Pad (2)\"");
            Check(byId.TryGetValue("Virtual Raw Stick#0", out var rawDev) && rawDev != null && !rawDev.IsGamepad,
                  "the unmapped joystick is enumerated and flagged RAW (no gamepad mapping)");
            Check(byId.TryGetValue("Virtual Pad#0", out var a0) && a0 != null && a0.IsGamepad,
                  "a virtual gamepad-type joystick gets an SDL gamepad mapping");

            // Bind P1 -> the raw stick, P2 -> the SECOND identical pad. P3/P4 unbound.
            var cfg = new Dictionary<string, InputConfiguration>
            {
                ["SNES_P1"] = new InputConfiguration { ConsoleName = "SNES", ControllerDeviceId = "Virtual Raw Stick#0" },
                ["SNES_P2"] = new InputConfiguration { ConsoleName = "SNES", ControllerDeviceId = "Virtual Pad#1" },
            };
            input.LoadConfiguration("SNES", key => cfg.TryGetValue(key, out var c) ? c : new InputConfiguration { ConsoleName = "SNES" });

            Console.WriteLine("--- port resolution");
            Check(input.PortDeviceId(0) == "Virtual Raw Stick#0", $"P1 reads its bound raw stick (got {input.PortDeviceId(0) ?? "none"})");
            Check(input.PortDeviceId(1) == "Virtual Pad#1",       $"P2 reads its bound pad #1 (got {input.PortDeviceId(1) ?? "none"})");
            // P3 is unbound: it takes the first UNCLAIMED device in enumeration order. With no real
            // pads that is "Virtual Pad#0"; with real pads attached it is the first real one.
            string? p3 = input.PortDeviceId(2);
            Check(p3 != null && p3 != "Virtual Raw Stick#0" && p3 != "Virtual Pad#1",
                  $"P3 (unbound) takes an unclaimed device, never a bound one (got {p3 ?? "none"})");
            if (realCount == 0)
                Check(p3 == "Virtual Pad#0", "P3 defaults to \"Virtual Pad#0\" (first unclaimed, enumeration order)");

            Console.WriteLine("--- raw joystick reads on P1 (positional layout)");
            const uint JOYPAD = SdlInput.RETRO_DEVICE_JOYPAD;
            raw.Button(0, true); Pump(input);
            Check(input.GetInputState(0, JOYPAD, 0, 0) == 1, "raw button 0 -> RetroPad B on P1");
            Check(input.GetInputState(1, JOYPAD, 0, 0) == 0, "…and NOT on P2");
            raw.Button(0, false);
            raw.Hat(0, SDL_HAT_UP); Pump(input);
            Check(input.GetInputState(0, JOYPAD, 0, 4) == 1, "raw hat UP -> RetroPad UP on P1");
            raw.Hat(0, 0);
            raw.Button(10, true); Pump(input);
            Check(input.GetInputState(0, JOYPAD, 0, 12) == 1, "raw spare button 10 -> RetroPad L2 (triggers claimed first)");
            raw.Button(10, false);
            raw.Axis(1, -30000); Pump(input);
            Check(input.IsRawControlDown(112, 0), "raw axis 1 pushed negative reads as stick-UP (id 112) — no sign flip on Linux");
            input.UsesAnalogStick = true;
            Check(input.GetInputState(0, SdlInput.RETRO_DEVICE_ANALOG, 0, 1) < -20000, "…and RETRO_DEVICE_ANALOG left-Y is negative (libretro up = negative)");
            raw.Axis(1, 0);

            Console.WriteLine("--- gamepad reads on P2");
            padB.Button(0, true); Pump(input);   // SDL_GAMEPAD_BUTTON_SOUTH
            Check(input.GetInputState(1, JOYPAD, 0, 0) == 1, "pad #1 SOUTH -> RetroPad B on P2");
            Check(input.GetInputState(2, JOYPAD, 0, 0) == 0, "…and NOT on P3");
            padB.Button(0, false);
            padB.Axis(4, 30000); Pump(input);    // SDL_GAMEPAD_AXIS_LEFT_TRIGGER
            Check(input.GetInputState(1, JOYPAD, 0, 12) == 1, "pad #1 left trigger axis -> RetroPad L2 on P2");
            padB.Axis(4, 0); Pump(input);

            Console.WriteLine("--- hat-less raw pads: where the d-pad comes from (the Retrolink-class layouts)");
            // Cheap SNES/NES USB adapters report no hat and put the d-pad on axes 0/1; a button-only
            // board reports neither hat nor axes. The spare-button d-pad must serve ONLY the latter.
            using var adapter = new Virtual("Virtual Adapter", SDL_JOYSTICK_TYPE_UNKNOWN, axes: 2, buttons: 14, hats: 0);
            using var buttonsOnly = new Virtual("Virtual Buttons Only", SDL_JOYSTICK_TYPE_UNKNOWN, axes: 0, buttons: 16, hats: 0);
            Pump(input);
            input.RefreshDevices();
            string? p3Held = input.PortDeviceId(2);
            cfg["SNES_P3"] = new InputConfiguration { ConsoleName = "SNES", ControllerDeviceId = "Virtual Adapter#0" };
            cfg["SNES_P4"] = new InputConfiguration { ConsoleName = "SNES", ControllerDeviceId = "Virtual Buttons Only#0" };
            input.LoadConfiguration("SNES", key => cfg.TryGetValue(key, out var c) ? c : new InputConfiguration { ConsoleName = "SNES" });
            Check(input.PortDeviceId(2) == "Virtual Adapter#0" && input.PortDeviceId(3) == "Virtual Buttons Only#0",
                  $"live rebind: P3 gives up its default pad ({p3Held}) for the adapter, P4 takes the button board");
            adapter.Button(12, true); Pump(input);
            Check(input.GetInputState(2, JOYPAD, 0, 4) == 0, "adapter (no hat, HAS axes): spare button 12 is NOT a phantom UP");
            adapter.Button(12, false);
            adapter.Axis(1, -30000); Pump(input);
            input.PromoteAnalogStickToDpad = true;
            Check(input.GetInputState(2, JOYPAD, 0, 4) == 1, "…its axis 1 negative reads as UP through the stick->d-pad promotion");
            input.PromoteAnalogStickToDpad = false;
            adapter.Axis(1, 0);
            buttonsOnly.Button(12, true); Pump(input);
            Check(input.GetInputState(3, JOYPAD, 0, 4) == 1, "button-only board (no hat, no axes): spare button 12 -> UP");
            buttonsOnly.Button(12, false);
            buttonsOnly.Button(10, true); Pump(input);
            Check(input.GetInputState(3, JOYPAD, 0, 12) == 1, "…and spare button 10 -> L2 (triggers are claimed before the d-pad)");
            buttonsOnly.Button(10, false);
            adapter.Detach(); buttonsOnly.Detach(); Pump(input);
            input.RefreshDevices();
            Check(input.PortDeviceId(2) == null && input.PortDeviceId(3) == null, "unplugging both bound pads leaves P3/P4 reading nothing");

            Console.WriteLine("--- disconnect rule: losing one player's pad must not shift the others");
            string? p2Before = input.PortDeviceId(1), p3Before = input.PortDeviceId(2);
            raw.Detach(); Pump(input);
            input.RefreshDevices();
            Check(input.PortDeviceId(0) == null,      "P1's bound stick unplugged -> P1 reads nothing (not handed another pad)");
            Check(input.PortDeviceId(1) == p2Before,  $"P2 unchanged ({p2Before})");
            Check(input.PortDeviceId(2) == p3Before,  $"P3 unchanged ({p3Before})");
            using var raw2 = new Virtual("Virtual Raw Stick", SDL_JOYSTICK_TYPE_UNKNOWN, axes: 4, buttons: 12, hats: 1);
            Pump(input);
            input.RefreshDevices();
            Check(input.PortDeviceId(0) == "Virtual Raw Stick#0", "re-plugging the stick re-binds P1 without any config change");
            Check(input.PortDeviceId(1) == p2Before && input.PortDeviceId(2) == p3Before, "…and P2/P3 still unchanged");

            Console.WriteLine("--- documented limit: identical pads renumber when an earlier one leaves");
            padA.Detach(); Pump(input);
            input.RefreshDevices();
            Console.WriteLine($"    after unplugging \"Virtual Pad#0\": P2 -> {input.PortDeviceId(1) ?? "none"} (bound \"Virtual Pad#1\", which is now labelled #0 — inherent to any index scheme)");

            padB.Detach(); raw2.Detach(); Pump(input);
            input.RefreshDevices();
            Check(input.Devices.Count == realCount, "all virtual devices detached cleanly");

            Console.WriteLine(_failures == 0 ? "=== PASS ===" : $"=== FAIL ({_failures} check(s)) ===");
            return _failures == 0 ? 0 : 1;
        }

        // Virtual state changes become visible to readers on the next joystick update; Poll()
        // does that (and the hot-plug rescan is forced explicitly where the test needs it).
        static void Pump(SdlInput input)
        {
            for (int i = 0; i < 3; i++) input.Poll();
        }
    }
}
