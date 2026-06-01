using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Emutastic.Configuration;
using Emutastic.Emulator;
using Emutastic.Models;
using Emutastic.Services;
using Emutastic.ViewModels;

namespace Emutastic.Views;

/// <summary>
/// Main-window shell + U1a interactions: bootstrap (DB/config/services/VM on the UI thread),
/// launch a game (resolve .so core via CoreManager → EmulatorWindow), and import ROMs (file
/// picker + drag-drop → ImportService, with progress surfaced through the VM banner).
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private DatabaseService? _db;
    private CoreManager? _coreManager;
    private ImportService? _importer;
    private ArtworkFetchService? _artworkFetch;

    public MainWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("MaximizeButton")!.Click += (_, _) => ToggleMaximize();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        var titleBar = this.FindControl<Grid>("CustomTitleBar")!;
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else BeginMoveDrag(e);
            }
        };

        // Launch on double-click / Enter from the library grid.
        var grid = this.FindControl<ListBox>("GameGridView")!;
        grid.DoubleTapped += (_, _) => LaunchSelected();
        grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) { LaunchSelected(); e.Handled = true; } };
        grid.AddHandler(ContextRequestedEvent, OnGameContextRequested);

        // List view (DataGrid) shares the launch + context-menu gestures.
        var list = this.FindControl<DataGrid>("GameListView")!;
        list.DoubleTapped += (_, _) => LaunchSelected();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { LaunchSelected(); e.Handled = true; } };
        list.AddHandler(ContextRequestedEvent, OnGameContextRequested);

        // Drag-drop ROM import.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Search box → debounced VM search (scoped to the current console when one is selected).
        var search = this.FindControl<TextBox>("SearchBox")!;
        var searchClear = this.FindControl<Button>("SearchClear")!;
        search.TextChanged += (_, _) =>
        {
            searchClear.IsVisible = !string.IsNullOrEmpty(search.Text);
            if (_vm == null) return;
            string? scope = !_vm.IsMixedView && _vm.SelectedConsole != "All Games" ? _vm.SelectedConsole : null;
            _ = _vm.SearchGames(search.Text ?? "", scope)
                   .ContinueWith(t => { if (t.IsFaulted) System.Diagnostics.Trace.WriteLine($"search faulted: {t.Exception}"); },
                                 System.Threading.Tasks.TaskScheduler.Default);
        };
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) search.Text = ""; };
        searchClear.Click += (_, _) => search.Text = "";

        // Toolbar tabs (Library active; Save States/Screenshots/Achievements land in U3/U8).
        foreach (var name in new[] { "TabLibrary", "TabSaveStates", "TabScreenshots", "TabAchievements" })
            this.FindControl<ToggleButton>(name)!.Click += OnTabClick;

        // View-mode toggles (grid live; list view lands in U2).
        this.FindControl<ToggleButton>("ViewGrid")!.Click += OnViewToggle;
        this.FindControl<ToggleButton>("ViewList")!.Click += OnViewToggle;

        Opened += OnOpened;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Tab strip: keep one checked; Library shows the grid, the others land in U3/U8 (status note for now).
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        foreach (var name in new[] { "TabLibrary", "TabSaveStates", "TabScreenshots", "TabAchievements" })
        {
            var tab = this.FindControl<ToggleButton>(name)!;
            tab.IsChecked = ReferenceEquals(tab, clicked);
        }
        if ((clicked.Tag as string) != "Library")
            _vm?.SetStatus($"{clicked.Content} view is coming soon.", autoClear: true);
    }

    // View-mode toggle: switch between the box-art grid and the list (DataGrid).
    private void OnViewToggle(object? sender, RoutedEventArgs e)
    {
        bool list = (sender as ToggleButton)?.Tag as string == "List";
        this.FindControl<ToggleButton>("ViewGrid")!.IsChecked = !list;
        this.FindControl<ToggleButton>("ViewList")!.IsChecked = list;
        this.FindControl<ListBox>("GameGridView")!.IsVisible = !list;
        this.FindControl<DataGrid>("GameListView")!.IsVisible = list;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (Environment.GetEnvironmentVariable("EMUTASTIC_SHOT") == "1")
        {
            Topmost = true;
            WindowState = WindowState.Maximized;
        }

        // GOLDEN RULE: construct services + VM on the UI thread (they capture
        // SynchronizationContext.Current); the heavy library read runs off-thread.
        App.Configuration ??= new JsonConfigurationService();
        _db = new DatabaseService();
        _coreManager = new CoreManager(App.Configuration);
        _importer = new ImportService(_db, _coreManager, App.Configuration);
        _vm = new MainViewModel(_db);
        _artworkFetch = new ArtworkFetchService(_db, new ArtworkService(), _vm);
        WireImportEvents();
        DataContext = _vm;

        // Apply the saved/default theme palette into Application.Resources (enables Light/OLED/Midnight;
        // for Dark this matches the static DarkTheme.axaml values).
        try
        {
            string? themeId = App.Configuration?.GetThemeConfiguration()?.ActiveThemeId;
            ThemeService.Instance.LoadAndApplyTheme(string.IsNullOrEmpty(themeId) ? "builtin.dark" : themeId);
        }
        catch { /* theme apply is best-effort; static dark palette is the fallback */ }

        // Sidebar OPTIONS buttons.
        var importBtn = this.FindControl<Button>("ImportButton");
        if (importBtn != null) importBtn.Click += (_, _) => RunGuarded(PickAndImportAsync);
        var prefsBtn = this.FindControl<Button>("PreferencesButton");
        if (prefsBtn != null) prefsBtn.Click += (_, _) => _vm?.SetStatus("Preferences is coming soon.", autoClear: true);

        Task.Run(() =>
        {
            _vm.Reload();
            Dispatcher.UIThread.Post(() =>
            {
                _vm.NavigateToAllGamesCommand.Execute(null);
                if (Environment.GetEnvironmentVariable("EMUTASTIC_SHOT") == "list")
                    OnViewToggle(this.FindControl<ToggleButton>("ViewList"), null!);
            });
        });
    }

    // ── Launch ─────────────────────────────────────────────────────────────
    private Game? SelectedGame()
    {
        var list = this.FindControl<DataGrid>("GameListView");
        if (list is { IsVisible: true } && list.SelectedItem is Game lg) return lg;
        return this.FindControl<ListBox>("GameGridView")?.SelectedItem as Game;
    }

    private void LaunchSelected()
    {
        if (SelectedGame() is Game g) LaunchGame(g);
    }

    private void LaunchGame(Game game)
    {
        string? corePath = _coreManager?.GetCorePathForGame(game);
        if (string.IsNullOrEmpty(corePath))
        {
            _vm?.SetStatus($"No core installed for {game.Console} — download one in Preferences → Cores.", autoClear: true);
            return;
        }
        string romPath = AppPaths.FromStoragePath(game.RomPath);
        if (!System.IO.File.Exists(romPath))
        {
            _vm?.SetStatus($"ROM file not found: {romPath}", autoClear: true);
            return;
        }
        try
        {
            new EmulatorWindow(new EmulatorSession(corePath, romPath)).Show();
        }
        catch (Exception ex)
        {
            _vm?.SetStatus($"Failed to launch: {ex.Message}", autoClear: true);
        }
    }

    // ── Import ─────────────────────────────────────────────────────────────
    // Hint the importer with the current console only when a specific console is selected (IsMixedView
    // is false for console navs; true for All Games / Recent / Favorites / Recently Added).
    private string? ImportConsoleHint() =>
        _vm != null && !_vm.IsMixedView && !string.IsNullOrEmpty(_vm.SelectedConsole) && _vm.SelectedConsole != "All Games"
            ? _vm.SelectedConsole : null;

    private async Task PickAndImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import ROMs",
            AllowMultiple = true,
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count > 0) _importer?.ImportFilesAsync(paths, ImportConsoleHint());
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return;
        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count > 0) _importer?.ImportFilesAsync(paths, ImportConsoleHint());
    }

    // ── Context menu (game card) ─────────────────────────────────────────────
    private void OnGameContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not Game g) return;
        BuildGameContextMenu(g).Open((Control)e.Source!);
        e.Handled = true;
    }

    private static MenuItem MenuAction(string header, Action onClick, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (enabled) mi.Click += (_, _) => onClick();
        return mi;
    }

    private ContextMenu BuildGameContextMenu(Game game)
    {
        var menu = new ContextMenu();
        var items = menu.Items;

        items.Add(MenuAction("▶  Play Game", () => LaunchGame(game)));

        // Play Save State — deferred until save-state loading lands (U9).
        items.Add(new MenuItem { Header = "⏱  Play Save State", IsEnabled = false });

        bool fav = game.IsFavorite;
        items.Add(MenuAction(fav ? "♥  Remove from Favorites" : "♡  Add to Favorites", () =>
        {
            game.IsFavorite = !game.IsFavorite;
            _db!.ToggleFavorite(game.Id, game.IsFavorite);
            _vm!.RefreshGame(game);
            if (_vm.IsShowingFavorites) _vm.LoadFavorites(_db);
        }));

        items.Add(new Separator());

        // Rating submenu
        var rating = new MenuItem { Header = "⭐  Rating" };
        foreach (var (label, value) in new[] { ("None", 0), ("★☆☆☆☆", 1), ("★★☆☆☆", 2), ("★★★☆☆", 3), ("★★★★☆", 4), ("★★★★★", 5) })
        {
            int v = value;
            rating.Items.Add(MenuAction((game.Rating == v ? "✓ " : "    ") + label, () =>
            {
                game.Rating = v; _db!.UpdateRating(game.Id, v); _vm!.RefreshGame(game);
            }));
        }
        items.Add(rating);

        items.Add(new Separator());

        // Deferred to their splinters (disabled stubs).
        items.Add(new MenuItem { Header = "📝  Notes", IsEnabled = false });           // U7
        items.Add(new MenuItem { Header = "📖  Manual", IsEnabled = false });          // U7
        if (!game.HasPatch && RomPatcher.SupportedConsoles.Contains(game.Console))
            items.Add(new MenuItem { Header = "🧩  Apply ROM Hack…", IsEnabled = false }); // later

        items.Add(MenuAction("📁  Show in Files", () =>
        {
            string rom = AppPaths.FromStoragePath(game.RomPath);
            string? dir = System.IO.Path.GetDirectoryName(rom);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                try { System.Diagnostics.Process.Start("xdg-open", dir); } catch { }
        }));

        items.Add(new Separator());

        items.Add(MenuAction("⬇  Download Cover Art", () => RunGuarded(async () =>
        {
            var (art, ss) = await _artworkFetch!.FetchSingleGameArtworkAsync(game);
            if (art == null && ss == null)
                await new ConfirmDialog("Artwork", "Could not find artwork for this game.", "OK", infoOnly: true).ShowDialog<bool>(this);
        })));

        items.Add(MenuAction("🖼  Add Cover Art from File…", () => RunGuarded(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Cover Art",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" } } },
            });
            string? src = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(src)) return;
            string dest = System.IO.Path.Combine(AppPaths.GetFolder("Artwork", game.Console),
                $"{game.RomHash}_custom{System.IO.Path.GetExtension(src)}");
            System.IO.File.Copy(src, dest, overwrite: true);
            _db!.UpdateCoverArt(game.Id, dest);
            // Evict the dest path (its bytes just changed) + the old path, then force a change
            // notification even when dest == the current CoverArtPath (the Game setter no-ops on equal).
            Converters.PathToImageConverter.Evict(dest);
            if (!string.IsNullOrEmpty(game.CoverArtPath) && game.CoverArtPath != dest)
                Converters.PathToImageConverter.Evict(game.CoverArtPath);
            game.CoverArtPath = "";
            game.CoverArtPath = dest;
            _vm!.RefreshGame(game);
        })));

        // Add to Collection — deferred until the collections sidebar lands.
        items.Add(new MenuItem { Header = "📂  Add to Collection", IsEnabled = false });

        items.Add(new Separator());

        items.Add(MenuAction("✏  Rename Game", () => RunGuarded(async () =>
        {
            string? newTitle = await new RenameWindow(game.Title).ShowDialog<string?>(this);
            if (string.IsNullOrEmpty(newTitle)) return;
            game.Title = newTitle;
            _db!.UpdateTitle(game.Id, newTitle);
            _vm!.RefreshGame(game);
        })));

        items.Add(MenuAction("🗑  Remove from Library", () => RunGuarded(async () =>
        {
            bool ok = await new ConfirmDialog("Remove Game",
                $"Remove \"{game.Title}\" from your library? (The ROM file is not deleted.)",
                "Remove", danger: true).ShowDialog<bool>(this);
            if (!ok) return;
            _db!.DeleteGame(game.Id);
            _vm!.RemoveGame(game);
        })));

        return menu;
    }

    // Runs an async menu action without letting an unhandled exception escape as an
    // async-void throw that would crash the dispatcher; surfaces failures in the status banner.
    private async void RunGuarded(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _vm?.SetStatus($"Action failed: {ex.Message}", autoClear: true); }
    }

    private void WireImportEvents()
    {
        if (_importer == null || _vm == null) return;

        _importer.StatusChanged += msg => Dispatcher.UIThread.Post(() =>
        {
            _vm.IsImporting = _importer!.IsImporting;
            _vm.ImportStatusText = msg;
        });

        _importer.ProgressChanged += (current, total) => Dispatcher.UIThread.Post(() =>
        {
            if (total == 0) return;
            _vm.IsImporting = _importer!.IsImporting;
            if (current >= total) { _vm.ImportProgressPercent = 100; return; }
            int pct = (int)(current / (double)total * 100);
            _vm.ImportStatusText = $"Importing… {pct}%  ({current} of {total})";
            _vm.ImportProgressPercent = pct;
        });

        // Ambiguous imports (.bin/.iso/.chd with no DAT match) → ask the user which system.
        _importer.AmbiguousConsoleResolver = (fileName, candidates) =>
        {
            var tcs = new TaskCompletionSource<string?>();
            Dispatcher.UIThread.Post(async () =>
            {
                try { tcs.SetResult(await new ConsolePickerWindow(fileName, candidates).ShowDialog<string?>(this)); }
                catch { tcs.SetResult(null); }
            });
            return tcs.Task;
        };

        _importer.GameImported += game => Dispatcher.UIThread.Post(() => _vm!.RefreshGame(game));

        _importer.ImportQueueDrained += () => Dispatcher.UIThread.Post(async () =>
        {
            await Task.Run(() => _vm!.Reload());
            await _vm!.FilterGamesAsync();
            _vm.IsImporting = false;
            _vm.ImportStatusText = "";
            _vm.ImportProgressPercent = 0;
        });
    }
}
