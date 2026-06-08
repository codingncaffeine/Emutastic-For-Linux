using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Default implementation. Suitable for any console that doesn't need special handling.
    /// Override only the methods that differ from these generic defaults.
    /// </summary>
    public abstract class ConsoleHandlerBase : IConsoleHandler
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public abstract string ConsoleName { get; }
        public virtual bool UsesAnalogStick => false;
        public virtual bool PromoteAnalogStickToDpad => false;

        /// <summary>The loaded core's file stem (e.g. "mupen64plus_next_libretro"), set by
        /// EmulatorSession before GetDefaultCoreOptions/GetVisualOptions. Lets a console with
        /// multiple cores (N64: parallel_n64 vs mupen64plus_next) return core-specific options.</summary>
        public string CoreFileName { get; set; } = "";

        public virtual Dictionary<string, string> GetDefaultCoreOptions()
            => new Dictionary<string, string>();

        /// <summary>
        /// Option keys that must NOT be visible to the core during retro_load_game — they're
        /// held back and applied through the live variables-dirty path right after the first
        /// frame instead (exactly what a cog-menu change does). For cores whose load-time init
        /// mis-sizes state when an option is pre-seeded (azahar: citra_resolution_factor
        /// pre-seeded at 10 → av_info max geometry comes out 640x480 → 10x render clipped to a
        /// corner sliver; deferred → full 7200x4800 buffer, applies cleanly at frame 1).
        /// </summary>
        public virtual IReadOnlyCollection<string> DeferUntilAfterLoad => System.Array.Empty<string>();

        public virtual void ConfigureControllerPorts(LibretroCore core)
        {
            for (uint port = 0; port < 4; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        public virtual void OnVariableAnnounced(string key, string[] validValues,
            Dictionary<string, string> coreOptions)
        { }

        public virtual double GetDisplayAspectRatio(uint baseWidth, uint baseHeight, float coreAspectRatio)
        {
            if (coreAspectRatio > 0.01f) return coreAspectRatio;
            if (baseHeight > 0) return (double)baseWidth / baseHeight;
            return 0;
        }

        public virtual double HardwareTargetFps => -1;

        public virtual void OnBeforeContextReset() { }
        public virtual void OnAfterContextReset() { }

        public virtual int PreferredHwContext => -1;
        // When true, a core-profile GL request (SET_HW_RENDER type 3) is downgraded to a
        // versionless compatibility context. Needed for cores that bootstrap GL through
        // GLEW (PPSSPP): glewInit() can't enumerate extensions on a core profile, so it
        // fails outright. Windows never exposes this because WGL's legacy context is
        // always compatibility profile; Mesa's compat profile carries GL 4.6 so the core
        // still gets every feature it would have had on core.
        public virtual bool ForceCompatibilityGlProfile => false;
        // RetroArch-style audio-backpressure pacing (see IConsoleHandler). Default off: most cores
        // pace fine on the audio-progress budget. PPSSPP self-paces to wall clock and needs this.
        public virtual bool PaceByAudioBackpressure => false;
        public virtual bool AllowHwSharedContext => false;
        public virtual bool UseEmbeddedWindow => false;

        public virtual string ResolveSystemDirectory(string defaultDir, string coreDllDir)
            => defaultDir;

        public virtual void PrepareSaveDirectory(string saveDir) { }
        public virtual bool UseFullFboReadback => false;
        public virtual bool UseGLOverlay => false;
        public virtual bool UseDefaultFramebuffer => false;
        public virtual string[] FilterCoreOptionValues(string key, string[] values) => values;

        public virtual List<(string key, string label)> GetVisualOptions() => new();
    }
}
