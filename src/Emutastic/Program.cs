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
