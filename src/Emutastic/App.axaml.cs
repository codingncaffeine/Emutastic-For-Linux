using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Emutastic.Configuration;
using Emutastic.Views;

namespace Emutastic;

public partial class App : Application
{
    /// <summary>
    /// Global configuration service (matches upstream App.Configuration). Set during
    /// startup; consulted by services/handlers (e.g. ConsoleHandlers' AMD/Intel-compat check).
    /// </summary>
    public static IConfigurationService? Configuration { get; internal set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? System.Array.Empty<string>();

            // Portable / flash-drive mode (day-one feature): portable.txt next to the
            // executable, or --portable, forces config+data into [exe]/PortableData/.
            AppPaths.DetectPortableMode(args);

            // Direct-launch shortcut for verification: `Emutastic <core.so> <rom>`.
            var files = args.Where(a => !a.StartsWith("--") && System.IO.File.Exists(a)).ToArray();
            if (files.Length >= 2)
            {
                desktop.MainWindow = new Emutastic.Views.EmulatorWindow(
                    new Emutastic.Emulator.EmulatorSession(files[0], files[1]));
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}