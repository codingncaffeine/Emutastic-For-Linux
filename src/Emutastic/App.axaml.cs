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
    /// The console tag currently being browsed in a single-console library view (null in
    /// All Games / Recent / Favorites / Collection views). Read by <see cref="ApplyLibraryLayout"/>
    /// so it honors that console's per-console spacing override on ANY trigger — including a
    /// Theme-panel layout change or a prefs save — instead of being stomped back to the global value.
    /// </summary>
    public static string? ActiveConsoleTag { get; set; }

    /// <summary>
    /// SOLE writer of <c>LibraryGridPadding</c>, <c>LibraryCardWidth</c>, and <c>LibraryCardMargin</c>.
    /// Reads theme config, applies safety clamps, and — when <see cref="ActiveConsoleTag"/> is set —
    /// honors that console's per-console spacing override before falling back to the global
    /// <c>CardSpacing</c>. Both global writers (Theme-panel sliders, prefs save) and the per-console
    /// writer (toolbar H/V slider) MUST route through here; a direct write to these three resources
    /// from anywhere else re-creates the multi-writer bug that stomps per-console margins on save.
    /// Called at startup, on navigation, and live whenever a layout control moves.
    /// </summary>
    public static void ApplyLibraryLayout()
    {
        var t = Configuration?.GetThemeConfiguration();
        if (t == null || Current == null) return;
        int padding = System.Math.Clamp(t.GridPadding, 8, 64);
        int cardW   = System.Math.Clamp(t.CardWidth, 148, 280);
        var (h, v)  = ResolvePerConsoleSpacing(ActiveConsoleTag);
        Current.Resources["LibraryGridPadding"] = new Thickness(padding);
        Current.Resources["LibraryCardWidth"]   = (double)cardW;
        Current.Resources["LibraryCardMargin"]  = new Thickness(0, 0, h, v);
    }

    /// <summary>
    /// Resolve the (H, V) card spacing for a console: its per-console override if one exists and
    /// parses cleanly, otherwise the global <c>CardSpacing</c>. All values clamped to 4–96 so a
    /// malformed config can never break the grid. Single source of truth shared by
    /// <see cref="ApplyLibraryLayout"/> and the toolbar slider handlers.
    /// </summary>
    public static (int H, int V) ResolvePerConsoleSpacing(string? console)
    {
        var t = Configuration?.GetThemeConfiguration();
        int globalFallback = System.Math.Clamp(t?.CardSpacing ?? 20, 4, 96);
        if (t == null || string.IsNullOrEmpty(console))
            return (globalFallback, globalFallback);

        if (t.PerConsoleSpacing != null
            && t.PerConsoleSpacing.TryGetValue(console, out var raw)
            && raw.Split(',') is var parts && parts.Length == 2
            && int.TryParse(parts[0], out int h)
            && int.TryParse(parts[1], out int v))
        {
            return (System.Math.Clamp(h, 4, 96), System.Math.Clamp(v, 4, 96));
        }
        return (globalFallback, globalFallback);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Point the Active{Min,Max,Close}Button resources at one of the macOS / Windows 11 /
    /// Linux-native button ControlThemes (defined in DarkTheme). Title bars bind these via
    /// DynamicResource, so the choice applies live everywhere. Called at startup and from the
    /// Theme panel's Window Style picker.
    /// </summary>
    public static void ApplyWindowButtonStyle(string? style)
    {
        if (Current == null) return;
        string p = style switch { "Windows11" => "Win11", "Linux" => "Linux", _ => "Mac" };
        foreach (var role in new[] { "Min", "Max", "Close" })
            if (Current.TryGetResource($"{p}{role}Button", null, out var theme) && theme != null)
                Current.Resources[$"Active{role}Button"] = theme;

        // Per-style layout of the window-button strip (title bars bind these via DynamicResource).
        // Windows 11 caption buttons sit FLUSH in the top-right corner — no right margin, no spacing
        // between them, top-aligned — so the close button's red hover fills the corner (the window's
        // ClipToBounds rounds it). macOS/Linux traffic-lights are centred with a right inset + spacing.
        bool win11 = p == "Win11";
        Current.Resources["WinBtnStripMargin"]  = win11 ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        Current.Resources["WinBtnStripSpacing"] = win11 ? 0.0 : 6.0;
        Current.Resources["WinBtnStripVAlign"]  = win11 ? Avalonia.Layout.VerticalAlignment.Top
                                                        : Avalonia.Layout.VerticalAlignment.Center;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? System.Array.Empty<string>();

            // Route Trace.WriteLine (used throughout import + the libretro run/log callbacks) to
            // stderr when no debugger is attached, so the [Import]/[Emu]/[core:] diagnostics are
            // visible when running the app from a terminal (matches upstream).
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Trace.Listeners.Clear();
                System.Diagnostics.Trace.Listeners.Add(
                    new System.Diagnostics.ConsoleTraceListener(useErrorStream: true));
            }

            // ── Global crash diagnostics (ports upstream's App.OnStartup handlers) → Logs/crash.log ──
            // Background-thread exceptions (e.g. a Task.Run without await).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Services.CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as System.Exception);
            // Faulted fire-and-forget Tasks whose exception was never observed (we have several).
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Services.CrashLog.Write("UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
            // Exceptions on the Avalonia UI dispatcher — log and keep running (Handled = true), the
            // Avalonia equivalent of WPF's DispatcherUnhandledException.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Services.CrashLog.Write("Dispatcher.UnhandledException", e.Exception);
                e.Handled = true;
            };

            // Portable / flash-drive mode (day-one feature): portable.txt next to the
            // executable, or --portable, forces config+data into [exe]/PortableData/.
            AppPaths.DetectPortableMode(args);

            // Load persisted config now — BEFORE any window is created — so each window can restore
            // its saved size/position in its constructor without a visible resize flash. MainWindow's
            // OnOpened re-uses this same instance (its ??= is a no-op once set).
            var swCfg = Services.StartupTrace.Start();
            Configuration ??= new JsonConfigurationService();
            try { System.Threading.Tasks.Task.Run(() => Configuration!.LoadAsync()).GetAwaiter().GetResult(); }
            catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"config load failed: {ex.Message}"); }
            Services.StartupTrace.Stop("App.LoadConfiguration", swCfg);

            // Apply the SAVED window-button style before any window's XAML resolves the Active*Button
            // resources, so the buttons resolve to the right style at construction. (Config is loaded
            // just above.) Previously this hard-seeded "macOS" and relied on MainWindow re-applying the
            // saved style live — but the Theme DynamicResource doesn't reliably re-theme already-built
            // buttons, so a saved Windows11 choice stayed showing the bright macOS traffic-light red.
            ApplyWindowButtonStyle(Configuration?.GetThemeConfiguration()?.WindowButtonStyle ?? "macOS");

            // Start the UI-freeze watchdog BEFORE the window so any stall during first render is
            // logged to Logs/ui_freezes.log. Stop it centrally on app exit — NOT per-window — so it's
            // covered no matter which window is the main one (MainWindow or a direct-launch
            // EmulatorWindow) or how the app shuts down.
            Services.UiFreezeWatchdog.Instance.Start(Avalonia.Threading.Dispatcher.UIThread);
            desktop.Exit += (_, _) => Services.UiFreezeWatchdog.Instance.Stop();
            Services.StartupTrace.Mark("watchdog_started");

            // Direct-launch shortcut for verification: `Emutastic <core.so> <rom>`.
            var files = args.Where(a => !a.StartsWith("--") && System.IO.File.Exists(a)).ToArray();
            var swWin = Services.StartupTrace.Start();
            if (files.Length >= 2)
            {
                var session = new Emutastic.Emulator.EmulatorSession(files[0], files[1]);
                // Overlay feasibility test: a transparent overlay floated over the in-process GL game.
                desktop.MainWindow = System.Environment.GetEnvironmentVariable("EMUTASTIC_OVERLAY_TEST") == "1"
                    ? new Emutastic.Views.GameOverlayWindow(session)
                    : new Emutastic.Views.EmulatorWindow(session);
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
            Services.StartupTrace.Stop("App.CreateMainWindow", swWin);

            // Cloud sync: restore the saved GitHub session, then validate + load the
            // remote manifest AFTER a 10s grace so first-launch rendering and artwork
            // aren't competing with network calls (upstream's timing).
            Services.GitHubSyncService.Instance.LoadFromConfig();
            if (Services.GitHubSyncService.Instance.IsAuthenticated)
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(10));
                    await Services.GitHubSyncService.Instance.ValidateTokenAsync();
                    if (Services.GitHubSyncService.Instance.IsAuthenticated)
                        await Services.GitHubSyncService.Instance.LoadManifestAsync();
                });

            // Finish any recording encode that a crash or hard power-off interrupted — the raw
            // frames + .meta.json sidecar are still on disk. Library launches only (the game-host
            // direct-launch path must not race a concurrently running library instance over the
            // same files). Background thread: this can run real ffmpeg encodes.
            if (files.Length < 2)
                System.Threading.Tasks.Task.Run(Services.RecordingService.RecoverInterrupted);
        }

        base.OnFrameworkInitializationCompleted();
    }
}