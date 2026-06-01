using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Emutastic.Services;
using Emutastic.ViewModels;

namespace Emutastic.Views;

/// <summary>
/// M4c main-window shell: title bar + sidebar + flat box-art grid + status banner.
/// Minimal bootstrap here (DB + MainViewModel on the UI thread) so the shell renders real
/// library data; full service wiring (import, launch, ArtworkFetchService, sliders) lands in M4d.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // Window chrome buttons (traffic lights).
        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("MaximizeButton")!.Click += (_, _) => ToggleMaximize();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        // Title-bar drag + double-click to maximize (custom chrome / ExtendClientArea).
        var titleBar = this.FindControl<Grid>("CustomTitleBar")!;
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else BeginMoveDrag(e);
            }
        };

        Opened += OnOpened;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnOpened(object? sender, EventArgs e)
    {
        // Diagnostic: EMUTASTIC_SHOT=1 opens maximized + on-top for a clean verification screenshot.
        if (Environment.GetEnvironmentVariable("EMUTASTIC_SHOT") == "1")
        {
            Topmost = true;
            WindowState = WindowState.Maximized;
        }

        // GOLDEN RULE: construct the VM on the UI thread (it captures SynchronizationContext.Current),
        // but keep the heavy library read off the UI thread.
        var db = new DatabaseService();
        _vm = new MainViewModel(db);
        DataContext = _vm;

        System.Threading.Tasks.Task.Run(() =>
        {
            _vm.Reload();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                _vm.NavigateToAllGamesCommand.Execute(null));
        });
    }
}
