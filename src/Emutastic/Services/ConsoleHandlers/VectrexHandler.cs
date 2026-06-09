using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class VectrexHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "Vectrex";

        // vecx reads the joystick as the digital d-pad. Let a modern pad's LEFT ANALOG STICK drive
        // that d-pad too (like the other digital consoles), so the stick works without binding each
        // direction — the explicit d-pad binding and the stick both reach the same JOYPAD directions.
        public override bool PromoteAnalogStickToDpad => true;

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            { "vecx_res_multi", "3" },
        };

        // The vecx HW renderer passes its actual render dimensions (e.g. 869×1080)
        // in the video callback. Reading the full square FBO captures extra black columns
        // on the right, making the game appear shifted left. Use the callback dimensions.
        public override bool UseFullFboReadback => false;
    }
}
