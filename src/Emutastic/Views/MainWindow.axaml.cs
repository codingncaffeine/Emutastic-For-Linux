using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
        grid.Tapped += OnGameTapped;                       // single-click → open detail (upstream UX)
        grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelectedDetail(); e.Handled = true; } };
        grid.AddHandler(ContextRequestedEvent, OnGameContextRequested);

        // List view (DataGrid) shares the launch + context-menu gestures.
        var list = this.FindControl<DataGrid>("GameListView")!;
        list.DoubleTapped += (_, _) => OpenSelectedDetail();   // list rows: double-click → open detail
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelectedDetail(); e.Handled = true; } };
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
            if (_suppressSearchTextChanged) return;
            string text = search.Text ?? "";
            searchClear.IsVisible = !string.IsNullOrEmpty(text);
            // Route the query to whichever tab is active (each keeps its own filter).
            // Save States / Screenshots repopulate synchronously on the UI thread, so
            // debounce keystrokes (matches upstream's 180ms cancellable delay).
            string tab = ActiveTab();
            if (tab is "SaveStates" or "Screenshots") { DebounceTabSearch(tab, text); return; }
            if (_vm == null) return;
            string? scope = !_vm.IsMixedView && _vm.SelectedConsole != "All Games" ? _vm.SelectedConsole : null;
            _ = _vm.SearchGames(text, scope)
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

    // Tab strip: keep one checked and swap the content view. Library / Save States /
    // Screenshots are live; Achievements lands in U8 (status note + blank content).
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        foreach (var name in new[] { "TabLibrary", "TabSaveStates", "TabScreenshots", "TabAchievements" })
        {
            var tab = this.FindControl<ToggleButton>(name)!;
            tab.IsChecked = ReferenceEquals(tab, clicked);
        }
        ShowTab((clicked.Tag as string) ?? "Library");
    }

    private bool _suppressSearchTextChanged;
    private System.Threading.CancellationTokenSource? _tabSearchCts;

    // Swap the three content containers (and populate the on-demand views).
    private void ShowTab(string tag)
    {
        var library = this.FindControl<Grid>("LibraryView");
        var saves   = this.FindControl<Grid>("SaveStatesView");
        var shots   = this.FindControl<Grid>("ScreenshotsView");
        if (library != null) library.IsVisible = tag == "Library";
        if (saves   != null) saves.IsVisible   = tag == "SaveStates";
        if (shots   != null) shots.IsVisible   = tag == "Screenshots";

        // Reset the shared search box for the new tab (upstream Tab_Click clears the
        // query, retargets the placeholder, and hides the box on Achievements).
        var searchBox    = this.FindControl<TextBox>("SearchBox");
        var searchBorder = this.FindControl<Border>("SearchBorder");
        var searchClear  = this.FindControl<Button>("SearchClear");
        _suppressSearchTextChanged = true;
        if (searchBox != null)
        {
            searchBox.Text = "";
            searchBox.PlaceholderText = tag switch
            {
                "SaveStates"  => "Search save states…",
                "Screenshots" => "Search screenshots…",
                _              => "Search games…",
            };
        }
        _saveStatesSearchQuery  = "";
        _screenshotsSearchQuery = "";
        if (searchClear  != null) searchClear.IsVisible  = false;
        if (searchBorder != null) searchBorder.IsVisible = tag != "Achievements";
        _suppressSearchTextChanged = false;

        switch (tag)
        {
            case "Library":
                // Clear any prior library filter so a query typed on Library doesn't linger.
                if (_vm != null)
                {
                    string? scope = !_vm.IsMixedView && _vm.SelectedConsole != "All Games" ? _vm.SelectedConsole : null;
                    _ = _vm.SearchGames("", scope);
                }
                break;
            case "SaveStates":  PopulateSaveStatesView();  break;
            case "Screenshots": PopulateScreenshotsView(); break;
            case "Achievements":
                _vm?.SetStatus("Achievements dashboard is coming soon.", autoClear: true);
                break;
        }
    }

    // Debounce Save States / Screenshots search: cancel the prior pending repopulate,
    // wait 180ms, then repopulate on the UI thread (Task.Delay resumes on the captured
    // UI SynchronizationContext). Avoids re-enumerating/decoding on every keystroke.
    private async void DebounceTabSearch(string tab, string text)
    {
        _tabSearchCts?.Cancel();
        var cts = _tabSearchCts = new System.Threading.CancellationTokenSource();
        try { await Task.Delay(180, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (cts.Token.IsCancellationRequested) return;
        if (tab == "SaveStates")  { _saveStatesSearchQuery  = text; PopulateSaveStatesView();  }
        else if (tab == "Screenshots") { _screenshotsSearchQuery = text; PopulateScreenshotsView(); }
    }

    // ── Resource lookup helpers (code-behind built controls reuse theme tokens) ──
    private static IBrush? Brush(string key)
    {
        var app = Application.Current!;
        return app.TryGetResource(key, app.ActualThemeVariant, out var v) ? v as IBrush : null;
    }
    private static FontFamily Font(string key)
    {
        var app = Application.Current!;
        return app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is FontFamily f ? f : FontFamily.Default;
    }
    private static Avalonia.Media.Imaging.Bitmap? DecodeThumb(string path, int width)
    {
        try { using var fs = System.IO.File.OpenRead(path); return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, width); }
        catch { return null; }
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

        // Warm LibVLC off the UI thread so the first detail-card snap video doesn't
        // pay the multi-second native init on the dispatcher.
        VideoPlaybackService.Instance.StartWarmup();

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
        if (prefsBtn != null) prefsBtn.Click += (_, _) => new PreferencesWindow().Show(this);

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

    // ── Game detail card (U4) ───────────────────────────────────────────────
    private GameDetailWindow? _openDetailWindow;

    // Single-click a box-art card → open its detail window (upstream UX). Shift+click
    // is reserved for range-select, so it never opens the card.
    private void OnGameTapped(object? sender, TappedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (e.Source is Control c && c.DataContext is Game g) OpenGameDetail(g);
    }

    private void OpenSelectedDetail()
    {
        if (SelectedGame() is Game g) OpenGameDetail(g);
    }

    private void OpenGameDetail(Game game)
    {
        _openDetailWindow?.Close();
        var win = _openDetailWindow = new GameDetailWindow(game);
        win.Closed += async (_, _) =>
        {
            _openDetailWindow = null;
            // If the game was removed via the detail card's "Remove from Library", refresh.
            if (_db != null && !_db.GameExists(game.Id))
            {
                _vm?.RemoveGame(game);
                if (_vm != null) await _vm.FilterGamesAsync();
            }
        };
        win.Show(this);
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

    // ════════════════════════════════════════════════════════════════════════
    //  U3 — Save States + Screenshots tabs (ported from upstream code-behind)
    // ════════════════════════════════════════════════════════════════════════

    private string _saveStatesSearchQuery  = "";
    private string _screenshotsSearchQuery = "";
    private readonly HashSet<string> _selectedScreenshots = new();

    private string ActiveTab()
    {
        foreach (var name in new[] { "TabSaveStates", "TabScreenshots", "TabAchievements" })
            if (this.FindControl<ToggleButton>(name)?.IsChecked == true) return (string)name[3..]; // "SaveStates" etc.
        return "Library";
    }

    // ── Save States ─────────────────────────────────────────────────────────
    private void PopulateSaveStatesView()
    {
        var panel = this.FindControl<StackPanel>("SaveStatesPanel");
        var emptyText = this.FindControl<TextBlock>("SaveStatesEmptyText");
        if (panel == null || _db == null) return;
        panel.Children.Clear();

        var allStates = _db.GetAllSaveStates();

        string rawQuery = (_saveStatesSearchQuery ?? "").Trim();
        bool hasQuery = rawQuery.Length > 0;
        if (hasQuery)
        {
            var tokens = rawQuery.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(MainViewModel.NormalizeForSearch).Where(t => t.Length > 0).ToArray();
            if (tokens.Length > 0)
                allStates = allStates.Where(s =>
                {
                    string text = MainViewModel.NormalizeForSearch(
                        (s.GameTitle ?? "") + "|" + (s.ConsoleName ?? "") + "|" + (s.Name ?? "") + "|" + (s.CoreName ?? ""));
                    return tokens.All(t => text.Contains(t, StringComparison.Ordinal));
                }).ToList();
        }

        if (allStates.Count == 0)
        {
            if (emptyText != null)
            {
                emptyText.Text = hasQuery
                    ? $"No save states match \"{rawQuery}\""
                    : "No save states yet. Press F5 or the Save State button while in a game.";
                emptyText.IsVisible = true;
            }
            return;
        }
        if (emptyText != null) emptyText.IsVisible = false;

        // Group per game; key on RomHash when present, else normalized title+console.
        static string GroupKey(SaveState s) =>
            !string.IsNullOrEmpty(s.RomHash)
                ? "hash:" + s.RomHash.ToLowerInvariant()
                : "title:" + (s.GameTitle ?? "").Trim().ToLowerInvariant() + "|" + (s.ConsoleName ?? "").Trim().ToLowerInvariant();

        var grouped = allStates.GroupBy(GroupKey)
            .Select(g => new
            {
                Title   = g.Select(x => x.GameTitle).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "",
                Console = g.Select(x => x.ConsoleName).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "",
                States  = g.OrderByDescending(x => x.CreatedAt).ToList(),
            })
            .OrderBy(g => g.Title).ThenBy(g => g.Console);

        foreach (var group in grouped)
        {
            panel.Children.Add(BuildGroupHeader(
                string.IsNullOrEmpty(group.Title) ? "Deleted Game" : group.Title, group.Console));
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 8, 16, 0) };
            foreach (var s in group.States) wrap.Children.Add(BuildSaveStateCard(s));
            panel.Children.Add(wrap);
        }
    }

    // OpenEmu-style section header: full-width engraved bar, title left / system right.
    private Control BuildGroupHeader(string gameTitle, string consoleName)
    {
        var border = new Border
        {
            Background      = Brush("ToolbarRaisedFillBrush"),
            BorderBrush     = Brush("ToolbarChiselBrush"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin          = new Thickness(0, 16, 0, 0),
            Height          = 32,
        };
        var grid = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var topInner = new Border { BorderBrush = Brush("ToolbarTopHighlightBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        var name = new TextBlock
        {
            Text = gameTitle, FontFamily = Font("PrimaryFont"), FontSize = 13, FontWeight = FontWeight.SemiBold,
            Foreground = Brush("ToolbarRaisedTextBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = new DropShadowEffect { BlurRadius = 0, OffsetX = 0, OffsetY = 1, Opacity = 0.85, Color = Colors.Black },
        };
        var system = new TextBlock
        {
            Text = consoleName, FontFamily = Font("PrimaryFont"), FontSize = 11, Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(system, 1);
        grid.Children.Add(name);
        grid.Children.Add(system);
        var stack = new Panel();
        stack.Children.Add(topInner);
        stack.Children.Add(grid);
        border.Child = stack;
        return border;
    }

    private Control BuildSaveStateCard(SaveState s)
    {
        var normal  = new SolidColorBrush(Color.Parse("#1F1F21"));
        var hover   = new SolidColorBrush(Color.Parse("#2A2A2D"));
        var card = new Border
        {
            Width = 148, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Hand), Background = normal,
        };
        var stack = new StackPanel();

        var thumb = new Border { Height = 100, ClipToBounds = true, Background = Brushes.Black };
        if (s.ScreenshotPath.Length > 0 && System.IO.File.Exists(s.ScreenshotPath))
        {
            var bmp = DecodeThumb(s.ScreenshotPath, 296);
            if (bmp != null) thumb.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        }
        stack.Children.Add(thumb);

        var info = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        info.Children.Add(new TextBlock
        {
            Text = s.Name, FontFamily = Font("PrimaryFont"), FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = s.GameTitle, FontFamily = Font("PrimaryFont"), FontSize = 10, Foreground = Brush("TextMutedBrush"),
            Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = s.RelativeTime, FontFamily = Font("PrimaryFont"), FontSize = 10, Foreground = Brush("TextMutedBrush"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        stack.Children.Add(info);
        card.Child = stack;

        card.PointerEntered += (_, _) => card.Background = hover;
        card.PointerExited  += (_, _) => card.Background = normal;
        card.Tapped         += (_, _) => LaunchWithSaveState(s);
        card.ContextMenu     = BuildSaveStateContextMenu(s);
        return card;
    }

    private ContextMenu BuildSaveStateContextMenu(SaveState s)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuAction("▶  Load State", () => LaunchWithSaveState(s)));
        menu.Items.Add(MenuAction("✏  Rename", () => RunGuarded(async () =>
        {
            string? newName = await new RenameWindow(s.Name).ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(newName)) return;
            string safeName = new string(newName.Select(c =>
                System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            string dir      = System.IO.Path.GetDirectoryName(s.StatePath) ?? "";
            string newState = System.IO.Path.Combine(dir, safeName + ".state");
            string newPng   = System.IO.Path.Combine(dir, safeName + ".png");
            string newJson  = System.IO.Path.Combine(dir, safeName + ".json");
            string oldJson  = System.IO.Path.ChangeExtension(s.StatePath, ".json");
            try
            {
                if (System.IO.File.Exists(s.StatePath))      System.IO.File.Move(s.StatePath, newState, overwrite: true);
                if (System.IO.File.Exists(s.ScreenshotPath)) System.IO.File.Move(s.ScreenshotPath, newPng, overwrite: true);
                if (System.IO.File.Exists(oldJson))          System.IO.File.Move(oldJson, newJson, overwrite: true);
            }
            catch (Exception ex)
            {
                await new ConfirmDialog("Error", $"Rename failed: {ex.Message}", "OK", infoOnly: true).ShowDialog<bool>(this);
                return;
            }
            _db!.UpdateSaveStateName(s.Id, newName, newState, newPng);
            PopulateSaveStatesView();
        })));
        menu.Items.Add(new Separator());
        var del = MenuAction("🗑  Delete", () => RunGuarded(async () =>
        {
            bool ok = await new ConfirmDialog("Delete Save State",
                $"Delete \"{s.Name}\"? This cannot be undone.", "Delete", danger: true).ShowDialog<bool>(this);
            if (!ok) return;
            try { if (System.IO.File.Exists(s.StatePath))      System.IO.File.Delete(s.StatePath);      } catch { }
            try { if (System.IO.File.Exists(s.ScreenshotPath)) System.IO.File.Delete(s.ScreenshotPath); } catch { }
            try { string p = System.IO.Path.ChangeExtension(s.StatePath, ".png");  if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
            try { string j = System.IO.Path.ChangeExtension(s.StatePath, ".json"); if (System.IO.File.Exists(j)) System.IO.File.Delete(j); } catch { }
            _db!.DeleteSaveState(s.Id);
            PopulateSaveStatesView();
        }));
        del.Foreground = new SolidColorBrush(Color.Parse("#FF5F57"));
        menu.Items.Add(del);
        return menu;
    }

    // Loading directly into a save state needs the in-game save/load runtime (M9);
    // for now this launches the game so the action is live, with a note.
    private void LaunchWithSaveState(SaveState s)
    {
        var game = _db?.GetGameById(s.GameId);
        if (game == null)
        {
            _vm?.SetStatus("Game not found in library.", autoClear: true);
            return;
        }
        LaunchGame(game);
        _vm?.SetStatus("Loading directly into a save state arrives with the in-game save/load UI.", autoClear: true);
    }

    // ── Screenshots ───────────────────────────────────────────────────────────
    private void PopulateScreenshotsView()
    {
        var panel = this.FindControl<StackPanel>("ScreenshotsPanel");
        var emptyState = this.FindControl<StackPanel>("ScreenshotsEmptyState");
        var emptyIcon = this.FindControl<TextBlock>("ScreenshotsEmptyIcon");
        var emptyHeadline = this.FindControl<TextBlock>("ScreenshotsEmptyHeadline");
        var emptyHint = this.FindControl<TextBlock>("ScreenshotsEmptyHint");
        if (panel == null) return;
        panel.Children.Clear();
        _selectedScreenshots.Clear();

        var screenshots = new ScreenshotService().GetAll();

        string rawQuery = (_screenshotsSearchQuery ?? "").Trim();
        bool hasQuery = rawQuery.Length > 0;
        if (hasQuery)
        {
            var tokens = rawQuery.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(MainViewModel.NormalizeForSearch).Where(t => t.Length > 0).ToArray();
            if (tokens.Length > 0)
                screenshots = screenshots.Where(s =>
                {
                    string fname = "";
                    try { fname = System.IO.Path.GetFileNameWithoutExtension(s.FilePath) ?? ""; } catch { }
                    string text = MainViewModel.NormalizeForSearch((s.GameTitle ?? "") + "|" + (s.Console ?? "") + "|" + fname);
                    return tokens.All(t => text.Contains(t, StringComparison.Ordinal));
                }).ToList();
        }

        if (screenshots.Count == 0)
        {
            if (emptyState != null) emptyState.IsVisible = true;
            if (hasQuery)
            {
                if (emptyIcon != null) emptyIcon.Text = "⌕";
                if (emptyHeadline != null) emptyHeadline.Text = $"No screenshots match \"{rawQuery}\"";
                if (emptyHint != null) emptyHint.IsVisible = false;
            }
            else
            {
                if (emptyIcon != null) emptyIcon.Text = "📷";
                if (emptyHeadline != null) emptyHeadline.Text = "Screenshots will appear here when they've been saved.";
                if (emptyHint != null) emptyHint.IsVisible = true;
            }
            return;
        }
        if (emptyState != null) emptyState.IsVisible = false;

        static string GroupKey(Screenshot s) =>
            (s.GameTitle ?? "").Trim().ToLowerInvariant() + "|" + (s.Console ?? "").Trim().ToLowerInvariant();

        var grouped = screenshots.GroupBy(GroupKey)
            .Select(g => new
            {
                Title   = g.Select(x => x.GameTitle).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "",
                Console = g.Select(x => x.Console).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "",
                Items   = g.OrderByDescending(x => x.TakenAt).ToList(),
            })
            .OrderBy(g => g.Title).ThenBy(g => g.Console);

        foreach (var group in grouped)
        {
            panel.Children.Add(BuildGroupHeader(
                string.IsNullOrEmpty(group.Title) ? "Deleted Game" : group.Title, group.Console));
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 8, 16, 0) };
            foreach (var ss in group.Items) wrap.Children.Add(BuildScreenshotCard(ss));
            panel.Children.Add(wrap);
        }
    }

    private Control BuildScreenshotCard(Screenshot ss)
    {
        var selectedBrush = new SolidColorBrush(Color.Parse("#E03535"));
        IBrush normalBrush = Brushes.Transparent;

        var card = new Border
        {
            Width = 240, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand), BorderThickness = new Thickness(2), BorderBrush = normalBrush,
        };
        var inner = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#1F1F21")) };
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = ss.Console, FontFamily = Font("PrimaryFont"), FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = Brush("AccentBrush"), Margin = new Thickness(8, 6, 8, 4),
        });

        var imgBorder = new Border { Height = 135, ClipToBounds = true, Background = Brushes.Black };
        if (System.IO.File.Exists(ss.FilePath))
        {
            var bmp = DecodeThumb(ss.FilePath, 240);
            if (bmp != null) imgBorder.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        }
        stack.Children.Add(imgBorder);

        stack.Children.Add(new TextBlock
        {
            Text = ss.GameTitle, FontFamily = Font("PrimaryFont"), FontSize = 12, FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimaryBrush"), Margin = new Thickness(8, 6, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text = ss.TakenAtDisplay, FontFamily = Font("PrimaryFont"), FontSize = 10, Foreground = Brush("TextMutedBrush"),
            Margin = new Thickness(8, 0, 8, 8),
        });

        inner.Child = stack;
        card.Child = inner;

        // Shift+click toggles selection; plain click opens the file in the system viewer.
        card.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (_selectedScreenshots.Contains(ss.FilePath)) { _selectedScreenshots.Remove(ss.FilePath); card.BorderBrush = normalBrush; }
                else { _selectedScreenshots.Add(ss.FilePath); card.BorderBrush = selectedBrush; }
                e.Handled = true;
            }
            else
            {
                try { System.Diagnostics.Process.Start("xdg-open", ss.FilePath); } catch { }
            }
        };

        // Right-click → delete selection (or this one).
        card.ContextRequested += (_, e) =>
        {
            var paths = _selectedScreenshots.Count > 0 ? _selectedScreenshots.ToList() : new List<string> { ss.FilePath };
            string label = paths.Count == 1 ? "🗑  Delete Screenshot" : $"🗑  Delete {paths.Count} Screenshots";
            var menu = new ContextMenu();
            menu.Items.Add(MenuAction(label, () => RunGuarded(() => DeleteScreenshotsWithConfirm(paths))));
            menu.Open(card);
            e.Handled = true;
        };
        return card;
    }

    private async Task DeleteScreenshotsWithConfirm(List<string> paths)
    {
        string msg = paths.Count == 1 ? "Delete this screenshot?" : $"Delete {paths.Count} screenshots?";
        bool ok = await new ConfirmDialog("Delete Screenshots", msg, "Delete", danger: true).ShowDialog<bool>(this);
        if (!ok) return;
        foreach (string path in paths) { try { System.IO.File.Delete(path); } catch { } }
        _selectedScreenshots.Clear();
        PopulateScreenshotsView();
    }
}
