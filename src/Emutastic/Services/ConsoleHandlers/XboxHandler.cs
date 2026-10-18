namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for the original Xbox (Midway Verdant, midway_verdant_libretro). The core renders on its own
    /// Vulkan device and hands over a software XRGB8888 frame, so no HW-render context is involved. Four pad
    /// ports, each a RetroPad with both sticks; the picture is 4:3 (the console's 640x480 scanout).
    /// </summary>
    public class XboxHandler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "Xbox";

        public override bool UsesAnalogStick => true;

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            for (uint port = 0; port < 4; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        public override double GetDisplayAspectRatio(uint baseWidth, uint baseHeight, float coreAspectRatio) => 4.0 / 3.0;
    }
}
