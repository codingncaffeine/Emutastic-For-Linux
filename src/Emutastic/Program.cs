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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
