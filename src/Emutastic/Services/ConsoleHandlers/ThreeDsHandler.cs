using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class ThreeDsHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "3DS";
        public override bool UsesAnalogStick => true;

        // azahar mis-sizes its av_info max geometry when citra_resolution_factor is pre-seeded
        // before retro_load_game (10x boot reported 640x480 max -> render clipped to a corner
        // sliver; 1x boot reports the full 7200x4800). Verified with testing: applying the
        // factor through the live variables-dirty path right after frame 1 renders correctly
        // at any factor, so the saved value is deferred past load.
        public override System.Collections.Generic.IReadOnlyCollection<string> DeferUntilAfterLoad
            => new[] { "citra_resolution_factor" };

        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("citra_resolution_factor", "Internal Resolution"),
            ("citra_texture_filter", "Texture Filter"),
        };
    }
}
