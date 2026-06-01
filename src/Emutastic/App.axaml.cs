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

    /// <summary>
    /// Push the saved library-layout settings (grid padding, card width, card spacing) into the
    /// app-level DynamicResources the box-art grid binds to. Called at startup and live whenever
    /// the Theme panel's Layout sliders move, so the grid re-lays out immediately.
    /// </summary>
    public static void ApplyLibraryLayout()
    {
        var t = Configuration?.GetThemeConfiguration();
        if (t == null || Current == null) return;
        int padding = System.Math.Clamp(t.GridPadding, 8, 64);
        int cardW   = System.Math.Clamp(t.CardWidth, 148, 280);
        int spacing = System.Math.Clamp(t.CardSpacing, 4, 96);
        Current.Resources["LibraryGridPadding"] = new Thickness(padding);
        Current.Resources["LibraryCardWidth"]   = (double)cardW;
        Current.Resources["LibraryCardMargin"]  = new Thickness(0, 0, spacing, spacing);
    }

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