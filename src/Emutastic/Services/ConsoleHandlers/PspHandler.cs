using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class PspHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "PSP";
        public override bool UsesAnalogStick => true;

        // PPSSPP requests GL 3.1 core but initializes GL via glewInit(), which fails on
        // any core-profile context ("[G3D] glewInit() failed." → black screen). Hand it
        // a compatibility context instead — see ConsoleHandlerBase.ForceCompatibilityGlProfile.
        public override bool ForceCompatibilityGlProfile => true;

        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("ppsspp_internal_resolution", "Internal Resolution"),
            ("ppsspp_texture_filtering", "Texture Filter"),
            ("ppsspp_mulitsample_level", "Anti-Aliasing"),
            ("ppsspp_texture_scaling_level", "Texture Upscaling"),
        };
    }
}
