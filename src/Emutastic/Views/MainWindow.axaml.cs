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
using Avalonia.LogicalTree;
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
    public ArtworkFetchService? ArtworkFetch => _artworkFetch;   // detail card reaches this via Owner

    public MainWindow()
    {
        InitializeComponent();

        // Restore the saved window size/position from the previous session (config is loaded at App
        // startup, before this ctor runs) and persist it on close. Done here, pre-show, so there's
        // no resize flash.
        RestoreWindowBounds();
        Closing += (_, _) => SaveWindowBounds();

        Platform.WindowResize.Enable(this);   // edge/corner resize for the borderless window
        Activated += OnMainActivated;          // click back on the app → dismiss the game-detail card

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
        // Manual WPF-Extended-style selection (Avalonia's Multiple mode accumulates on plain
        // click, which we don't want): tunnel the press so we drive selection + detail ourselves.
        grid.AddHandler(PointerPressedEvent, OnGamePointerPressed, RoutingStrategies.Tunnel);
        grid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelectedDetail(); e.Handled = true; }
            else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { grid.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Delete) { RunGuarded(() => DeleteGamesWithConfirmAsync(SelectedGames())); e.Handled = true; }
        };
        grid.AddHandler(ContextRequestedEvent, OnGameContextRequested);

        // List view (DataGrid) shares the launch + context-menu gestures.
        var list = this.FindControl<DataGrid>("GameListView")!;
        // Alternating row fills (upstream ListRowItemStyle #FF161616/#FF1E1E1E via AlternationIndex;
        // Avalonia has no alternation, so paint per-row as rows realize/recycle).
        list.LoadingRow += (_, e) =>
            e.Row.Background = (e.Row.Index % 2 == 0) ? RowFillEven : RowFillOdd;
        // Sort persistence (upstream GameListColumnHeader_Click → listSortColumn/listSortDirection):
        // the control sorts on header click; we persist the result after it lands.
        list.Sorting += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Read the result off the collection view once the click's sort has landed.
            var sd = list.CollectionView?.SortDescriptions?.OfType<Avalonia.Collections.DataGridSortDescription>().FirstOrDefault();
            if (sd?.PropertyPath is not { Length: > 0 } path) return;
            App.Configuration?.SetValue("listSortColumn", HeaderToSortMember(path));
            App.Configuration?.SetValue("listSortDirection",
                sd.Direction == System.ComponentModel.ListSortDirection.Descending ? "Descending" : "Ascending");
        });
        // Saved sort (default Title ascending) once the first ItemsSource has landed.
        Dispatcher.UIThread.Post(ApplySavedListSort, DispatcherPriority.Loaded);
        list.DoubleTapped += (_, _) => OpenSelectedDetail();   // list rows: double-click → open detail
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelectedDetail(); e.Handled = true; }
            else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { list.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Delete) { RunGuarded(() => DeleteGamesWithConfirmAsync(SelectedGames())); e.Handled = true; }
        };
        list.AddHandler(ContextRequestedEvent, OnGameContextRequested);

        // Right-click a console in the left nav → console context menu.
        this.FindControl<StackPanel>("SidebarPanel")!.AddHandler(ContextRequestedEvent, OnConsoleContextRequested);

        // Drag-drop ROM import. NOTE: Avalonia 12.0.4's X11 backend does not implement an XDND
        // drop target (no XdndAware is set on the window), so external file drops are never
        // delivered on Linux/X11 — the file-picker import (toolbar "Import") is the working path.
        // These handlers are correct and will start working once the platform adds X11 DnD.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        // Search box → debounced VM search (scoped to the current console when one is selected).
        var search = this.FindControl<TextBox>("SearchBox")!;
        var searchClear = this.FindControl<Button>("SearchClear")!;
        var searchCount = this.FindControl<TextBlock>("SearchResultCount")!;
        search.TextChanged += (_, _) =>
        {
            if (_suppressSearchTextChanged) return;
            string text = search.Text ?? "";
            searchClear.IsVisible = !string.IsNullOrEmpty(text);
            // Count readout only while a query is typed (upstream: DataTrigger on
            // SearchBox.Text). On the Library tab only — Save States / Screenshots
            // searches don't go through the VM, so GameCountText would be stale.
            searchCount.IsVisible = !string.IsNullOrEmpty(text) && ActiveTab() == "Library";
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

        // 2D / 3D box-art toggle (hidden until the current view has 3D art).
        this.FindControl<ToggleButton>("BoxArt2D")!.Click += OnBoxArtToggle;
        this.FindControl<ToggleButton>("BoxArt3D")!.Click += OnBoxArtToggle;

        // Per-console card spacing: H/V cap flips the axis; slider drags persist that axis.
        this.FindControl<Border>("SpacingHVCap")!.PointerPressed += (_, _) => SpacingHVToggle();
        var spacingSlider = this.FindControl<Slider>("SpacingSliderToolbar")!;
        spacingSlider.PropertyChanged += (_, e) =>
        {
            if (_spacingControlSuppressEvents || e.Property.Name != nameof(Slider.Value)) return;
            OnSpacingSliderChanged(spacingSlider.Value);
        };

        Opened += OnOpened;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ── Window size/position persistence (mirrors upstream Restore/SaveMainWindowBounds) ──
    private void RestoreWindowBounds()
    {
        try
        {
            var cfg = App.Configuration;
            if (cfg == null) return;

            double w = cfg.GetValue("mainWinWidth", 0.0);
            double h = cfg.GetValue("mainWinHeight", 0.0);
            int x = cfg.GetValue("mainWinLeft", int.MinValue);
            int y = cfg.GetValue("mainWinTop", int.MinValue);
            bool maximized = cfg.GetValue("mainWinMaximized", false);

            if (w >= MinWidth && h >= MinHeight)
            {
                Width = w;
                Height = h;
            }
            if (x != int.MinValue && y != int.MinValue)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(x, y);
            }
            if (maximized)
                WindowState = WindowState.Maximized;
        }
        catch { /* fall back to the XAML default size */ }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var cfg = App.Configuration;
            if (cfg == null) return;

            cfg.SetValue("mainWinMaximized", WindowState == WindowState.Maximized);
            // Only capture size/position in the Normal state, so we restore the pre-maximize bounds.
            // ClientSize is the reliable current size for our borderless custom-chrome window
            // (Width/Height aren't updated by the platform on a user resize).
            if (WindowState == WindowState.Normal)
            {
                cfg.SetValue("mainWinWidth", ClientSize.Width);
                cfg.SetValue("mainWinHeight", ClientSize.Height);
                cfg.SetValue("mainWinLeft", Position.X);
                cfg.SetValue("mainWinTop", Position.Y);
            }
            // Persist off the UI thread. Calling SaveAsync directly on the UI thread here deadlocks:
            // it captures the UI context across its awaits while the OnOpened Closing handler blocks
            // the UI thread on Task.Run(SaveAsync).GetResult() waiting for the same _saveLock.
            _ = System.Threading.Tasks.Task.Run(() => cfg.SaveAsync());
        }
        catch { /* best-effort on close */ }
    }

    // Tab strip: keep one checked and swap the content view. Library / Save States /
    // Screenshots / Achievements are all live (Achievements = the A8f RA tab).
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        ActivateTab((clicked.Tag as string) ?? "Library");
    }

    /// <summary>Switches the active top-level tab: toggle states + content swap +
    /// search routing. Shared by OnTabClick and OnNavigated — sidebar navigation
    /// must land in the Library tab (upstream 6e1f073).</summary>
    private void ActivateTab(string tag)
    {
        foreach (var name in new[] { "TabLibrary", "TabSaveStates", "TabScreenshots", "TabAchievements" })
        {
            var tab = this.FindControl<ToggleButton>(name)!;
            tab.IsChecked = (tab.Tag as string) == tag;
        }
        _activeTab = tag;
        ShowTab(tag);
    }

    private string _activeTab = "Library";
    private Services.ControllerManager? _hotplugMgr;   // hotplug status toasts (disposed on close)

    // Live refresh: the session-end save-state ingest fires SaveStatesChanged — if the user is
    // sitting ON the Save States tab, repopulate so the new state appears without a tab switch.
    // (Wired from the ctor; the event had no subscribers before — fired into the void.)
    private void OnSaveStatesChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (_activeTab == "SaveStates") PopulateSaveStatesView(); });

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
        var ach = this.FindControl<Grid>("AchievementsView");
        if (ach != null) ach.IsVisible = tag == "Achievements";
        if (tag == "Achievements") PopulateAchievementsView();

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
        // The clear above is suppressed, so TextChanged won't hide the count readout.
        if (this.FindControl<TextBlock>("SearchResultCount") is { } searchCount)
            searchCount.IsVisible = false;
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
            // Achievements: the dashboard partial (MainWindow.RaTab) owns its own activation —
            // nothing to do here (a stale "coming soon" status used to flash on every switch).
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
        ApplyCurrentViewMode(_vm?.IsShowingFavorites == true);
    }

    // ── List-view sort persistence (upstream ApplyListSort/HeaderToSortMember) ────────────────
    private static readonly IBrush RowFillEven = new SolidColorBrush(Color.Parse("#FF161616"));
    private static readonly IBrush RowFillOdd  = new SolidColorBrush(Color.Parse("#FF1E1E1E"));

    private static string HeaderToSortMember(string label) => label switch
    {
        "Name" or "Title" => "Title",
        "Rating"          => "Rating",
        "Last Played" or "LastPlayed" => "LastPlayed",
        "System" or "Console"         => "Console",
        _ => label,
    };

    // Re-applies the persisted list sort (default Title ascending) by driving the column's own
    // Sort() — keeps the header chevron in sync, same end state as upstream's SortDescription.
    private void ApplySavedListSort()
    {
        try
        {
            var list = this.FindControl<DataGrid>("GameListView");
            if (list == null || list.ItemsSource == null) return;
            string member = HeaderToSortMember(App.Configuration?.GetValue("listSortColumn", "Title") ?? "Title");
            var dir = (App.Configuration?.GetValue("listSortDirection", "Ascending")) == "Descending"
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending;
            foreach (var col in list.Columns)
                if (HeaderToSortMember(col.SortMemberPath ?? (col.Header as string ?? "")) == member)
                { col.Sort(dir); break; }
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[ListSort] apply failed: {ex.Message}"); }
    }

    // Upstream ApplyCurrentViewMode — single source of truth for the three content panels:
    // Favorites swaps in its own grouped panel; everything else restores the user's grid/list
    // choice so navigation never desyncs from the toggle.
    private void ApplyCurrentViewMode(bool forceFavorites)
    {
        bool list = this.FindControl<ToggleButton>("ViewList")?.IsChecked == true;
        this.FindControl<ScrollViewer>("FavoritesGroupedView")!.IsVisible = forceFavorites;
        this.FindControl<ListBox>("GameGridView")!.IsVisible = !forceFavorites && !list;
        this.FindControl<DataGrid>("GameListView")!.IsVisible = !forceFavorites && list;
        if (forceFavorites) PopulateFavoritesView();
    }

    // Port of upstream PopulateFavoritesView: per-console headers (alphabetical) + WrapPanels of
    // art-only cards (148px, art height 200, title text fallback). Left-click opens the detail
    // card, right-click the regular game context menu.
    private void PopulateFavoritesView()
    {
        var panel = this.FindControl<StackPanel>("FavoritesPanel");
        if (panel == null || _db == null) return;
        panel.Children.Clear();
        var favs = _db.GetFavorites();

        if (favs.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No favorites yet. Right-click a game and choose Add to Favorites.",
                FontSize = 13,
                Foreground = this.TryFindResource("TextMutedBrush", out var mb) ? mb as IBrush : Brushes.Gray,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0),
            });
            return;
        }

        var headerBrush = this.TryFindResource("TextSecondaryBrush", out var hb) ? hb as IBrush : Brushes.LightGray;
        foreach (var group in favs.GroupBy(g => g.Console).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(group.Key) ? "Unknown" : group.Key,
                FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = headerBrush, Margin = new Thickness(0, 16, 0, 8),
            });

            var wrap = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            foreach (var game in group.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
            {
                var artBorder = new Border { Height = 200, ClipToBounds = true, Background = Brushes.Transparent };
                string? artPath = game.DisplayArtPath;
                if (!string.IsNullOrEmpty(artPath) && System.IO.File.Exists(artPath))
                {
                    // Async decode (cache hit applies instantly) — sync per-card decode froze the
                    // UI for the whole panel build on large favorites lists.
                    var img = new Image { Stretch = Stretch.Uniform };
                    Controls.AsyncImage.SetSourcePath(img, artPath);
                    artBorder.Child = img;
                }
                else
                    artBorder.Child = new TextBlock
                    {
                        Text = game.Title, FontSize = 13, FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Margin = new Thickness(12),
                    };

                var card = new Border
                {
                    Width = 148, Margin = new Thickness(0, 0, 12, 12),
                    CornerRadius = new CornerRadius(8), ClipToBounds = true,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Background = Brushes.Transparent, Child = artBorder, DataContext = game,
                };
                card.PointerPressed += (_, e) =>
                {
                    var props = e.GetCurrentPoint(card).Properties;
                    if (props.IsRightButtonPressed) { BuildGameContextMenu(game).Open(card); e.Handled = true; }
                    else if (props.IsLeftButtonPressed) { OpenGameDetail(game); e.Handled = true; }
                };
                wrap.Children.Add(card);
            }
            panel.Children.Add(wrap);
        }
    }

    // Switches the library between 2D cover art and 3D box art for the current console (or every
    // console in a favorites/grouped view), persists the choice, and rebinds the tiles. Mirrors
    // upstream MainWindow.BoxArtToggle_Click.
    private void OnBoxArtToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || _vm == null) return;
        bool use3D = clicked.Tag as string == "3D";
        this.FindControl<ToggleButton>("BoxArt2D")!.IsChecked = !use3D;
        this.FindControl<ToggleButton>("BoxArt3D")!.IsChecked = use3D;

        if (_vm.IsShowingFavorites)
        {
            foreach (var c in _vm.Games.Select(g => g.Console).Distinct())
            {
                if (use3D) Game.EnableConsole3D(c);
                else       Game.DisableConsole3D(c);
            }
        }
        else
        {
            string console = _vm.SelectedConsole ?? "";
            if (use3D) Game.EnableConsole3D(console);
            else       Game.DisableConsole3D(console);
        }

        // Persist which consoles display 3D box art (restored at startup).
        var snapConfig = App.Configuration?.GetSnapConfiguration();
        if (snapConfig != null)
        {
            snapConfig.Use3DBoxArtConsoles = new System.Collections.Generic.List<string>(Game.Consoles3D);
            App.Configuration!.SetSnapConfiguration(snapConfig);
        }

        // Consoles3D is static, so changing it raises no per-game PropertyChanged — re-seat the
        // games collection (which favorites/all-games/console views all bind to) so DisplayArtPath
        // is re-read and the tiles swap art.
        _vm.RefreshAllGames();
    }

    // Shows the 2D/3D toggle only when the current view has any 3D box art, and syncs the
    // checked segment to the current console's preference. Mirrors upstream UpdateBoxArtToggleVisibility.
    private void UpdateBoxArtToggleVisibility()
    {
        var panel = this.FindControl<Border>("BoxArtTogglePanel");
        if (panel == null || _vm == null) return;

        bool any3D = _vm.Games?.Any(g => !string.IsNullOrEmpty(g.BoxArt3DPath)) == true;
        panel.IsVisible = any3D;
        if (any3D)
        {
            string console = _vm.SelectedConsole ?? "";
            bool is3D = Game.Consoles3D.Contains(console);
            this.FindControl<ToggleButton>("BoxArt2D")!.IsChecked = !is3D;
            this.FindControl<ToggleButton>("BoxArt3D")!.IsChecked = is3D;
        }
    }

    // ── Per-console card spacing (toolbar H/V cap + slider) ──
    private string _spacingAxis = "H";              // which axis the slider currently drives
    private bool _spacingControlSuppressEvents;     // suppress the slider's change handler during a programmatic reload
    private string _currentNavTag = "All Games";    // tag of the library view currently shown

    // Console tags get a per-console spacing control; category views (All Games / Recent /
    // Favorites / RecentlyAdded / Collection:*) use the global spacing and hide the control.
    private static bool IsConsoleTag(string? tag)
        => !string.IsNullOrEmpty(tag)
           && tag != "All Games" && tag != "Recent"
           && tag != "Favorites" && tag != "RecentlyAdded"
           && !tag.StartsWith("Collection:");

    // Runs after every navigation: keeps the 2D/3D toggle and the per-console spacing control in sync.
    private void OnNavigated(string tag)
    {
        // Sidebar navigation always lands in the Library tab. Without this,
        // clicking a console while on Save States/Screenshots/Achievements
        // left the old tab's content on screen and kept the search box
        // routed to that tab's search.
        if (ActiveTab() != "Library") ActivateTab("Library");

        // Navigating away ends the current search (upstream OnNavigated): clear the
        // box with TextChanged suppressed — the freshly navigated view replaces the
        // result set, so no SearchGames("") pass is wanted (the VM cancels any
        // in-flight search itself via CancelInFlightSearch).
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (!string.IsNullOrEmpty(searchBox?.Text))
        {
            _suppressSearchTextChanged = true;
            searchBox!.Text = "";
            _suppressSearchTextChanged = false;
            // Suppressed clear ⇒ TextChanged won't hide these (upstream hides them
            // via Style triggers on SearchBox.Text; ours are toggled in code).
            if (this.FindControl<Button>("SearchClear") is { } sc) sc.IsVisible = false;
            if (this.FindControl<TextBlock>("SearchResultCount") is { } cnt) cnt.IsVisible = false;
        }

        _currentNavTag = tag;
        ApplyCurrentViewMode(tag == "Favorites");   // Favorites → grouped panel; else grid/list per toggle
        // Ghost-card diagnostic (user repro: the just-played game "follows" navigation into other
        // libraries for a while). Discriminates DATA (game wrongly present in the new view's
        // collection) vs VISUAL (a recycled container painting a game that isn't in the
        // collection). Logs to Logs/ghost-diag.log only when something is wrong — cheap otherwise.
        Dispatcher.UIThread.Post(() => GhostCheck(tag), DispatcherPriority.Loaded);
        UpdateBoxArtToggleVisibility();
        UpdateSpacingControl(tag, IsConsoleTag(tag));
        HighlightSidebar(tag);
    }

    // ── Ghost-card diagnostic ───────────────────────────────────────────────────────────────
    private Game? _lastPlayedGame;   // set by the play-stats hook; the ghost is always the played game

    private void GhostCheck(string tag)
    {
        try
        {
            if (_vm == null) return;
            var grid = this.FindControl<ListBox>("GameGridView");
            if (grid == null || !grid.IsVisible) return;
            var inView = new HashSet<Game>(_vm.Games);
            int stale = 0;
            foreach (var c in grid.GetRealizedContainers())
                if (c.DataContext is Game g && !inView.Contains(g))
                {
                    stale++;
                    GhostLog($"STALE-CONTAINER '{g.Title}' ({g.Console}) painted in view '{tag}'");
                }
            if (stale > 0)
                GhostLog($"{stale} stale container(s) in '{tag}'  Games.Count={_vm.Games.Count}");
            // NOTE: no auto-heal here. Collection re-seat was proven futile (the orphan survives
            // swaps) and hiding the container raced the virtualizer's rebind — when the check ran
            // before rebinding finished it hid LIVE containers (blank library). Detect + log only;
            // the root-cause fix targets the item-template bindings / virtualizer interaction.
            if (_lastPlayedGame is { } lp && !string.Equals(lp.Console, tag, StringComparison.OrdinalIgnoreCase)
                && _vm.Games.Any(g => g.Id == lp.Id))
                GhostLog($"DATA-GHOST '{lp.Title}' ({lp.Console}) is IN the Games collection for view '{tag}'  Games.Count={_vm.Games.Count}");
        }
        catch (Exception ex) { GhostLog($"check failed: {ex.Message}"); }
    }

    private static void GhostLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "ghost-diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { /* never throw from logging */ }
    }

    // Keep the selected library/console lit in the sidebar (persistent, same fill as hover) so it's
    // clear which console you're in / importing to. Console buttons match by CommandParameter; the
    // category buttons (All Games / Recent / Favorites / Recently Added) match by Tag.
    private void HighlightSidebar(string tag)
    {
        var panel = this.FindControl<StackPanel>("SidebarPanel");
        if (panel == null) return;
        foreach (var btn in panel.GetLogicalDescendants().OfType<Button>())
        {
            string? btnTag = (btn.CommandParameter as string) ?? (btn.Tag as string);
            btn.Classes.Set("selected", btnTag == tag);
        }
    }

    // Show/hide the toolbar spacing control on navigation and load the active console's values.
    private void UpdateSpacingControl(string tag, bool isConsoleView)
    {
        var panel = this.FindControl<Border>("SpacingControlPanel");
        if (panel == null) return;
        panel.IsVisible = isConsoleView;

        // Mirror the active console on App so the SOLE layout writer honors this console's
        // per-console override on any trigger (prefs save, theme change) instead of stomping it
        // back to the global value. null on category views → global spacing.
        App.ActiveConsoleTag = isConsoleView ? tag : null;
        App.ApplyLibraryLayout();

        if (!isConsoleView) return;
        var (h, v) = App.ResolvePerConsoleSpacing(tag);
        ReloadSpacingSliderValue(h, v);
    }

    // Tap the H/V cap to flip which axis the slider drives, and show that axis's current value.
    private void SpacingHVToggle()
    {
        if (!IsConsoleTag(_currentNavTag)) return;
        _spacingAxis = _spacingAxis == "H" ? "V" : "H";
        var lbl = this.FindControl<TextBlock>("SpacingHVLabel");
        if (lbl != null) lbl.Text = _spacingAxis;
        var (h, v) = App.ResolvePerConsoleSpacing(_currentNavTag);
        ReloadSpacingSliderValue(h, v);
    }

    // Slider drag writes the new value back to this console's per-console spacing for the active axis.
    private void OnSpacingSliderChanged(double value)
    {
        if (!IsConsoleTag(_currentNavTag)) return;
        var (h, v) = App.ResolvePerConsoleSpacing(_currentNavTag);
        int newVal = (int)System.Math.Round(value);
        if (_spacingAxis == "H") h = newVal; else v = newVal;

        var theme = App.Configuration?.GetThemeConfiguration();
        if (theme != null)
        {
            theme.PerConsoleSpacing ??= new();   // guard a hand-edited "perConsoleSpacing": null
            theme.PerConsoleSpacing[_currentNavTag] = $"{h},{v}";
            // SetThemeConfiguration already schedules a debounced save; no direct SaveAsync per
            // drag pixel (the on-close flush persists the final value).
            App.Configuration!.SetThemeConfiguration(theme);
        }
        App.ApplyLibraryLayout();   // route through the sole writer (honors ActiveConsoleTag)
    }

    // Reload the slider's displayed value from per-console state without re-firing the change handler.
    private void ReloadSpacingSliderValue(int h, int v)
    {
        var slider = this.FindControl<Slider>("SpacingSliderToolbar");
        if (slider == null) return;
        _spacingControlSuppressEvents = true;
        slider.Value = _spacingAxis == "H" ? h : v;
        _spacingControlSuppressEvents = false;
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
        // Config is normally loaded at App startup (before any window is created). Only load here as
        // a fallback if that didn't happen — re-loading on the UI thread otherwise just re-reads the
        // file for no reason and slows the open.
        if (App.Configuration == null)
        {
            App.Configuration = new JsonConfigurationService();
            var swCfg = Services.StartupTrace.Start();
            try { Task.Run(() => App.Configuration!.LoadAsync()).GetAwaiter().GetResult(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"config load failed: {ex.Message}"); }
            Services.StartupTrace.Stop("MainWindow.LoadConfiguration", swCfg);
        }
        // Flush pending settings + stop the freeze watchdog on exit.
        Closing += (_, _) =>
        {
            Services.UiFreezeWatchdog.Instance.Stop();
            try { Task.Run(() => App.Configuration?.SaveAsync() ?? Task.CompletedTask).GetAwaiter().GetResult(); }
            catch { /* best-effort on shutdown */ }
        };
        var swSvc = Services.StartupTrace.Start();
        _db = new DatabaseService();
        _coreManager = new CoreManager(App.Configuration);
        _importer = new ImportService(_db, _coreManager, App.Configuration);
        // Any game session ending (whichever window launched it) ingests states the host wrote;
        // DiscoverSaveStates matches .json sidecars by RomHash and fires SaveStatesChanged.
        Services.GameHostLauncher.OnGameSessionEnded = g =>
        {
            _importer?.DiscoverSaveStates(g);
            // Cloud sync: upload the battery save the session just wrote (fire-and-
            // forget, like upstream's game-close hook; no-op when manual timing).
            _ = Services.GitHubSyncService.Instance.UploadSaveAfterSessionAsync(g);
        };
        // RetroAchievements session results: the host writes neither DB nor config, so the
        // Play stats (upstream's UpdatePlayCount at launch + play-time accrual): recorded at
        // session end — DB row + the in-memory Game object, so open views (detail card stat
        // pills, Recently Played) reflect the session without a library reload. Off-UI thread;
        // DB writes are thread-safe and the Game property setters are plain fields.
        Services.GameHostLauncher.OnPlayStats = (g, seconds) =>
        {
            try
            {
                _db?.UpdatePlayCount(g.Id);
                if (seconds > 0) _db?.UpdatePlayTime(g.Id, seconds);
                g.PlayCount++;
                g.LastPlayed = DateTime.Now;
                g.TotalPlayTimeSeconds += seconds;
                _lastPlayedGame = g;   // ghost-card diagnostic: the ghost is always the played game
                GhostLog($"SESSION-END '{g.Title}' ({g.Console})");
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[PlayStats] record failed: {ex.Message}"); }
        };
        // identification outcome, live-progress snapshot, and any refreshed login token are
        // ingested here (off-UI thread — DB + config only, no controls touched).
        Services.GameHostLauncher.OnRaSessionResults = (g, res) =>
        {
            try
            {
                if (res.RaGameId > 0 && g.RAGameId != res.RaGameId)
                {
                    g.RAGameId = res.RaGameId;
                    _db?.UpdateRAGameId(g.Id, res.RaGameId);
                }
                if (!string.IsNullOrEmpty(res.RaOutcome) && g.RALastLaunchOutcome != res.RaOutcome)
                {
                    g.RALastLaunchOutcome = res.RaOutcome;
                    _db?.UpdateRALastLaunchOutcome(g.Id, res.RaOutcome);
                }
                if (!string.IsNullOrEmpty(res.RaLiveProgressJson))
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    g.RALiveProgressJson = res.RaLiveProgressJson!;
                    g.RALiveProgressFetchedAt = now;
                    _db?.UpdateRALiveProgress(g.Id, res.RaLiveProgressJson!, now);
                }
                if (!string.IsNullOrEmpty(res.RaNewToken) && App.Configuration != null)
                {
                    var ra = App.Configuration.GetRetroAchievementsConfiguration();
                    ra.Token = res.RaNewToken!;
                    App.Configuration.SetRetroAchievementsConfiguration(ra);
                    App.Configuration.ScheduleSave();
                }
                // A session just ended → the user-progress cache is stale by definition.
                if (g.RAGameId > 0 && App.Configuration != null && _db != null)
                    new Services.RetroAchievementsService(App.Configuration, _db).InvalidateUserProgressForGame(g);
                // …and so are the Achievements-tab spotlight + recent-unlocks caches.
                try { _raData?.InvalidatePostPlay(); } catch { }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[RA] session ingest failed: {ex.Message}"); }
        };
        // In-game cog → "Edit Game Controls…": the host asks us (UI thread) to open Preferences
        // on the Controls panel with its console preselected.
        Services.GameHostLauncher.OnHostCommand = (verb, arg) =>
        {
            if (verb == "open-controls") PreferencesWindow.OpenOrFocus(this, arg);
            // In-game cog → turbo toggle: "save-turbo <gameId> <port> [csv]". Persist per game here
            // (save-on-click, like upstream's dialog — the host's config copy is read-only).
            else if (verb == "save-turbo" && App.Configuration != null)
            {
                var t = (arg ?? "").Split(' ', 3);
                if (t.Length >= 2 && int.TryParse(t[0], out int turboGameId)
                    && int.TryParse(t[1], out int turboPort) && turboPort is >= 0 and < 4)
                {
                    App.Configuration.SetValue($"turbo_p{turboPort}_{turboGameId}", t.Length == 3 ? t[2] : "");
                    App.Configuration.ScheduleSave();
                }
            }
            // Game window closed → remember its size for that game's next launch.
            else if (verb == "save-win-size" && App.Configuration != null)
            {
                var ws = (arg ?? "").Split(' ');
                if (ws.Length == 3 && int.TryParse(ws[0], out int winGameId)
                    && int.TryParse(ws[1], out int winW) && int.TryParse(ws[2], out int winH))
                {
                    App.Configuration.SetValue($"gameWin_{winGameId}_w", winW);
                    App.Configuration.SetValue($"gameWin_{winGameId}_h", winH);
                    App.Configuration.ScheduleSave();
                }
            }
            // In-game cog → shader pick: persist per game (upstream's shader_{gameId} key).
            else if (verb == "save-shader" && App.Configuration != null)
            {
                var sh = (arg ?? "").Split(' ', 2);
                if (sh.Length == 2 && int.TryParse(sh[0], out int shaderGameId))
                {
                    App.Configuration.SetValue($"shader_{shaderGameId}", sh[1]);
                    App.Configuration.ScheduleSave();
                }
            }
            // Paused right-click cycled the pause effect in the host — persist the pick here
            // (the host's config copy is read-only by convention; one writer avoids clashes).
            else if (verb == "set-pause-effect" && App.Configuration != null)
            {
                var theme = App.Configuration.GetThemeConfiguration();
                theme.PauseEffect = arg;
                App.Configuration.SetThemeConfiguration(theme);
                App.Configuration.ScheduleSave();
            }
            // In-game cog → "Notes"/"Manual": both windows live here (the host has no Avalonia).
            else if (verb == "open-notes" && int.TryParse(arg, out int notesGameId))
            {
                var notesGame = _db?.GetGameById(notesGameId);
                if (notesGame != null) NotesWindow.ShowFor(notesGame, this);
            }
            else if (verb == "open-manual" && int.TryParse(arg, out int manualGameId))
            {
                var manualGame = _db?.GetGameById(manualGameId);
                if (manualGame != null) RunGuarded(() => ManualLauncher.OpenOrDownloadAsync(manualGame, _artworkFetch!));
            }
            // In-game cog → "Add Cheat…": the dialog lives here (the host has no Avalonia).
            // Save appends to the per-game cheat JSON, then the host reloads it live.
            else if (verb == "open-cheat-editor" && int.TryParse(arg, out int cheatGameId))
            {
                var game = _db?.GetGameById(cheatGameId);
                if (game == null) return;
                string corePath = _coreManager?.GetCorePathForGame(game) ?? "";
                RunGuarded(async () =>
                {
                    var dlg = new CheatEditWindow(null, corePath);
                    bool okDlg = await dlg.ShowDialog<bool>(this);
                    if (!okDlg || dlg.DeleteRequested) return;
                    var cheats = Services.CheatService.Load(game);
                    cheats.Add(dlg.Result);
                    Services.CheatService.Save(game, cheats);
                    Services.GameHostLauncher.SendToHost(game.Id, "reload-cheats");
                });
            }
        };
        _vm = new MainViewModel(_db);
        WireRaTab();
        Services.DatabaseService.SaveStatesChanged += OnSaveStatesChanged;
        Closed += (_, _) =>
        {
            Services.DatabaseService.SaveStatesChanged -= OnSaveStatesChanged;
            try { _hotplugMgr?.Dispose(); } catch { }
        };
        _artworkFetch = new ArtworkFetchService(_db, new ArtworkService(), _vm);
        WireImportEvents();
        DataContext = _vm;
        Services.StartupTrace.Stop("MainWindow.CreateServices", swSvc);

        // Apply the saved ScreenScraper thread allowance at startup (upstream MainWindow). Without
        // this, CurrentMaxThreads stays 1 until the user re-runs Test Login, so the metadata/3D-art
        // fetch paths run single-threaded — the cause of slow art downloads on a paid (e.g. 6-thread) account.
        var snapCfg = App.Configuration?.GetSnapConfiguration();
        if (snapCfg != null) Services.ScreenScraperService.SetMaxThreads(snapCfg.ScreenScraperMaxThreads);

        // Restore which consoles display 3D box art (persisted by BoxArtToggle_Click). Matches
        // upstream MainWindow startup — without this the 2D/3D toggle always defaults to 2D.
        if (snapCfg?.Use3DBoxArtConsoles?.Count > 0)
            Models.Game.Consoles3D = new System.Collections.Generic.HashSet<string>(snapCfg.Use3DBoxArtConsoles);

        // The 2D/3D box-art toggle is hidden unless the current view has 3D art. Re-evaluate after
        // every navigation, and force it visible the moment a 3D-art download finishes.
        _vm.Navigated += tag => Dispatcher.UIThread.Post(() => OnNavigated(tag));
        // Ghost-card fix (root cause): Avalonia's virtualizer keeps the SELECTED item's container
        // alive across an ItemsSource swap — the orphan stayed painted over the next view
        // (ghost-diag.log: the ghost was always the clicked card). Clear selection BEFORE the
        // Games collection is replaced so no container is pinned through the swap.
        _vm.ViewSwapping += () =>
        {
            _selectionAnchor = null;
            var grid = this.FindControl<ListBox>("GameGridView");
            grid?.SelectedItems?.Clear();
            this.FindControl<DataGrid>("GameListView")?.SelectedItems?.Clear();
            // THE ghost-card root cause (from reading the virtualizer source): the recycle sweep
            // exempts the container recorded in KeyboardNavigation.TabOnceActiveElement — the
            // "where does Tab return to" memory, set by clicking a card and replaced only by the
            // NEXT click. Selection and keyboard focus were red herrings (the panel consults
            // neither), which is why every earlier fix bounced. Clear the memory before the swap;
            // VirtualizingWrapPanel.OnItemsChanged also clears it defensively for any other path.
            if (grid != null) Avalonia.Input.KeyboardNavigation.SetTabOnceActiveElement(grid, null);
        };
        _artworkFetch.BoxArt3DFetched += () => Dispatcher.UIThread.Post(() =>
        {
            var panel = this.FindControl<Border>("BoxArtTogglePanel");
            if (panel != null) panel.IsVisible = true;
        });

        // Heal an interrupted import FIRST, then retry artwork. A close mid-import strands games with
        // no hash (the hash+art background tasks never ran), and unhashed games are invisible to the
        // artwork retry — so they'd never get art. ResumeIncompleteImportsAsync re-hashes them in a fast
        // local pass; only then can RetryMissingArtworkAsync (which matches on hash) fetch their art.
        // Both run off the UI thread; the resume is awaited so hashes exist before the retry queries.
        // DEFERRED: this work can be heavy (re-hashing hundreds of stranded ROM files + network
        // artwork fetches). Even off the UI thread it saturates disk/CPU, so kicking it off during
        // launch makes the freshly-shown window feel sluggish. Wait until the UI has settled before
        // starting it — the golden rule is the app must never feel slow, and nothing here is urgent.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2500);
            Services.StartupTrace.Mark("deferred_startup_work_begin");
            try { await _importer.ResumeIncompleteImportsAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[MainWindow] resume failed: {ex.Message}"); }
            await _artworkFetch.RetryMissingArtworkAsync();

            // Orphaned save states (upstream MainWindow:274): files created outside the app —
            // or stranded by a crash — get DB rows so the Save States tab sees them.
            try
            {
                int found = _db!.DiscoverOrphanedSaveStates();
                if (found > 0)
                    Dispatcher.UIThread.Post(() =>
                        _vm?.SetStatus($"Discovered {found} save state(s)", autoClear: true));
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[MainWindow] orphan save-state discovery failed: {ex.Message}"); }

            // Core updates (upstream's CheckCoreUpdatesAndNotifyAsync): banner for 20s.
            try
            {
                var updates = await new Services.CoreDownloadService()
                    .CheckAllForUpdatesAsync(AppPaths.GetCoresFolder());
                if (updates.Count > 0)
                {
                    _coreUpdatesNotified = true;   // banner click → Preferences → Cores
                    Dispatcher.UIThread.Post(() => _vm?.SetStatus(updates.Count == 1
                        ? "1 core update available — Preferences → Cores"
                        : $"{updates.Count} core updates available — Preferences → Cores", 20_000));
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[CoreUpdateCheck] {ex.Message}"); }

            // App update check (upstream MainWindow:383 — runs after the core check so the two
            // GitHub probes don't compete). Persistent banner; click → confirm → download+restart.
            try
            {
                var update = await Services.UpdateService.CheckAsync(System.Threading.CancellationToken.None);
                if (update != null)
                {
                    _pendingAppUpdate = update;
                    Dispatcher.UIThread.Post(() =>
                        _vm?.SetStatus($"Emutastic {update.Tag} available — click to install", 0));
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[AppUpdateCheck] {ex.Message}"); }

            Services.StartupTrace.Mark("deferred_startup_work_done");
        });

        // Controller hotplug status (upstream MainWindow:626 + the named poll at :1529): a
        // long-lived manager just for connection events — the Controls panel makes its own
        // short-lived one for capture (SDL init is refcounted). MUST be built on the UI thread:
        // it polls itself on a DispatcherTimer. Events arrive on the UI thread; diffing the name
        // list gives upstream's per-device "Controller connected: <name>" messages.
        try
        {
            _hotplugMgr = new Services.ControllerManager();
            var prevPads = _hotplugMgr.GetDeviceNames();
            _hotplugMgr.ConnectionChanged += _ =>
            {
                var now = _hotplugMgr!.GetDeviceNames();
                var added = now.Except(prevPads).ToList();
                var removed = prevPads.Except(now).ToList();
                prevPads = now;
                foreach (var n in added) _vm?.SetStatus($"Controller connected: {n}", 5000);
                foreach (var n in removed) _vm?.SetStatus($"Controller disconnected: {n}", 5000);
                if (added.Count == 0 && removed.Count == 0)
                    _vm?.SetStatus(now.Count > 0 ? "Controller connected" : "Controller disconnected", 5000);
            };
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[MainWindow] controller hotplug watch failed: {ex.Message}"); }

        // Warm LibVLC off the UI thread so the first detail-card snap video doesn't
        // pay the multi-second native init on the dispatcher.
        VideoPlaybackService.Instance.StartWarmup();

        // Apply the saved/default theme palette into Application.Resources (enables Light/OLED/Midnight;
        // for Dark this matches the static DarkTheme.axaml values).
        var swTheme = Services.StartupTrace.Start();
        try
        {
            string? themeId = App.Configuration?.GetThemeConfiguration()?.ActiveThemeId;
            ThemeService.Instance.LoadAndApplyTheme(string.IsNullOrEmpty(themeId) ? "builtin.dark" : themeId);
        }
        catch { /* theme apply is best-effort; static dark palette is the fallback */ }
        Services.StartupTrace.Stop("MainWindow.ApplyTheme", swTheme);

        // Library layout (Preferences → Theme → Layout): push saved padding/card-size/spacing into
        // the grid's DynamicResources before first layout.
        App.ApplyLibraryLayout();
        App.ApplyWindowButtonStyle(App.Configuration?.GetThemeConfiguration()?.WindowButtonStyle);

        // Grid background image (Preferences → Theme): apply now + on change.
        ThemeService.Instance.BackgroundImageChanged += (_, _) => Dispatcher.UIThread.Post(ApplyBackgroundImage);
        ApplyBackgroundImage();

        // Sidebar OPTIONS buttons.
        var importBtn = this.FindControl<Button>("ImportButton");
        if (importBtn != null) importBtn.Click += (_, _) => RunGuarded(PickAndImportAsync);
        var prefsBtn = this.FindControl<Button>("PreferencesButton");
        if (prefsBtn != null) prefsBtn.Click += (_, _) => PreferencesWindow.OpenOrFocus(this);
        var newCollBtn = this.FindControl<Button>("NewCollectionButton");
        if (newCollBtn != null) newCollBtn.Click += (_, _) => NewCollection();
        // Status banner click (upstream BannerBorder_MouseLeftButtonUp): pending app update →
        // confirm + install; core-update notice → Preferences → Cores.
        var banner = this.FindControl<Border>("StatusBanner");
        if (banner != null) banner.PointerPressed += OnBannerPressed;
        RefreshCollectionsSidebar();

        Task.Run(() =>
        {
            var swReload = Services.StartupTrace.Start();
            _vm.Reload();
            Services.StartupTrace.Stop("MainWindow.LibraryReload", swReload);
            Dispatcher.UIThread.Post(() =>
            {
                var swNav = Services.StartupTrace.Start();
                _vm.NavigateToAllGamesCommand.Execute(null);
                Services.StartupTrace.Stop("MainWindow.NavigateToAllGames", swNav);
                Services.StartupTrace.Mark("main_window_shown");
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

    // ── Grid background image (Preferences → Theme) ─────────────────────────
    private string? _bgImagePath;                            // path of the currently-decoded bitmap
    private Avalonia.Media.Imaging.Bitmap? _bgBitmap;

    private void ApplyBackgroundImage()
    {
        var img = this.FindControl<Image>("GridBackgroundImage");
        if (img == null) return;
        var cfg = App.Configuration?.GetThemeConfiguration();
        string path = cfg != null ? AppPaths.FromStoragePath(cfg.BackgroundImagePath) : "";

        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            _bgImagePath = null; _bgBitmap = null;
            img.Source = null; img.IsVisible = false;
            return;
        }

        // Opacity + stretch are cheap property sets — always apply (no decode needed).
        img.Opacity = cfg!.BackgroundImageOpacity;
        img.Stretch = cfg.BackgroundImageStretch switch
        {
            "Uniform" => Avalonia.Media.Stretch.Uniform,
            "Fill"    => Avalonia.Media.Stretch.Fill,
            "None"    => Avalonia.Media.Stretch.None,
            _          => Avalonia.Media.Stretch.UniformToFill,
        };

        // Only (re)decode when the path actually changed — and do it off the UI thread,
        // so dragging the opacity slider doesn't re-read/decode the image every tick.
        if (path == _bgImagePath && _bgBitmap != null)
        {
            img.Source = _bgBitmap; img.IsVisible = true;
            return;
        }
        _bgImagePath = path;
        string captured = path;
        Task.Run(() =>
        {
            Avalonia.Media.Imaging.Bitmap? bmp = null;
            try { bmp = new Avalonia.Media.Imaging.Bitmap(captured); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                if (bmp == null || _bgImagePath != captured) return;   // path changed again meanwhile
                _bgBitmap = bmp;
                img.Source = bmp;
                img.IsVisible = true;
            });
        });
    }

    // ── Game detail card (U4) ───────────────────────────────────────────────
    private GameDetailWindow? _openDetailWindow;
    private DateTime _detailOpenedAt;

    // Single-click a box-art card → open its detail window (upstream UX). Shift+click
    // is reserved for range-select, so it never opens the card.
    private Game? _selectionAnchor;

    // Left-click a card: plain → select one + open detail; Ctrl → toggle; Shift → range from
    // the anchor (ports upstream GameCard_Click + DoRangeSelect). Right-click falls through to
    // ContextRequested. We handle the press so Avalonia's built-in Multiple-toggle doesn't fire.
    // ── Status-banner click (upstream BannerBorder_MouseLeftButtonUp): a pending app update
    //    installs after confirmation; the core-update notice opens Preferences → Cores. ─────────
    private Services.UpdateService.AppUpdate? _pendingAppUpdate;
    private bool _coreUpdatesNotified;
    private System.Threading.CancellationTokenSource? _metadataRefreshCts;   // banner click cancels (upstream)

    private async void OnBannerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (_vm?.IsNotification != true) return;

            // A metadata refresh in progress → click STOPS the refresh (upstream order: this
            // wins over everything else). Resumable — the next Refresh skips filled games.
            if (_metadataRefreshCts is { IsCancellationRequested: false } cts)
            {
                cts.Cancel();
                _vm?.SetStatus("Metadata refresh stopped.", autoClear: true);
                return;
            }

            if (_pendingAppUpdate is { } update)
            {
                bool ok = await new ConfirmDialog("Update Available",
                    $"Update to {update.Tag}?\n\nThe app will download the update and restart.",
                    "Update").ShowDialog<bool>(this);
                if (!ok) return;
                _pendingAppUpdate = null;
                var progress = new Progress<(int pct, string msg)>(p => _vm?.SetStatus(p.msg, 0));
                string? err = await Services.UpdateService.DownloadAndApplyAsync(
                    update.Asset, update.Kind, progress, System.Threading.CancellationToken.None);
                if (err != null) _vm?.SetStatus(err, 10_000);   // success never returns — the app exits
                return;
            }

            if (_coreUpdatesNotified)
            {
                _coreUpdatesNotified = false;
                PreferencesWindow.OpenOrFocus(this, panel: "NavCores");
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Banner] click failed: {ex.Message}"); }
    }

    private void OnGamePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) return;
        var node = e.Source as Control;
        while (node != null && node.DataContext is not Game) node = node.Parent as Control;
        if (node?.DataContext is not Game g) return;   // empty space → let the ListBox clear selection
        var grid = this.FindControl<ListBox>("GameGridView")!;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { DoRangeSelect(grid, g); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (grid.SelectedItems!.Contains(g)) grid.SelectedItems.Remove(g);
            else grid.SelectedItems.Add(g);
            _selectionAnchor = g;
            e.Handled = true;
        }
        else
        {
            grid.SelectedItems!.Clear();
            grid.SelectedItems.Add(g);
            _selectionAnchor = g;
            e.Handled = true;
            OpenGameDetail(g);
        }
        // Handling the press stops the ListBox's default focus grab — take it explicitly so the
        // grid's KeyDown (Delete/Enter) works right after a click. (The window-level Delete in
        // OnKeyDown is the belt to this suspender.)
        grid.Focus();
    }

    // After a console-scoped artwork batch finishes: if the user is still LOOKING at that
    // console, re-run its navigation — exactly what "leave and come back" did by hand. Rebuilds
    // the cards with the new art and re-evaluates the 2D/3D toggle via the Navigated hook.
    private async Task RefreshCurrentViewIfShowing(string console)
    {
        if (_vm == null) return;
        if (!string.Equals(_vm.SelectedConsole, console, StringComparison.OrdinalIgnoreCase)) return;
        await _vm.NavigateToConsoleCommand.ExecuteAsync(console);
    }

    // Window-level Delete (upstream's PreviewKeyDown at MainWindow:2555): works regardless of
    // which control has focus — Library tab deletes the selected games, Screenshots tab deletes
    // the selected shots. The grid/list KeyDown handlers stay for when they hold focus.
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Delete) return;
        // Don't hijack Delete while typing in a text box.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        if (_activeTab == "Library")
        {
            var grid = this.FindControl<ListBox>("GameGridView");
            var list = this.FindControl<DataGrid>("GameListView");
            var sel = grid?.IsVisible == true ? grid.SelectedItems?.OfType<Game>().ToList()
                    : list?.IsVisible == true ? list.SelectedItems?.OfType<Game>().ToList() : null;
            if (sel is { Count: > 0 })
            {
                e.Handled = true;
                RunGuarded(() => DeleteGamesWithConfirmAsync(sel));
            }
        }
        else if (_activeTab == "Screenshots" && _selectedScreenshots.Count > 0)
        {
            e.Handled = true;
            RunGuarded(() => DeleteScreenshotsWithConfirm(_selectedScreenshots.ToList()));
        }
    }

    private void DoRangeSelect(ListBox grid, Game clicked)
    {
        var items = grid.Items.OfType<Game>().ToList();
        int ci = items.IndexOf(clicked);
        if (ci < 0) return;
        if (_selectionAnchor == null)
        {
            _selectionAnchor = clicked;
            grid.SelectedItems!.Clear();
            grid.SelectedItems.Add(clicked);
            return;
        }
        int ai = items.IndexOf(_selectionAnchor);
        if (ai < 0) ai = 0;
        int start = Math.Min(ai, ci), end = Math.Max(ai, ci);
        grid.SelectedItems!.Clear();
        for (int i = start; i <= end; i++) grid.SelectedItems.Add(items[i]);
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
        _detailOpenedAt = DateTime.Now;
        win.Show(this);
    }

    // Clicking back on the main window dismisses an open game-detail card (light dismiss). The
    // small grace window avoids a spurious close from the activation flicker when the card opens.
    // Child dialogs opened FROM the card (Notes/Cheats/Rename) reactivate the card, not the main
    // window, so they don't trigger this.
    private void OnMainActivated(object? sender, EventArgs e)
    {
        if (_openDetailWindow != null && (DateTime.Now - _detailOpenedAt).TotalMilliseconds > 350)
            _openDetailWindow.Close();
    }

    private void LaunchGame(Game game) => LaunchGame(game, loadStatePath: null);

    private void LaunchGame(Game game, string? loadStatePath)
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
        var missingBios = Services.CoreManager.GetMissingBiosForLaunch(game.Console ?? "", romPath, corePath);
        if (missingBios.Count > 0)
        {
            // Show the BIOS-required dialog and abort, rather than letting the
            // core fail to load with a cryptic error.
            _ = new ConfirmDialog("BIOS Required",
                $"{game.Console} requires a BIOS to run.\n\nMissing: {string.Join(", ", missingBios)}\n\n" +
                "Add it in Preferences → System Files, or place it next to your ROMs.",
                "OK", infoOnly: true).ShowDialog<bool>(this);
            return;
        }
        try
        {
            // Routes to the legacy in-process EmulatorWindow, or (EMUTASTIC_PRESENT=gl) a separate
            // --game-host process. See docs/gl-present-phase1-host-process-design.md.
            // Save-state ingestion on exit is centralized in GameHostLauncher.OnGameSessionEnded.
            Services.GameHostLauncher.Launch(corePath, romPath, game.Console ?? "", game, loadStatePath);
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
        e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer?.Contains(DataFormat.File) != true) return;
        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return;
        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count > 0) _importer?.ImportFilesAsync(paths, ImportConsoleHint());
        e.Handled = true;
    }

    // ── Context menu (game card) ─────────────────────────────────────────────
    private void OnGameContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Walk up from the hit visual to the card that carries the Game — the right-click
        // can land on a nested element (image, rating panel, text run) whose own DataContext
        // isn't the Game, so checking only e.Source missed the menu entirely.
        var node = e.Source as Control;
        while (node != null && node.DataContext is not Game) node = node.Parent as Control;
        if (node?.DataContext is not Game g) return;

        // If a multi-selection is active and this game is part of it, show the bulk menu.
        var selected = SelectedGames();
        ContextMenu menu = selected.Count > 1 && selected.Contains(g)
            ? BuildMultiSelectContextMenu(selected)
            : BuildGameContextMenu(g);
        menu.Open(node);
        e.Handled = true;
    }

    // Games selected in whichever view is visible (grid or list).
    private List<Game> SelectedGames()
    {
        var list = this.FindControl<DataGrid>("GameListView");
        if (list is { IsVisible: true }) return list.SelectedItems.OfType<Game>().ToList();
        return this.FindControl<ListBox>("GameGridView")?.SelectedItems?.OfType<Game>().ToList() ?? new List<Game>();
    }

    private ContextMenu BuildMultiSelectContextMenu(List<Game> games)
    {
        var menu = new ContextMenu();
        var del = MenuAction($"🗑  Delete Selected ({games.Count})",
            () => RunGuarded(() => DeleteGamesWithConfirmAsync(games)));
        del.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF5F57"));
        menu.Items.Add(del);
        return menu;
    }

    // Confirm + delete a set of games (shared by the context menu and the Delete key).
    private async Task DeleteGamesWithConfirmAsync(List<Game> games)
    {
        if (games.Count == 0) return;
        bool ok = await new ConfirmDialog("Delete Games",
            $"Delete {games.Count} game{(games.Count == 1 ? "" : "s")}? Save states will not be removed.",
            "Delete", danger: true).ShowDialog<bool>(this);
        if (!ok) return;
        await Task.Run(() => _db!.DeleteGames(games.Select(g => g.Id)));
        foreach (var g in games) _vm!.RemoveGame(g);
        var listV = this.FindControl<DataGrid>("GameListView");
        if (listV is { IsVisible: true }) listV.SelectedItems.Clear();
        else this.FindControl<ListBox>("GameGridView")?.SelectedItems?.Clear();
        _selectionAnchor = null;
        await _vm!.FilterGamesAsync();
    }

    private static MenuItem MenuAction(string header, Action onClick, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (enabled) mi.Click += (_, _) => onClick();
        return mi;
    }

    // Fire-and-forget DB write off the UI thread. A context-menu click must NEVER do a
    // synchronous SQLite write: under lock contention the UI thread blocks while the open menu
    // holds the X11 pointer/keyboard grab — the whole DESKTOP stops accepting input until the
    // write returns (Arch field report: rating a game wedged the session past logging out).
    // In-memory state is updated by the caller first, so the UI is correct immediately and the
    // write is pure persistence; failures surface in the banner instead of crashing the click.
    private void DbWriteAsync(Action write, Action? thenOnUi = null) => _ = Task.Run(() =>
    {
        try
        {
            write();
            if (thenOnUi != null) Dispatcher.UIThread.Post(thenOnUi);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DbWrite] {ex.Message}");
            Dispatcher.UIThread.Post(() => _vm?.SetStatus("Saving the change failed — see Logs.", 8000));
        }
    });

    // ── Sidebar context menu (right-click a console OR a collection in the left nav) ──
    private void OnConsoleContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Walk up to a nav Button: collections carry an int Tag; consoles a string CommandParameter.
        var node = e.Source as Control;
        while (node != null && !(node is Button bb && (bb.Tag is int || bb.CommandParameter is string))) node = node.Parent as Control;
        if (node is not Button btn) return;

        // Collection branch (rename / delete).
        if (btn.Tag is int collectionId)
        {
            string collName = (btn.Content as string)?.Replace("📂  ", "") ?? "Collection";
            BuildCollectionContextMenu(collectionId, collName).Open(btn);
            e.Handled = true;
            return;
        }

        if (btn.CommandParameter is not string console || string.IsNullOrEmpty(console)) return;
        if (!CoreManager.ConsoleCoreMap.ContainsKey(console)) return;   // real consoles only (not the LIBRARY pseudo-navs)

        string display = console;
        if (btn.Content is StackPanel sp)
        {
            var tb = sp.Children.OfType<TextBlock>().LastOrDefault();
            if (tb?.Text != null) display = tb.Text;
        }
        BuildConsoleContextMenu(console, display).Open(btn);
        e.Handled = true;
    }

    private ContextMenu BuildConsoleContextMenu(string console, string display)
    {
        var menu = new ContextMenu();
        int count = _db?.GetGameCountForConsole(console) ?? 0;

        // Refresh Library — always available (rescans the console's source folders for new ROMs).
        menu.Items.Add(MenuAction("🔄  Refresh Library", () => RefreshLibraryFolder(console, display)));

        if (count == 0) return menu;   // empty console → refresh only (matches upstream)

        menu.Items.Add(new Separator());

        var remove = MenuAction($"🗑  Remove all {display} games ({count})", () => RunGuarded(async () =>
        {
            bool ok = await new ConfirmDialog("Remove All Games",
                $"Remove all {count} {display} games from your library?\n\nYour save states will not be affected.",
                "Remove All", danger: true).ShowDialog<bool>(this);
            if (!ok) return;
            _db!.DeleteAllGamesForConsole(console);
            await Task.Run(() => _vm!.Reload());
            await _vm!.FilterGamesAsync();
        }));
        remove.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF5F57"));
        menu.Items.Add(remove);

        // Each artwork download ends with RefreshCurrentViewIfShowing — the live per-card
        // refreshes cover mid-batch arrivals, and this guarantees the END state (all boxes +
        // the 2D/3D toggle) snaps into the open view without leaving and coming back.
        menu.Items.Add(MenuAction("⬇  Download Missing Artwork",
            () => RunGuarded(async () =>
            {
                await _artworkFetch!.FetchMissingArtworkForConsoleAsync(console, display);
                await RefreshCurrentViewIfShowing(console);
            })));

        var snap = App.Configuration?.GetSnapConfiguration();
        if (snap is { ScreenScraperEnabled: true } && !string.IsNullOrWhiteSpace(snap.ScreenScraperUser))
        {
            menu.Items.Add(MenuAction("⬇  Download 3D Box Art",
                () => RunGuarded(async () =>
                {
                    await _artworkFetch!.Fetch3DBoxArtForConsoleAsync(console, display);
                    await RefreshCurrentViewIfShowing(console);
                })));
            menu.Items.Add(MenuAction("⬇  Download ScreenScraper 2D Art",
                () => RunGuarded(async () =>
                {
                    await _artworkFetch!.FetchScreenScraperArtForConsoleAsync(console, display);
                    await RefreshCurrentViewIfShowing(console);
                })));
        }

        // Edit Controls — opens Preferences on the Controls panel with this console preselected.
        var editControls = MenuAction("🎮  Edit Controls…", () => PreferencesWindow.OpenOrFocus(this, console));
        menu.Items.Insert(0, editControls);
        menu.Items.Insert(1, new Separator());
        return menu;
    }

    // ── Collections sidebar ──────────────────────────────────────────────────
    public void RefreshCollectionsSidebar()
    {
        var panel = this.FindControl<StackPanel>("UserCollectionsPanel");
        if (panel == null || _db == null) return;
        panel.Children.Clear();
        var theme = this.TryFindResource("SidebarItemStyle", out var t) ? t as Avalonia.Styling.ControlTheme : null;
        foreach (var (id, name) in _db.GetAllCollections())
        {
            int cid = id;
            var btn = new Button { Content = $"📂  {name}", Tag = id };
            if (theme != null) btn.Theme = theme;
            btn.Click += (_, _) => _vm?.NavigateToCollectionCommand.Execute(cid);
            panel.Children.Add(btn);
        }
    }

    private void NewCollection() => RunGuarded(async () =>
    {
        string? name = await new RenameWindow("").ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name) || _db == null) return;
        _db.CreateCollection(name.Trim());
        RefreshCollectionsSidebar();
    });

    private ContextMenu BuildCollectionContextMenu(int collectionId, string display)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuAction("✏  Rename Collection", () => RunGuarded(async () =>
        {
            string? newName = await new RenameWindow(display).ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(newName) || _db == null) return;
            _db.RenameCollection(collectionId, newName.Trim());
            RefreshCollectionsSidebar();
        })));
        var del = MenuAction("🗑  Delete Collection", () => RunGuarded(async () =>
        {
            bool ok = await new ConfirmDialog("Delete Collection",
                $"Delete the collection \"{display}\"?\n\nGames will not be removed from your library.",
                "Delete", danger: true).ShowDialog<bool>(this);
            if (!ok || _db == null) return;
            _db.DeleteCollection(collectionId);
            RefreshCollectionsSidebar();
        }));
        del.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF5F57"));
        menu.Items.Add(del);
        return menu;
    }

    // Rescan the source folders of this console's imported games for new ROMs of its
    // extensions and import them (port of upstream RefreshLibraryFolder), then backfill
    // missing artwork for the console.
    private bool _refreshInProgress;

    private async void RefreshLibraryFolder(string console, string display)
    {
        if (_db == null || _importer == null) return;
        // Guard stays set until the import drains (released in onDrained / early returns) so a
        // second Refresh can't subscribe a duplicate drain handler onto one coalesced drain.
        if (_refreshInProgress) { _vm?.SetStatus("A refresh is already running…", autoClear: true); return; }
        _refreshInProgress = true;

        // Scan off the UI thread (GetAllGames + recursive EnumerateFiles can be heavy).
        var exts = new HashSet<string>(RomService.GetExtensionsForConsole(console), StringComparer.OrdinalIgnoreCase);
        if (exts.Count == 0) { _vm?.SetStatus($"No file extensions registered for {display}.", autoClear: true); _refreshInProgress = false; return; }

        var (candidates, before, noDirs) = await Task.Run(() =>
        {
            var scanDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = _db!.GetAllGames();
            foreach (var game in all)
            {
                if (!string.Equals(game.Console, console, StringComparison.OrdinalIgnoreCase)) continue;
                string source = AppPaths.FromStoragePath(!string.IsNullOrEmpty(game.OriginalSourcePath) ? game.OriginalSourcePath : game.RomPath);
                if (string.IsNullOrEmpty(source)) continue;
                try { string? d = System.IO.Path.GetDirectoryName(source); if (!string.IsNullOrEmpty(d) && System.IO.Directory.Exists(d)) scanDirs.Add(d); }
                catch { }
            }
            var found = new List<string>();
            foreach (var dir in scanDirs)
            {
                try { foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
                        if (exts.Contains(System.IO.Path.GetExtension(path))) found.Add(path); }
                catch { }
            }
            int cnt = all.Count(g => string.Equals(g.Console, console, StringComparison.OrdinalIgnoreCase));
            return (found, cnt, scanDirs.Count == 0);
        });

        // Bootstrap: an empty console has no library entries to derive scan folders from
        // (upstream has the same dead-end — on Windows the library predates it, so it never
        // bites there). Ask for the folder instead of silently doing nothing; the import
        // pipeline classifies .zip/.7z archives by their inner contents, so an archive-only
        // folder (e.g. PSP) imports fine once it's actually handed to the importer.
        if (noDirs || candidates.Count == 0)
        {
            string why = noDirs
                ? $"No {display} games in the library yet"
                : $"No new {display} files in your existing {display} folders";
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"{why} — select your {display} ROM folder",
                AllowMultiple = false,
            });
            string? folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(folder))
            {
                _vm?.SetStatus($"Refresh cancelled — no {display} folder selected.", autoClear: true);
                _refreshInProgress = false;
                return;
            }
            candidates = new List<string> { folder };   // ImportFilesAsync expands directories
        }
        Action? onDrained = null;
        onDrained = () =>
        {
            _importer!.ImportQueueDrained -= onDrained;
            Dispatcher.UIThread.Post(async () =>
            {
                await Task.Run(() => _vm!.Reload());
                await _vm!.FilterGamesAsync();
                int after = _db!.GetAllGames().Count(g => string.Equals(g.Console, console, StringComparison.OrdinalIgnoreCase));
                int added = Math.Max(0, after - before);
                _vm!.SetStatus(added switch
                {
                    0 => $"Refresh — no new {display} ROMs.",
                    1 => $"Refresh — added 1 new {display} game.",
                    _ => $"Refresh — added {added} new {display} games.",
                }, autoClear: true);
                // Backfill missing artwork for the console (the user expects a full refresh).
                try { await _artworkFetch!.FetchMissingArtworkForConsoleAsync(console, display); } catch { }
                // Metadata pass (upstream MainWindow:1697): the manual Refresh click is the
                // user's deliberate opt-in to retry games previously marked "we tried, came
                // back empty" — reset MetadataAttempts for the console, then backfill
                // missing Developer/Publisher/Genre/Description/Year fields.
                try
                {
                    _db!.ResetMetadataAttemptsForConsole(console);
                    // Cancellable via banner click (upstream _metadataRefreshCts): state is
                    // naturally resumable — the next Refresh skips already-filled games.
                    _metadataRefreshCts?.Dispose();
                    _metadataRefreshCts = new System.Threading.CancellationTokenSource();
                    await _artworkFetch!.RefreshConsoleMetadataAsync(console,
                        s => _vm?.SetStatus(s, autoClear: true), _metadataRefreshCts.Token);
                }
                catch { }
                _metadataRefreshCts?.Dispose();
                _metadataRefreshCts = null;
                _refreshInProgress = false;
            });
        };
        _importer.ImportQueueDrained += onDrained;
        _vm?.SetStatus($"Refreshing {display}…", autoClear: false);
        _importer.ImportFilesAsync(candidates, console);
    }

    private ContextMenu BuildGameContextMenu(Game game)
    {
        var menu = new ContextMenu();
        var items = menu.Items;

        items.Add(MenuAction("▶  Play Game", () => LaunchGame(game)));

        // ── Play Save State submenu (first 10, newest first — matches upstream) ──
        var saveStateItem = new MenuItem { Header = "⏱  Play Save State" };
        var saveStates = _db?.GetSaveStatesByGame(game.Id) ?? new List<SaveState>();
        if (saveStates.Count == 0)
            saveStateItem.Items.Add(new MenuItem { Header = "No save states", IsEnabled = false });
        else
            foreach (var s in saveStates.Take(10))
            {
                var state = s;
                var si = new MenuItem { Header = state.Name };
                si.Click += (_, _) => RunGuarded(() => { LaunchWithSaveState(state); return System.Threading.Tasks.Task.CompletedTask; });
                saveStateItem.Items.Add(si);
            }
        items.Add(saveStateItem);

        bool fav = game.IsFavorite;
        items.Add(MenuAction(fav ? "♥  Remove from Favorites" : "♡  Add to Favorites", () =>
        {
            game.IsFavorite = !game.IsFavorite;
            _vm!.RefreshGame(game);
            // Favorites view re-reads the DB, so reload only after the write lands.
            DbWriteAsync(() => _db!.ToggleFavorite(game.Id, game.IsFavorite), thenOnUi: () => RunGuarded(async () =>
            {
                if (_vm?.IsShowingFavorites == true) { await _vm.LoadFavoritesAsync(_db!); PopulateFavoritesView(); }
            }));
        }));

        items.Add(new Separator());

        // Rating submenu
        var rating = new MenuItem { Header = "⭐  Rating" };
        foreach (var (label, value) in new[] { ("None", 0), ("★☆☆☆☆", 1), ("★★☆☆☆", 2), ("★★★☆☆", 3), ("★★★★☆", 4), ("★★★★★", 5) })
        {
            int v = value;
            rating.Items.Add(MenuAction((game.Rating == v ? "✓ " : "    ") + label, () =>
            {
                game.Rating = v; _vm!.RefreshGame(game);
                DbWriteAsync(() => _db!.UpdateRating(game.Id, v));
            }));
        }
        items.Add(rating);

        items.Add(new Separator());

        // Deferred to their splinters (disabled stubs).
        items.Add(MenuAction("📝  Notes", () => NotesWindow.ShowFor(game, this)));
        items.Add(MenuAction(ManualLauncher.HasUsableManual(game) ? "📖  View Manual" : "⬇  Download Manual",
            () => RunGuarded(() => ManualLauncher.OpenOrDownloadAsync(game, _artworkFetch!))));
        // Cheats — only when the console's core supports them (upstream gate). Modal so two
        // windows can't last-write-wipe the same cheat JSON.
        if (CoreManager.ConsoleCoreMap.TryGetValue(game.Console ?? "", out var cheatCores) && cheatCores.Length > 0
            && CheatSupport.Lookup(cheatCores[0]).Level != CheatSupportLevel.NotSupported)
            items.Add(MenuAction("🎮  Cheats…", () => RunGuarded(() => new CheatsManagerWindow(game).ShowDialog(this))));
        // Apply ROM Hack (cartridge systems only; base ROMs, not already-hacked entries).
        if (!game.HasPatch && RomPatcher.SupportedConsoles.Contains(game.Console ?? ""))
            items.Add(MenuAction("🧩  Apply ROM Hack…", () => RunGuarded(() => ApplyRomHackAsync(game))));

        items.Add(MenuAction("📁  Show in Files", () =>
        {
            string rom = AppPaths.FromStoragePath(game.RomPath);
            string? dir = System.IO.Path.GetDirectoryName(rom);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                Services.ShellOpen.Open(dir);
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
            string dest = System.IO.Path.Combine(AppPaths.GetFolder("Artwork", game.Console ?? ""),
                $"{game.RomHash}_custom{System.IO.Path.GetExtension(src)}");
            await Task.Run(() =>
            {
                System.IO.File.Copy(src, dest, overwrite: true);   // file IO off the UI thread too
                _db!.UpdateCoverArt(game.Id, dest);
            });
            // Evict the dest path (its bytes just changed) + the old path, then force a change
            // notification even when dest == the current CoverArtPath (the Game setter no-ops on equal).
            Converters.PathToImageConverter.Evict(dest);
            if (!string.IsNullOrEmpty(game.CoverArtPath) && game.CoverArtPath != dest)
                Converters.PathToImageConverter.Evict(game.CoverArtPath);
            game.CoverArtPath = "";
            game.CoverArtPath = dest;
            _vm!.RefreshGame(game);
        })));

        // Add to Collection — toggle membership per collection (✓ when the game is in it),
        // plus "New Collection…".
        var addToColl = new MenuItem { Header = "📂  Add to Collection" };
        var memberOf = _db!.GetCollectionsForGame(game.Id).Select(c => c.Id).ToHashSet();
        foreach (var (cid, cname) in _db.GetAllCollections())
        {
            int id = cid;
            bool inIt = memberOf.Contains(cid);
            addToColl.Items.Add(MenuAction((inIt ? "✓ " : "    ") + cname, () =>
            {
                DbWriteAsync(() =>
                {
                    if (inIt) _db!.RemoveGameFromCollection(game.Id, id);
                    else _db!.AddGameToCollection(game.Id, id);
                });
                _vm?.SetStatus(inIt ? $"Removed from {cname}." : $"Added to {cname}.", autoClear: true);
            }));
        }
        if (addToColl.Items.Count > 0) addToColl.Items.Add(new Separator());
        addToColl.Items.Add(MenuAction("＋  New Collection…", () => RunGuarded(async () =>
        {
            string? name = await new RenameWindow("").ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(name)) return;
            await Task.Run(() =>
            {
                int id = _db!.CreateCollection(name.Trim());
                _db.AddGameToCollection(game.Id, id);
            });
            RefreshCollectionsSidebar();
        })));
        items.Add(addToColl);

        items.Add(new Separator());

        items.Add(MenuAction("✏  Rename Game", () => RunGuarded(async () =>
        {
            string? newTitle = await new RenameWindow(game.Title).ShowDialog<string?>(this);
            if (string.IsNullOrEmpty(newTitle)) return;
            game.Title = newTitle;
            await Task.Run(() => _db!.UpdateTitle(game.Id, newTitle));
            _vm!.RefreshGame(game);
        })));

        items.Add(MenuAction("🗑  Remove from Library", () => RunGuarded(async () =>
        {
            bool ok = await new ConfirmDialog("Remove Game",
                $"Remove \"{game.Title}\" from your library? (The ROM file is not deleted.)",
                "Remove", danger: true).ShowDialog<bool>(this);
            if (!ok) return;
            await Task.Run(() => _db!.DeleteGame(game.Id));
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

    /// <summary>
    /// "Apply ROM Hack…" (port of upstream): pick an IPS/BPS/UPS patch, validate it against the
    /// base ROM off the UI thread (BPS/UPS verify the source checksum), name the hack, copy the
    /// patch into RomPatches/{Console}/, and add the result as its own library entry. The base
    /// ROM file is never modified — the patch is applied in memory at launch (LoadGame).
    /// </summary>
    private async Task ApplyRomHackAsync(Game baseGame)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a ROM hack patch",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ROM hack patches") { Patterns = new[] { "*.ips", "*.bps", "*.ups" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        string? patchPicked = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(patchPicked)) return;

        if (!RomPatcher.IsPatchExtension(patchPicked))
        {
            await new ConfirmDialog("ROM Hack", "That file isn't an IPS, BPS, or UPS patch.",
                "OK", infoOnly: true).ShowDialog<bool>(this);
            return;
        }

        _vm!.SetStatus($"Validating ROM hack for {baseGame.Title}…");

        string baseRomPath = AppPaths.FromStoragePath(baseGame.RomPath);
        string console     = baseGame.Console ?? "";

        // Resolve the raw base bytes (extracting if archived), apply + validate the patch,
        // and hash the patched output — all off the UI thread.
        var (pr, patchedHash) = await Task.Run<(PatchResult pr, string? hash)>(() =>
        {
            try
            {
                string raw = baseRomPath;
                string ext = System.IO.Path.GetExtension(raw);
                if (ZipRomExtractor.IsArchiveExtension(ext) && ZipRomExtractor.ConsoleNeedsExtraction(console))
                {
                    string? extracted = ZipRomExtractor.ExtractSync(raw, console);
                    if (!string.IsNullOrEmpty(extracted) && System.IO.File.Exists(extracted)) raw = extracted;
                }
                if (!System.IO.File.Exists(raw))
                    return (PatchResult.Fail("The base ROM file couldn't be found."), null);

                var result = RomPatcher.Apply(System.IO.File.ReadAllBytes(raw), System.IO.File.ReadAllBytes(patchPicked));
                string? hash = result.Ok && result.Patched != null
                    ? Convert.ToHexString(System.Security.Cryptography.MD5.HashData(result.Patched))
                    : null;
                return (result, hash);
            }
            catch (Exception ex) { return (PatchResult.Fail(ex.Message), null); }
        });

        if (!pr.Ok || patchedHash == null)
        {
            _vm.SetStatus("ROM hack not applied", autoClear: true);
            await new ConfirmDialog("ROM Hack", $"Couldn't apply this patch:\n\n{pr.Error}",
                "OK", infoOnly: true).ShowDialog<bool>(this);
            return;
        }

        // Name the hack (default to the patch's file name).
        string defaultTitle = System.IO.Path.GetFileNameWithoutExtension(patchPicked);
        string? newTitle = await new RenameWindow(defaultTitle).ShowDialog<string?>(this);
        if (newTitle == null) { _vm.SetStatus("ROM hack not applied", autoClear: true); return; }
        string hackTitle = string.IsNullOrWhiteSpace(newTitle) ? defaultTitle : newTitle;

        // Copy the patch into managed storage (hash-suffixed so two hacks can't collide).
        string patchDir = AppPaths.GetFolder("RomPatches", console);
        string safeStem = string.Join("_", defaultTitle.Split(System.IO.Path.GetInvalidFileNameChars()));
        string storedPatch = System.IO.Path.Combine(patchDir,
            $"{safeStem} [{patchedHash[..8]}]{System.IO.Path.GetExtension(patchPicked)}");
        try { System.IO.File.Copy(patchPicked, storedPatch, overwrite: true); }
        catch (Exception ex) { _vm.SetStatus($"Couldn't save the patch: {ex.Message}", autoClear: true); return; }

        // Create the hacked entry — distinct RomHash so it gets its own saves/art, never
        // the base game's. RomPath stays the base ROM; the patch is applied in memory at launch.
        var hacked = new Game
        {
            Title           = hackTitle,
            Console         = console,
            Manufacturer    = baseGame.Manufacturer,
            Year            = baseGame.Year,
            RomPath         = baseGame.RomPath,
            RomHash         = patchedHash,
            Developer       = baseGame.Developer,
            Publisher       = baseGame.Publisher,
            Genre           = baseGame.Genre,
            Description     = baseGame.Description,
            BackgroundColor = baseGame.BackgroundColor,
            AccentColor     = baseGame.AccentColor,
        };
        _db!.InsertGame(hacked);                 // assigns hacked.Id
        _db.UpdatePatchPath(hacked.Id, storedPatch);

        // Reload from the DB + re-filter the current view — the same path import uses to
        // surface new games. (RefreshGame alone leaves the filter cache marked clean, so a
        // freshly created entry wouldn't appear until a manual console switch/refresh.)
        await Task.Run(() => _vm.Reload());
        await _vm.FilterGamesAsync();
        _vm.SetStatus($"Added ROM hack: {hackTitle}", autoClear: true);
    }

    private void WireImportEvents()
    {
        if (_importer == null || _vm == null) return;

        _importer.StatusChanged += msg => Dispatcher.UIThread.Post(() =>
        {
            // Route through SetStatus (notification path) so it stays visible during the late
            // artwork-download phase — those tasks fire StatusChanged AFTER the queue drains sets
            // IsImporting false, so ImportStatusText alone would be masked/hidden (matches upstream).
            _vm.SetStatus(msg);
            _vm.IsImporting = _importer!.IsImporting;
            _vm.ImportStatusText = msg;
        });

        _importer.ProgressChanged += (current, total) => Dispatcher.UIThread.Post(() =>
        {
            if (total == 0) return;
            _vm.IsImporting = _importer!.IsImporting;
            if (current >= total)
            {
                _vm.SetStatus("Import complete", autoClear: true);
                _vm.ImportProgressPercent = 100;
                return;
            }
            int pct = (int)(current / (double)total * 100);
            string headline = $"Importing… {pct}%  ({current} of {total})";
            _vm.SetStatus(headline);
            _vm.ImportStatusText = headline;
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
        if (s.ScreenshotPath.Length > 0)
        {
            // Async decode (cache hit applies instantly): a sync DecodeThumb here ran once PER CARD
            // on the UI thread — multi-second freeze on large save-state libraries.
            var img = new Image { Stretch = Stretch.UniformToFill };
            Controls.AsyncImage.SetSourcePath(img, s.ScreenshotPath);
            thumb.Child = img;
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
            // The rcheevos progress side-car travels with its .state — orphaning it on rename
            // would silently drop achievement hit counts when the renamed state is loaded.
            string newCheevos = System.IO.Path.Combine(dir, safeName + ".cheevos");
            string oldCheevos = System.IO.Path.ChangeExtension(s.StatePath, ".cheevos");
            try
            {
                // File moves + DB write off the UI thread (large .state files can live on slow disks).
                await Task.Run(() =>
                {
                    if (System.IO.File.Exists(s.StatePath))      System.IO.File.Move(s.StatePath, newState, overwrite: true);
                    if (System.IO.File.Exists(s.ScreenshotPath)) System.IO.File.Move(s.ScreenshotPath, newPng, overwrite: true);
                    if (System.IO.File.Exists(oldJson))          System.IO.File.Move(oldJson, newJson, overwrite: true);
                    if (System.IO.File.Exists(oldCheevos))       System.IO.File.Move(oldCheevos, newCheevos, overwrite: true);
                    _db!.UpdateSaveStateName(s.Id, newName, newState, newPng);
                });
            }
            catch (Exception ex)
            {
                await new ConfirmDialog("Error", $"Rename failed: {ex.Message}", "OK", infoOnly: true).ShowDialog<bool>(this);
                return;
            }
            PopulateSaveStatesView();
        })));
        menu.Items.Add(new Separator());
        var del = MenuAction("🗑  Delete", () => RunGuarded(async () =>
        {
            bool ok = await new ConfirmDialog("Delete Save State",
                $"Delete \"{s.Name}\"? This cannot be undone.", "Delete", danger: true).ShowDialog<bool>(this);
            if (!ok) return;
            // File deletes + DB write off the UI thread (same blocking class as the rating fix).
            await Task.Run(() =>
            {
                try { if (System.IO.File.Exists(s.StatePath))      System.IO.File.Delete(s.StatePath);      } catch { }
                try { if (System.IO.File.Exists(s.ScreenshotPath)) System.IO.File.Delete(s.ScreenshotPath); } catch { }
                try { string p = System.IO.Path.ChangeExtension(s.StatePath, ".png");  if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
                try { string j = System.IO.Path.ChangeExtension(s.StatePath, ".json"); if (System.IO.File.Exists(j)) System.IO.File.Delete(j); } catch { }
                try { string c = System.IO.Path.ChangeExtension(s.StatePath, ".cheevos"); if (System.IO.File.Exists(c)) System.IO.File.Delete(c); } catch { }
                _db!.DeleteSaveState(s.Id);
            });
            PopulateSaveStatesView();
        }));
        del.Foreground = new SolidColorBrush(Color.Parse("#FF5F57"));
        menu.Items.Add(del);
        return menu;
    }

    /// <summary>Boot a game directly into a save state (Save States tab / context-menu entry).
    /// Passes --load-state to the game host, which queues it exactly like upstream's
    /// pendingLoadStatePath (60-frame warmup + retry on the emu thread).</summary>
    private void LaunchWithSaveState(SaveState s)
    {
        var game = _db?.GetGameById(s.GameId);
        if (game == null)
        {
            _vm?.SetStatus("Game not found in library.", autoClear: true);
            return;
        }
        LaunchGame(game, AppPaths.FromStoragePath(s.StatePath));
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
                Services.ShellOpen.Open(ss.FilePath);
            }
        };

        // Right-click → show-in-folder + delete selection (or this one).
        card.ContextRequested += (_, e) =>
        {
            var paths = _selectedScreenshots.Count > 0 ? _selectedScreenshots.ToList() : new List<string> { ss.FilePath };
            string label = paths.Count == 1 ? "🗑  Delete Screenshot" : $"🗑  Delete {paths.Count} Screenshots";
            var menu = new ContextMenu();
            // Mirrors the library's "Show in Files" (upstream's "Show in Explorer", which selects
            // the file). Always acts on the card under the cursor (ss), not the multi-selection —
            // opening N file-manager windows for a shift-selection would be hostile.
            menu.Items.Add(MenuAction("📁  Show in Files", () =>
            {
                if (System.IO.File.Exists(ss.FilePath)) Services.ShellOpen.ShowInFolder(ss.FilePath);
                else _vm?.SetStatus("Screenshot file not found.", autoClear: true);
            }));
            menu.Items.Add(new Separator());
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
