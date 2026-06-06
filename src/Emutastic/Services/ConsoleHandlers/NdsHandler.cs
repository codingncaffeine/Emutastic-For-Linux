using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Nintendo DS (DeSmuME). Exists for the touch screen: controller-only players (couch/TV)
    /// can't click the window, and games gate progression behind mandatory touches (RPG intro
    /// sequences etc.). The core's emulated pointer moves a crosshair with the RIGHT analog
    /// stick and taps on the JOYPAD_R2 wire (the bindable "Touch" row in Edit Controls —
    /// upstream commits 8308053/8f0ec0e). Mouse clicks keep working alongside.
    /// </summary>
    public class NdsHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "NDS";

        // Right stick must reach the core as RETRO_DEVICE_ANALOG for the emulated pointer;
        // the left stick still promotes to D-pad like the other digital-pad handhelds.
        public override bool UsesAnalogStick => true;
        public override bool PromoteAnalogStickToDpad => true;

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            // Absolute touch pointer, no mouse-style crosshair lag (upstream EmulatorWindow).
            ["desmume_pointer_type"] = "touch",
            // Right-stick emulated pointer ON by default (upstream 8308053). A user's saved
            // Core Options choice still wins — EmulatorSession applies the store on top.
            ["desmume_pointer_device_r"] = "emulated",
        };
    }
}
