using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Nintendo DS (DeSmuME). Exists for the touch screen: controller-only players (couch/TV)
    /// can't click the window, and games gate progression behind mandatory touches (RPG intro
    /// sequences etc.). The core's emulated pointer moves a crosshair with the RIGHT analog
    /// stick and taps on the JOYPAD_R2 wire (the bindable "Touch" row in Edit Controls —
    /// upstream commits 8308053/8f0ec0e). Mouse clicks keep working alongside.
    /// Visuals menu (upstream a540f91): DeSmuME's software-rasterizer visual options in the
    /// overlay cog, mirroring the 3DS layout.
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
            // Performance: the core defaults are interpreter + single-threaded SoftRasterizer,
            // which caps high internal resolutions on CPU rendering. A/B (Mario Kart DS attract
            // race at 4x/1024x768, 2026-06-07): defaults 48-51fps @ ~20.5ms core.Run →
            // jit + 4 raster threads locked 60 @ ~8.6ms. Same config-archaeology class as the
            // GameCube dual-core+fastmem win; candidate to suggest upstream.
            ["desmume_cpu_mode"]  = "jit",
            ["desmume_num_cores"] = "4",
        };

        // DeSmuME renders on the CPU (SoftRasterizer), and both of these
        // options work on that path. The "OpenGL:" prefixed core options
        // (multisampling, texture smoothing, shadow polygons) require the GL
        // rasterizer we don't enable — deliberately not exposed.
        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("desmume_internal_resolution", "Internal Resolution"),
            ("desmume_gfx_texture_scaling", "Texture Scaling (xBrz)"),
        };

        // SoftRasterizer cost scales with pixel count and the core offers up
        // to 10x (2560x1920) — slideshow territory for CPU rendering. Cap the
        // picker at 4x; 2x-3x is the sweet spot for big displays.
        private static readonly HashSet<string> AllowedResolutions = new()
        {
            "256x192", "512x384", "768x576", "1024x768"
        };

        public override string[] FilterCoreOptionValues(string key, string[] values)
        {
            if (key == "desmume_internal_resolution")
                return values.Where(v => AllowedResolutions.Contains(v.Trim())).ToArray();
            return values;
        }
    }
}
