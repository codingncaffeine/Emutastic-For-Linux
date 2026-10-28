using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for the original Xbox (Midway Verdant, midway_verdant_libretro). The core renders with Vulkan on a
    /// device made through its negotiation interface on our instance, and hands each frame over as a Vulkan image
    /// (drawn from GPU memory; read back to the CPU where a presenter cannot import it). Four pad
    /// ports, each a RetroPad with both sticks; the picture is 4:3 (the console's 640x480 scanout, times the
    /// internal resolution).
    /// </summary>
    public class XboxHandler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "Xbox";

        public override bool UsesAnalogStick => true;
        // Midway Verdant renders with Vulkan; at a high internal resolution its frames are tens of MB, shown from GPU memory.
        public override bool AcceptsVulkanHwRender => true;

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            for (uint port = 0; port < 4; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        public override double GetDisplayAspectRatio(uint baseWidth, uint baseHeight, float coreAspectRatio) => 4.0 / 3.0;

        // Both apply live: a new internal resolution at the core's next vblank (its render targets come back at the
        // new size, the frame grows with them), anisotropic filtering as new samplers are made.
        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("midway_verdant_resolution", "Internal Resolution"),
            ("midway_verdant_anisotropy", "Anisotropic Filtering"),
        };
    }
}
