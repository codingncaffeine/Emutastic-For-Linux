using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for GameCube via Dolphin libretro.
    ///
    /// Key differences from generic cores:
    ///  - Requires all 4 controller ports configured post-LoadGame (triggers Dolphin's input stage 2 init)
    ///  - dolphin_cpu_core must be auto-selected at runtime from the core's valid list (avoid JIT)
    ///  - Analog sticks active
    ///
    /// Linux port note: upstream blocked Windows' d3d11.dll D3D11CreateDevice (via VirtualProtect
    /// byte-patching) during context_reset so Dolphin would fall straight through to OpenGL. On
    /// Linux there is no d3d11.dll and the Linux dolphin_libretro.so has no Direct3D backend — it
    /// uses OpenGL/Vulkan natively — so the dolphin_gfx_backend=OGL core option below is sufficient
    /// and the D3D-blocking hooks are no-ops here. (The upstream code patched the Windows SYSTEM
    /// dll, not the Dolphin core, so there are no core-specific byte offsets to re-derive.)
    /// </summary>
    public class GameCubeHandler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "GameCube";
        public override bool UsesAnalogStick => true;

        // =====================================================================
        // Core options
        // =====================================================================
        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("dolphin_efb_scale", "Internal Resolution"),
            ("dolphin_max_anisotropy", "Anisotropic Filtering"),
            ("dolphin_anti_aliasing", "Anti-Aliasing"),
        };

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            // Dual-core + fastmem: the original single-core/no-fastmem safety config was a
            // Windows-era caution (SEH/VEH conflicts with .NET) that does NOT apply on Linux —
            // Dolphin's SIGSEGV fastmem handler chains cleanly with CoreCLR's, and the CPU
            // thread never touches GL so it needs no shared context. A/B on Mario Kart DD
            // attract race (RTX 3080 Ti, 2026-06-07): single-core no-fastmem 42-46fps →
            // dual-core 54-58 → dual-core+fastmem locked 60. If a game crashes on launch,
            // flip these three in Preferences → Core Options (values store overrides these).
            ["dolphin_main_cpu_thread"]        = "enabled",
            ["dolphin_fastmem"]                = "enabled",
            ["dolphin_fastmem_arena"]          = "enabled",
            // dolphin_cpu_core: NOT pre-seeded — OnVariableAnnounced picks the safest
            // non-JIT value from the core's own valid list at runtime
            ["dolphin_dsp_jit"]                = "disabled",
            ["dolphin_dsp_enable_jit"]         = "disabled",
            ["dolphin_dsp_hle"]                = "enabled",
            ["dolphin_skip_gc_bios"]           = "enabled",
            // Force OpenGL — only a GL context is set up
            ["dolphin_gfx_backend"]            = "OGL",
            ["dolphin_renderer"]               = "Hardware",
            // Synchronous UBERSHADERS (mode 1) — the only compilation mode that needs no worker
            // thread. Dolphin can't create its shader-compiler worker against our surfaceless EGL
            // context ("Failed to create shared context for shader compiling"), so the default
            // specialized mode (0) compiles EVERY new shader inline on the emu thread → 0-1fps
            // slideshow until a scene's shaders are done (the Linux GameCube 1fps bug, 2026-06-03).
            // Ubershaders compile a small fixed set on demand, cached globally and persistently
            // (Saves/User/Cache/Shaders/OpenGL-uber-pipeline-*.cache) — measured 0-1fps → 57.8fps
            // on Mario Kart DD once the cache warmed; fresh scenes still dip while it fills.
            ["dolphin_shader_compilation_mode"] = "1",
            // Do NOT precompile the full uber set at boot: at this GPU's idle clocks that stalled
            // boot for 60s+. On-demand compiles amortize, and the global cache makes them one-time.
            ["dolphin_wait_for_shaders"]        = "disabled",
            // Internal resolution. NOT 1x: at native res Dolphin's framebuffer-indirection
            // render lands tucked in the bottom-left corner of our managed FBO (documented in
            // the GameCube wiki page). The image fills the buffer correctly from ~3x up; 4x is
            // the wiki's recommended "4K-equivalent" upscale and renders full-window out of the box.
            ["dolphin_efb_scale"]              = "4",

            // ── Performance options ──────────────────────────────────────────
            // EFB copies to texture instead of RAM: avoids expensive VRAM→RAM→VRAM roundtrip.
            // This alone can be worth 20-30% for GC titles that use EFB effects heavily.
            ["dolphin_efb_copy_method"]        = "Texture",
            ["dolphin_efb_copy_to_texture"]    = "enabled",   // alternate key name (core version dependent)
            // Disable CPU reads from EFB — expensive synchronous stall, most games don't need it.
            ["dolphin_efb_access_enable"]      = "disabled",
            ["dolphin_efb_access"]             = "disabled",  // alternate key name
            // GPU-side texture decode — offloads format conversion from CPU to GPU.
            ["dolphin_gpu_texture_decode"]     = "enabled",
            // No texture filtering at 1x — eliminates bilinear sampling overhead.
            ["dolphin_texture_filtering"]      = "Nearest",
            // No anisotropic filtering.
            ["dolphin_max_anisotropy"]         = "0",
            // No MSAA.
            ["dolphin_video_multisampling"]    = "disabled",
            ["dolphin_msaa"]                   = "disabled",
            // Widescreen hack adds a clip-space transform pass — skip it.
            ["dolphin_widescreen_hack"]        = "disabled",
        };

        // =====================================================================
        // Controller ports
        // =====================================================================
        public override void ConfigureControllerPorts(LibretroCore core)
        {
            // Dolphin's BootCore (called inside retro_load_game) expects all 4 ports
            // to receive retro_set_controller_port_device before retro_run. Without
            // this, the input subsystem is left partially initialised and retro_run crashes.
            for (uint port = 0; port < 4; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        // =====================================================================
        // CPU core mode — toggle at runtime via UseJit property
        // =====================================================================

        /// <summary>
        /// When true, selects JIT64 for full-speed emulation.
        /// (The old "JIT64 requires fastmem=disabled" note was the Windows SEH concern;
        /// on Linux JIT64 + fastmem chain signals cleanly — see the A/B note above.)
        /// If the game crashes on launch, set this back to false.
        /// </summary>
        public bool UseJit { get; set; } = true;

        public override void OnVariableAnnounced(string key, string[] validValues,
            Dictionary<string, string> coreOptions)
        {
            if (key != "dolphin_cpu_core" || validValues.Length == 0)
                return;

            string? pick;
            if (UseJit)
            {
                // JIT64: native recompilation, ~5x faster than CachedInterpreter.
                // fastmem is already disabled above so the VEH/SEH conflict is avoided.
                pick = validValues.FirstOrDefault(v => v == "1")
                    ?? validValues.FirstOrDefault(v => v.IndexOf("jit64", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? validValues.FirstOrDefault(v => v.IndexOf("jit",   StringComparison.OrdinalIgnoreCase) >= 0);
            }
            else
            {
                // CachedInterpreter: safe fallback if JIT64 is unstable.
                pick = validValues.FirstOrDefault(v => v == "5")
                    ?? validValues.FirstOrDefault(v => v.IndexOf("cachedinterpreter",  StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? validValues.FirstOrDefault(v => v.IndexOf("cached interpreter", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? validValues.FirstOrDefault(v => v == "0")
                    ?? validValues.FirstOrDefault(v => v.IndexOf("interpreter",        StringComparison.OrdinalIgnoreCase) >= 0
                                                    && v.IndexOf("jit",                StringComparison.OrdinalIgnoreCase) <  0);
            }

            string selected = pick ?? validValues[0];
            coreOptions[key] = selected;
            System.Diagnostics.Trace.WriteLine($"[GameCubeHandler] dolphin_cpu_core SELECT (UseJit={UseJit}): '{selected}' from [{string.Join(", ", validValues)}]");
        }

        // =====================================================================
        // D3D11 blocking around context_reset — Windows-only; no-op on Linux.
        // (No d3d11.dll exists; the Linux Dolphin core has no Direct3D backend.)
        // =====================================================================
        public override void OnBeforeContextReset() { /* no-op on Linux */ }
        public override void OnAfterContextReset()  { /* no-op on Linux */ }

        // Dolphin needs OpenGL Core profile and requires shared context support.
        // RETRO_HW_CONTEXT_OPENGL_CORE = 3
        public override int PreferredHwContext => 3;
        // With dolphin_main_cpu_thread=disabled Dolphin renders on retro_run's thread (our
        // _emuThread).  There is no separate Dolphin EmuThread, so we must NOT release the
        // GL context after context_reset — it must stay current for get_current_framebuffer
        // and retro_run.  Setting false keeps the context current and skips the N64-style
        // context release that caused ctx=0x0 on every GCF call.
        public override bool AllowHwSharedContext => false;
        // Dolphin creates its own EmuThread context against an internal DC, not ours,
        // so presenting our overlay back buffer shows an empty frame (black screen).
        // Use the same FBO-0 readback path as N64 instead.
        public override bool UseEmbeddedWindow => false;

        // GL overlay (cog menu, save/load slots, cheats panel) is incompatible
        // with rendering directly to FBO 0, so users who flip the AMD/Intel
        // compatibility option in Preferences trade the overlay for working
        // GameCube video.
        public override bool UseGLOverlay => !UseDefaultFramebuffer;

        public override bool UseDefaultFramebuffer =>
            App.Configuration?.GetEmulatorConfiguration().ResolveAmdIntelCompat() ?? false;

        // Remove the 1x and 2x Internal Resolution choices: at those scales dolphin_libretro's
        // OGL present centers its output in a believed-backbuffer smaller than our FBO, so the
        // game renders into the lower-left corner of the window (the low-res cornering bug —
        // see project_gamecube_lowres_cornering). It renders correctly from ~3x up; default is 4x.
        public override string[] FilterCoreOptionValues(string key, string[] values)
        {
            if (key == "dolphin_efb_scale")
                return values.Where(v => v.Trim() != "1" && v.Trim() != "2").ToArray();
            return values;
        }

        // Use the core's parent directory as the system directory so that
        // dolphin-emu/Sys/ can be placed alongside dolphin_libretro.so.
        public override string ResolveSystemDirectory(string defaultDir, string coreDllDir)
            => coreDllDir;
    }
}
