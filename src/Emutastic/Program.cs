using Avalonia;
using System;

namespace Emutastic;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless data-layer self-test (no Avalonia/window): `Emutastic --selftest-library <rom>`.
        // Verifies ROM identification + the SQLite library round-trip at runtime.
        // Portable mode FIRST, for EVERY entry path (library, game-host, selftests,
        // previews) — anything below may touch AppPaths.DataRoot. Idempotent; the
        // later per-path calls are harmless.
        Emutastic.AppPaths.DetectPortableMode(args);

        if (args.Length >= 1 && args[0] == "--selftest-library")
        {
            Emutastic.SelfTest.RunLibrary(args.Length > 1 ? args[1] : null);
            return;
        }
        if (args.Length >= 3 && args[0] == "--selftest-import")
        {
            Emutastic.SelfTest.RunImport(args[1], args[2]);
            return;
        }
        // Separate game process (Branch B): runs the SDL-GL game window with NO Avalonia in this process
        // (Avalonia + SDL-GL in one process hangs after present #1). Exit code propagates to the parent
        // supervisor for crash detection. See docs/gl-present-phase1-host-process-design.md.
        if (args.Length >= 1 && args[0] == "--game-host")
        {
            Environment.Exit(Emutastic.GameHost.Run(args));
            return;
        }
        // Dev-only: cloud-sync engine selftest against the real GitHub API with an
        // injected token (EMUTASTIC_SYNC_TOKEN); bypasses the OAuth device flow.
        if (args.Length >= 1 && args[0] == "--selftest-cloudsync")
        {
            Environment.Exit(Emutastic.Services.CloudSyncSelfTest.Run());
            return;
        }
        // Dev-only: in-app-update selftest — `Emutastic --selftest-update` runs the
        // full check→pick→download→apply pipeline headlessly against EMUTASTIC_UPDATE_API
        // (a local mock in tests). On success the process hands off to the relaunch
        // script and EXITS; the relaunched binary is the proof.
        if (args.Length >= 1 && args[0] == "--selftest-update")
        {
            Environment.Exit(Emutastic.Services.UpdateSelfTest.Run());
            return;
        }
        // Dev-only: RetroAchievements native-foundation selftest — verifies the marshaled
        // struct layouts against the checkabi numbers and exercises rc_client create/destroy
        // through librcheevos.so. `Emutastic --ra-selftest`. No window, no network, no login.
        if (args.Length >= 1 && args[0] == "--ra-selftest")
        {
            Environment.Exit(Emutastic.Services.RaSelfTest.Run());
            return;
        }
        // Dev-only: render the in-game OSD (status line + HUD pill) to PNGs for an aesthetic check —
        // `Emutastic --osd-preview [outDir]`. Pure Skia (GlOsd), no GL/window. See Platform/GlOsd.cs.
        if (args.Length >= 1 && args[0] == "--osd-preview")
        {
            Emutastic.Platform.OsdPreview.Run(args.Length > 1 ? args[1] : "/tmp/osd");
            return;
        }
        // Overlay feasibility test (EMUTASTIC_OVERLAY_TEST=1): in-process full-screen GL game + a
        // transparent Avalonia overlay floated on top. Force the modes it needs; App builds the overlay
        // window for the 2-file direct launch. GAMEHOST=0 keeps the game in-process (skips the host route).
        if (Environment.GetEnvironmentVariable("EMUTASTIC_OVERLAY_TEST") == "1")
        {
            Environment.SetEnvironmentVariable("EMUTASTIC_PRESENT", "gl");
            Environment.SetEnvironmentVariable("EMUTASTIC_GAMEHOST", "0");   // in-process so the overlay can track the game window
            // WINDOWED (not fullscreen) so the overlay is visible over the game and we test windowed play.
            Emutastic.AppPaths.DetectPortableMode(args);                     // resolve the Logs dir before logging
            Emutastic.Services.EmuLog.Setup("emulator-overlay.log");        // guarantee the GlStats readout is captured
        }
        // Direct-launch `Emutastic <core> <rom>`: in GL mode run it as the separate game host (no Avalonia);
        // otherwise fall through to App's legacy in-process EmulatorWindow path.
        if (args.Length >= 2 && !args[0].StartsWith("--")
            && System.IO.File.Exists(args[0]) && System.IO.File.Exists(args[1])
            && string.Equals((Environment.GetEnvironmentVariable("EMUTASTIC_PRESENT") ?? "").Trim(), "gl", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("EMUTASTIC_GAMEHOST") != "0")   // =0 forces in-process (A/B)
        {
            Environment.Exit(Emutastic.GameHost.Run(new[] { "--game-host", args[0], args[1] }));
            return;
        }
        // Headless LibVLC native-init check (U4b): `Emutastic --selftest-vlc`.
        // Proves Core.Initialize() + new LibVLC resolve system libvlc on this box.
        if (args.Length >= 1 && args[0] == "--selftest-vlc")
        {
            try
            {
                var lib = Emutastic.Services.VideoPlaybackService.Instance.GetLibVLCAsync()
                    .GetAwaiter().GetResult();
                Console.WriteLine($"=== PASS (LibVLC initialized: {lib.Version}) ===");
            }
            catch (Exception ex) { Console.WriteLine($"=== FAIL: {ex.Message} ==="); }
            return;
        }

        // Timeline anchors for the cold-start hunt:
        //  • "+Nms since exec" = time from process launch to reaching Main = .NET runtime load + JIT
        //    of the startup path (the part ReadyToRun precompilation would cut).
        //  • The gap from here to the first App.* phase = Avalonia platform init (X11 + Skia +
        //    HarfBuzz + Inter font / fontconfig) — happens before the window maps.
        try
        {
            var sinceExec = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Emutastic.Services.StartupTrace.Mark($"program_main_start (+{sinceExec.TotalMilliseconds:F0}ms since exec)");
        }
        catch { Emutastic.Services.StartupTrace.Mark("program_main_start"); }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        // X11 + Skia explicitly (Linux target). We don't reference Avalonia.Desktop — see the
        // vendored-Avalonia note in the csproj — so UsePlatformDetect() isn't available here.
        => AppBuilder.Configure<App>()
            .UseX11()
            .UseSkia()
            .UseHarfBuzz()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
