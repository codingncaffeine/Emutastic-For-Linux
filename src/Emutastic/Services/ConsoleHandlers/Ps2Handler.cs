using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for PlayStation 2 (LRPS2 / pcsx2_libretro).
    ///
    /// Renderer is pinned to the **OpenGL** GS backend. Upstream (Windows) pins
    /// D3D11 and drives it through a D3D11 HW-render context; that whole path is
    /// Windows-only and doesn't exist here. On Linux we drive pcsx2's OpenGL GS
    /// renderer through the same mature GL HW path PS1/GameCube/Dreamcast/3DS
    /// already use (SET_HW_RENDER type 3 → HwGlContext → GL overlay). Vulkan /
    /// paraLLEl-GS is the cross-platform follow-up: it needs a libretro Vulkan
    /// HW-render-context negotiation the port doesn't have yet (SET_HW_RENDER
    /// declines type 6 today), so it's deferred.
    ///
    /// NEVER "Auto": Auto lets the core pick a backend and can fall to a path the
    /// frontend can't present (libretro/ps2 #13). We pin OpenGL explicitly.
    /// </summary>
    public class Ps2Handler : ConsoleHandlerBase
    {
        // LRPS2's retro_set_controller_port_device only recognizes plain
        // RETRO_DEVICE_JOYPAD (=1), which it maps to a DualShock2 (and still
        // reads the analog axes). The DualShock SUBCLASS (2<<8|5 = 517) falls
        // through its switch to "Type = None" — i.e. NO controller connected,
        // which blocks games like God of War that gate on a detected pad.
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "PS2";

        // PS2 games lean on the analog sticks (3D camera/movement). With the
        // JOYPAD device LRPS2 reads BOTH the d-pad buttons and the analog axes,
        // and on Linux UsesAnalogStick only gates the analog-axis read path
        // (SdlInput.ReadAnalog) — the d-pad flows independently, so unlike the
        // PS1 DualShock-subclass quirk there's no d-pad shadowing here.
        public override bool UsesAnalogStick => true;

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            for (uint port = 0; port < 2; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        // NOTE: this hook feeds BOTH the in-game cog dropdown AND the launch-time
        // value validation/clamp — capping a value here silently overrides what
        // the user set in Preferences → Core Options. Keep it to genuinely
        // unsupported values only.
        public override string[] FilterCoreOptionValues(string key, string[] values)
        {
            // Renderer: expose only OpenGL on Linux. Hide Auto/Software and the
            // Windows-only D3D backends, plus Vulkan/paraLLEl-GS until the Vulkan
            // HW-render path lands. OpenGL stays the validation fallback.
            if (key == "pcsx2_renderer")
                return values.Where(v => v == "OpenGL").ToArray();

            return values;
        }

        // RETRO_HW_CONTEXT_OPENGL_CORE. SET_HW_RENDER creates the GL context and
        // the core renders pcsx2's OpenGL GS through it — same plumbing PS1's
        // Beetle PSX HW uses.
        public override int PreferredHwContext => 3;

        // Direct GPU→GPU present via the GL overlay window (glBlitFramebuffer +
        // SwapBuffers), avoiding the per-frame glReadPixels readback that ships
        // tens of MB/frame at high internal resolution. Falls back to readback
        // when the AMD/Intel compatibility toggle renders to FBO 0. Same pattern
        // as PS1/GameCube/Dreamcast.
        public override bool UseGLOverlay => !UseDefaultFramebuffer;

        public override bool UseDefaultFramebuffer =>
            App.Configuration?.GetEmulatorConfiguration().ResolveAmdIntelCompat() ?? false;

        // Upscale lives in the cog's Visuals menu. The panel filters to keys the
        // active core actually announced, so listing them here is safe even
        // before the core declares them. GSdx HW (OpenGL) uses
        // pcsx2_upscale_multiplier for internal resolution.
        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("pcsx2_upscale_multiplier",   "Internal Resolution ⚠ restart"),
            ("pcsx2_texture_filtering",    "Texture Filtering"),
            ("pcsx2_anisotropic_filtering","Anisotropic Filtering"),
            ("pcsx2_deinterlace_mode",     "Deinterlacing"),
        };

        // Sane desktop defaults condensed from the LRPS2 integration brief.
        // Renderer EXPLICIT OpenGL (the only Linux GS backend we present today).
        // Users override via the core-options UI; persisted values win over
        // these. Value strings must match the core's exact option strings.
        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            ["pcsx2_renderer"]            = "OpenGL",
            ["pcsx2_upscale_multiplier"]  = "2x Native (~720p)",   // must match the core's exact value string
            ["pcsx2_fastboot"]            = "enabled",
            ["pcsx2_fastcdvd"]            = "disabled",
            ["pcsx2_shared_memory_cards"] = "enabled",
            ["pcsx2_widescreen_hint"]     = "disabled",
            ["pcsx2_deinterlace_mode"]    = "Automatic",
            ["pcsx2_texture_filtering"]   = "Bilinear (PS2)",
            ["pcsx2_blending_accuracy"]   = "Basic",
            ["pcsx2_ee_cycle_rate"]       = "100%",
            ["pcsx2_ee_cycle_skip"]       = "disabled",
            ["pcsx2_enable_hw_hacks"]     = "disabled",
            ["pcsx2_nointerlacing_hint"]  = "enabled",
            ["pcsx2_pcrtc_antiblur"]      = "enabled",
            ["pcsx2_dithering"]           = "Unscaled",
            ["pcsx2_anisotropic_filtering"] = "disabled",
        };
    }
}
