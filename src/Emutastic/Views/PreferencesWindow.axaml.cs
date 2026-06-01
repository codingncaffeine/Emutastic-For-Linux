using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Emutastic.Views;

/// <summary>
/// U5 — the Preferences hub shell. Custom-chromed window with an 11-tab nav bar that
/// switches between panels (Controls, System Files, Cores/Extras, Core Options, Library,
/// Theme, Snaps, Achievements, Media, Backups, About). Each panel's real content is filled
/// in its own audited sub-splinter (U5a–U5j); until then a panel shows a placeholder.
/// </summary>
public partial class PreferencesWindow : Window
{
    // (nav RadioButton name, content panel name) — drives the show/hide switch.
    private static readonly (string Nav, string Panel)[] Sections =
    {
        ("NavControls",     "PanelControls"),
        ("NavSystemFiles",  "PanelSystemFiles"),
        ("NavCores",        "PanelCores"),
        ("NavCoreOptions",  "PanelCoreOptions"),
        ("NavLibrary",      "PanelLibrary"),
        ("NavTheme",        "PanelTheme"),
        ("NavSnaps",        "PanelSnaps"),
        ("NavAchievements", "PanelAchievements"),
        ("NavMedia",        "PanelMedia"),
        ("NavBackups",      "PanelBackups"),
        ("NavAbout",        "PanelAbout"),
    };

    public PreferencesWindow()
    {
        InitializeComponent();

        Platform.WindowResize.Enable(this);   // edge/corner resize for the borderless window

        // Custom chrome.
        this.FindControl<Grid>("CustomTitleBar")!.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("MaximizeButton")!.Click += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        // Nav → panel switching + per-panel placeholders.
        foreach (var (nav, panel) in Sections)
        {
            var rb = this.FindControl<RadioButton>(nav)!;
            var name = (string)nav[3..]; // "Controls" etc.
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) ShowPanel(panel); };
            FillPlaceholder(panel, name);
        }

        WireAbout();
        WireTheme();
        WireLibrary();
        WireSnaps();
        this.FindControl<Button>("CoreOptionsResetBtn")!.Click += (_, _) => CoreOptionsReset();
        this.FindControl<Button>("CoreOptionsSaveBtn")!.Click += (_, _) => CoreOptionsSave();
        WireMedia();
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowCts.Cancel();
        base.OnClosed(e);
    }

    private readonly CancellationTokenSource _windowCts = new();

    private void ShowPanel(string target)
    {
        foreach (var (_, panel) in Sections)
        {
            var grid = this.FindControl<Grid>(panel);
            if (grid != null) grid.IsVisible = panel == target;
        }
        if (target == "PanelAbout") LoadAboutSettings();
        if (target == "PanelTheme") LoadThemeSettings();
        if (target == "PanelLibrary") LoadLibrarySettings();
        if (target == "PanelSnaps") LoadSnapsSettings();
        if (target == "PanelCoreOptions") BuildCoreOptionsTab();
        if (target == "PanelMedia") LoadMediaSettings();
    }

    // Temporary placeholder until the panel's sub-splinter fills it.
    private void FillPlaceholder(string panel, string title)
    {
        var grid = this.FindControl<Grid>(panel);
        if (grid == null || grid.Children.Count > 0) return;
        grid.Children.Add(new TextBlock
        {
            Text = $"{title} settings — in progress",
            FontFamily = this.TryFindResource("PrimaryFont", out var f) && f is FontFamily ff ? ff : FontFamily.Default,
            FontSize = 14,
            Foreground = this.TryFindResource("TextMutedBrush", out var b) ? b as IBrush : Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 — About panel (port of upstream; GitHub release check inlined, since
    //  PreferencesCache isn't ported — a self-contained HttpClient GET + 3s budget)
    // ════════════════════════════════════════════════════════════════════════

    private const string GitHubRepoUrl     = "https://github.com/codingncaffeine/Emutastic-For-Linux";
    private const string GitHubLatestApi   = "https://api.github.com/repos/codingncaffeine/Emutastic-For-Linux/releases/latest";
    private const string GitHubReleasesUrl = "https://github.com/codingncaffeine/Emutastic-For-Linux/releases";

    private static readonly System.Net.Http.HttpClient _aboutHttp = CreateAboutHttp();
    private string? _latestReleaseUrl;
    private bool _aboutLoaded;

    private static System.Net.Http.HttpClient CreateAboutHttp()
    {
        var http = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/about-tab");   // GitHub rejects no-UA requests
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private void WireAbout()
    {
        this.FindControl<Button>("AboutOpenRepoBtn")!.Click += (_, _) => OpenUrl(GitHubRepoUrl);
        this.FindControl<Button>("AboutOpenLatestReleaseBtn")!.Click += (_, _) => OpenUrl(_latestReleaseUrl ?? GitHubReleasesUrl);
        this.FindControl<Button>("AboutRecheckBtn")!.Click += (_, _) => _ = CheckLatestReleaseAsync();
        this.FindControl<Button>("AboutLicenseBtn")!.Click += (_, _) => OpenUrl(GitHubRepoUrl + "/blob/main/LICENSE");
        this.FindControl<Button>("AboutCoresBtn")!.Click += (_, _) => OpenUrl(GitHubRepoUrl + "#credits");
    }

    private void LoadAboutSettings()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        this.FindControl<TextBlock>("AboutInstalledVersionText")!.Text =
            version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v?.?.?";

        if (_aboutLoaded) return;   // one fetch per window lifetime; "Check Again" forces a re-fetch
        _aboutLoaded = true;
        _ = CheckLatestReleaseAsync();
    }

    private async Task CheckLatestReleaseAsync()
    {
        var latest = this.FindControl<TextBlock>("AboutLatestVersionText")!;
        var status = this.FindControl<TextBlock>("AboutUpdateStatusText")!;
        var openLatest = this.FindControl<Button>("AboutOpenLatestReleaseBtn")!;
        var recheck = this.FindControl<Button>("AboutRecheckBtn")!;

        latest.Text = "Checking…";
        status.Text = "";
        openLatest.IsVisible = false;
        recheck.IsEnabled = false;

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            string json = await _aboutHttp.GetStringAsync(GitHubLatestApi, budget.Token).ConfigureAwait(true);

            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            string tag = obj.Value<string>("tag_name") ?? "";
            _latestReleaseUrl = obj.Value<string>("html_url");
            if (string.IsNullOrWhiteSpace(_latestReleaseUrl)) _latestReleaseUrl = GitHubReleasesUrl;

            latest.Text = string.IsNullOrWhiteSpace(tag) ? "—" : tag;

            if (TryCompareVersions(tag, out int cmp))
            {
                if (cmp > 0)
                {
                    status.Text = "A newer release is available.";
                    status.Foreground = this.TryFindResource("AccentBrush", out var a) ? a as IBrush : Brushes.OrangeRed;
                    openLatest.IsVisible = true;
                }
                else if (cmp < 0)
                    status.Text = "Your installed version is newer than the latest release (development build).";
                else
                    status.Text = "You're running the latest release.";
            }
            else
            {
                status.Text = "Could not compare versions — open the release on GitHub for details.";
                openLatest.IsVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
            latest.Text = "—";
            status.Text = "Network request timed out. Try again later.";
        }
        catch (Exception ex)
        {
            latest.Text = "—";
            status.Text = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            recheck.IsEnabled = true;
        }
    }

    // Compare installed (assembly) version against a GitHub tag like "v1.7.6".
    // >0 remote newer · <0 local newer · 0 equal. False when either side is unparseable.
    private static bool TryCompareVersions(string remoteTag, out int comparison)
    {
        comparison = 0;
        if (!Version.TryParse(remoteTag.TrimStart('v', 'V').Trim(), out var remote)) return false;
        var local = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (local == null) return false;
        comparison = new Version(remote.Major, remote.Minor, remote.Build)
            .CompareTo(new Version(local.Major, local.Minor, local.Build));
        return true;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start("xdg-open", url); } catch { }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 — Theme panel (selector + live apply + swatches + .emutheme import).
    //  Background-image controls land in the next increment.
    // ════════════════════════════════════════════════════════════════════════

    private bool _themePopulated;
    private bool _suppressThemeChange;

    private void WireTheme()
    {
        this.FindControl<ComboBox>("ThemeCombo")!.SelectionChanged += (_, _) =>
        {
            if (_suppressThemeChange) return;
            if (this.FindControl<ComboBox>("ThemeCombo")!.SelectedItem is ComboBoxItem { Tag: string id })
                ApplyTheme(id);
        };
        this.FindControl<Button>("ImportThemeBtn")!.Click += (_, _) => _ = ImportThemeAsync();

        // Background image controls.
        this.FindControl<Button>("BgImagePickBtn")!.Click += (_, _) => _ = PickBackgroundAsync();
        this.FindControl<Button>("BgImageClearBtn")!.Click += (_, _) =>
        {
            UpdateThemeConfig(c => { c.BackgroundImagePath = ""; });
            LoadBackgroundSettings();
            Services.ThemeService.Instance.RaiseBackgroundImageChanged();
        };
        this.FindControl<Slider>("BgOpacitySlider")!.PropertyChanged += (s, e) =>
        {
            if (_suppressBgChange || e.Property.Name != nameof(Slider.Value)) return;
            double v = this.FindControl<Slider>("BgOpacitySlider")!.Value;
            this.FindControl<TextBlock>("BgOpacityValueLabel")!.Text = $"{(int)v}%";
            UpdateThemeConfig(c => c.BackgroundImageOpacity = v / 100.0);
            Services.ThemeService.Instance.RaiseBackgroundImageChanged();
        };
        this.FindControl<ComboBox>("BgStretchCombo")!.SelectionChanged += (_, _) =>
        {
            if (_suppressBgChange) return;
            if (this.FindControl<ComboBox>("BgStretchCombo")!.SelectedItem is ComboBoxItem { Content: string fit })
            {
                UpdateThemeConfig(c => c.BackgroundImageStretch = fit);
                Services.ThemeService.Instance.RaiseBackgroundImageChanged();
            }
        };
    }

    private bool _suppressBgChange;
    private bool _bgStretchPopulated;

    private void UpdateThemeConfig(System.Action<Configuration.ThemeConfiguration> mutate)
    {
        var cfg = App.Configuration?.GetThemeConfiguration();
        if (cfg == null) return;
        mutate(cfg);
        App.Configuration?.SetThemeConfiguration(cfg);
    }

    private void LoadBackgroundSettings()
    {
        var cfg = App.Configuration?.GetThemeConfiguration();
        if (cfg == null) return;
        _suppressBgChange = true;

        var stretchCombo = this.FindControl<ComboBox>("BgStretchCombo")!;
        if (!_bgStretchPopulated)
        {
            _bgStretchPopulated = true;
            foreach (var fit in new[] { "UniformToFill", "Uniform", "Fill", "None" })
                stretchCombo.Items.Add(new ComboBoxItem { Content = fit });
        }
        stretchCombo.SelectedItem = stretchCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Content == cfg.BackgroundImageStretch) ?? stretchCombo.Items[0];

        this.FindControl<Slider>("BgOpacitySlider")!.Value = cfg.BackgroundImageOpacity * 100.0;
        this.FindControl<TextBlock>("BgOpacityValueLabel")!.Text = $"{(int)(cfg.BackgroundImageOpacity * 100)}%";

        string abs = AppPaths.FromStoragePath(cfg.BackgroundImagePath);
        this.FindControl<TextBlock>("BgImagePathLabel")!.Text =
            string.IsNullOrEmpty(abs) ? "No image set." : abs;

        _suppressBgChange = false;
    }

    private async Task PickBackgroundAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Background Image",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp" } } },
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;
        UpdateThemeConfig(c => c.BackgroundImagePath = AppPaths.ToStoragePath(path));
        LoadBackgroundSettings();
        Services.ThemeService.Instance.RaiseBackgroundImageChanged();
    }

    private void LoadThemeSettings()
    {
        var combo = this.FindControl<ComboBox>("ThemeCombo")!;
        string activeId = Services.ThemeService.Instance.ActiveThemeId;

        if (!_themePopulated)
        {
            _themePopulated = true;
            _suppressThemeChange = true;
            combo.Items.Clear();
            foreach (var (id, name) in Services.ThemeService.Instance.GetAvailableThemes())
                combo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            _suppressThemeChange = false;
        }

        // Reflect the active theme in the combo without re-applying.
        _suppressThemeChange = true;
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == activeId);
        _suppressThemeChange = false;

        BuildThemeSwatches(activeId);
        LoadBackgroundSettings();
    }

    private void ApplyTheme(string id)
    {
        Services.ThemeService.Instance.LoadAndApplyTheme(id);   // pushes colors into Application.Resources (live)
        var cfg = App.Configuration?.GetThemeConfiguration();
        if (cfg != null) { cfg.ActiveThemeId = id; App.Configuration?.SetThemeConfiguration(cfg); }
        BuildThemeSwatches(id);
    }

    private void BuildThemeSwatches(string activeId)
    {
        var panel = this.FindControl<WrapPanel>("InstalledThemesPanel");
        if (panel == null) return;
        panel.Children.Clear();

        foreach (var (id, name) in Services.ThemeService.Instance.GetAvailableThemes())
        {
            var colors = Services.ThemeService.Instance.GetColorsForTheme(id);
            bool active = id == activeId;

            var card = new Border
            {
                Width = 132, Height = 76, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(8),
                Background = ParseBrush(colors.BgPrimary, "#0F0F10"),
                BorderBrush = active ? Brush("AccentBrush") : Brush("BorderNormalBrush"),
                BorderThickness = new Thickness(active ? 2 : 1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var stack = new StackPanel { Margin = new Thickness(10) };
            // Three color chips previewing the palette.
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var hex in new[] { colors.Accent, colors.BgTertiary, colors.TextPrimary })
                chips.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = ParseBrush(hex, "#888888") });
            stack.Children.Add(chips);
            stack.Children.Add(new TextBlock
            {
                Text = name, FontFamily = Font("PrimaryFont"), FontSize = 12, FontWeight = FontWeight.SemiBold,
                Foreground = ParseBrush(colors.TextPrimary, "#F0F0F0"),
            });
            card.Child = stack;
            card.PointerPressed += (_, _) =>
            {
                var combo = this.FindControl<ComboBox>("ThemeCombo")!;
                _suppressThemeChange = true;
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == id);
                _suppressThemeChange = false;
                ApplyTheme(id);
            };
            panel.Children.Add(card);
        }
    }

    private async Task ImportThemeAsync()
    {
        var status = this.FindControl<TextBlock>("ThemeStatusText")!;
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import Theme",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Emutastic Theme") { Patterns = new[] { "*.emutheme" } } },
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        string? id = Services.ThemeService.Instance.InstallTheme(path);
        status.Text = id != null
            ? $"Imported theme. Select it from the dropdown to apply."
            : "Could not import that theme file.";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 — Library panel (data dir + library folder + import behaviour)
    // ════════════════════════════════════════════════════════════════════════

    private void WireLibrary()
    {
        this.FindControl<Button>("BrowseLibraryBtn")!.Click += (_, _) => _ = BrowseLibraryAsync();
        this.FindControl<Button>("LibrarySaveBtn")!.Click += (_, _) => SaveLibrarySettings();
        var copy = this.FindControl<RadioButton>("LibraryCopyFiles")!;
        var keep = this.FindControl<RadioButton>("LibraryKeepInPlace")!;
        var organize = this.FindControl<CheckBox>("LibraryOrganizeByConsole")!;
        copy.IsCheckedChanged += (_, _) => { if (copy.IsChecked == true) organize.IsEnabled = true; };
        keep.IsCheckedChanged += (_, _) => { if (keep.IsChecked == true) organize.IsEnabled = false; };
    }

    private void LoadLibrarySettings()
    {
        this.FindControl<TextBlock>("DataDirPathText")!.Text = AppPaths.DataRoot;
        var lib = App.Configuration?.GetLibraryConfiguration() ?? new Configuration.LibraryConfiguration();
        var pathText = this.FindControl<TextBlock>("LibraryPathText")!;
        bool hasPath = !string.IsNullOrEmpty(lib.LibraryPath);
        pathText.Text = hasPath ? lib.LibraryPath : "Not set — games stay in their original location";
        pathText.Foreground = Brush(hasPath ? "TextPrimaryBrush" : "TextSecondaryBrush");
        this.FindControl<RadioButton>("LibraryCopyFiles")!.IsChecked = lib.CopyToLibrary;
        this.FindControl<RadioButton>("LibraryKeepInPlace")!.IsChecked = !lib.CopyToLibrary;
        var organize = this.FindControl<CheckBox>("LibraryOrganizeByConsole")!;
        organize.IsChecked = lib.OrganizeByConsole;
        organize.IsEnabled = lib.CopyToLibrary;
        this.FindControl<TextBlock>("LibraryStatusText")!.Text = "";
    }

    private async Task BrowseLibraryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select game library folder",
            AllowMultiple = false,
        });
        string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;
        var pathText = this.FindControl<TextBlock>("LibraryPathText")!;
        pathText.Text = path;
        pathText.Foreground = Brush("TextPrimaryBrush");
    }

    private void SaveLibrarySettings()
    {
        var lib = App.Configuration?.GetLibraryConfiguration();
        if (lib == null) return;
        string shown = this.FindControl<TextBlock>("LibraryPathText")!.Text ?? "";
        lib.LibraryPath = shown.StartsWith("Not set") ? "" : shown;
        lib.CopyToLibrary = this.FindControl<RadioButton>("LibraryCopyFiles")!.IsChecked == true;
        lib.OrganizeByConsole = this.FindControl<CheckBox>("LibraryOrganizeByConsole")!.IsChecked == true;
        App.Configuration!.SetLibraryConfiguration(lib);
        App.Configuration!.ScheduleSave();
        this.FindControl<TextBlock>("LibraryStatusText")!.Text = "Saved.";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 — Snaps panel (ScreenScraper credentials + login test + 2D-art pref)
    // ════════════════════════════════════════════════════════════════════════

    private bool _snapsLoaded;
    private bool _suppressSnapSave;

    private void WireSnaps()
    {
        this.FindControl<ToggleSwitch>("SSEnabledToggle")!.IsCheckedChanged += (_, _) => { Refresh2DPrefEnabled(); SaveSnapSettings(); };
        this.FindControl<ToggleSwitch>("SSPrefer2DToggle")!.IsCheckedChanged += (_, _) => SaveSnapSettings();
        this.FindControl<TextBox>("SSUsernameBox")!.LostFocus += (_, _) => { Refresh2DPrefEnabled(); SaveSnapSettings(); };
        this.FindControl<TextBox>("SSPasswordBox")!.LostFocus += (_, _) => SaveSnapSettings();
        this.FindControl<Button>("SSTestBtn")!.Click += (_, _) => _ = SnapsTestLoginAsync();
    }

    private void LoadSnapsSettings()
    {
        var snap = App.Configuration?.GetSnapConfiguration() ?? new Configuration.SnapConfiguration();
        _suppressSnapSave = true;
        this.FindControl<ToggleSwitch>("SSEnabledToggle")!.IsChecked = snap.ScreenScraperEnabled;
        this.FindControl<TextBox>("SSUsernameBox")!.Text = snap.ScreenScraperUser;
        this.FindControl<TextBox>("SSPasswordBox")!.Text = snap.ScreenScraperPassword;
        this.FindControl<ToggleSwitch>("SSPrefer2DToggle")!.IsChecked = snap.PreferScreenScraper2D;
        _suppressSnapSave = false;
        _snapsLoaded = true;
        Refresh2DPrefEnabled();
    }

    private void Refresh2DPrefEnabled()
    {
        var pref = this.FindControl<ToggleSwitch>("SSPrefer2DToggle")!;
        bool ssOn = this.FindControl<ToggleSwitch>("SSEnabledToggle")!.IsChecked == true
                    && !string.IsNullOrWhiteSpace(this.FindControl<TextBox>("SSUsernameBox")!.Text);
        pref.IsEnabled = ssOn;
        if (!ssOn) { _suppressSnapSave = true; pref.IsChecked = false; _suppressSnapSave = false; }
    }

    private void SaveSnapSettings()
    {
        if (_suppressSnapSave || !_snapsLoaded) return;
        var snap = App.Configuration?.GetSnapConfiguration();
        if (snap == null) return;
        snap.ScreenScraperEnabled  = this.FindControl<ToggleSwitch>("SSEnabledToggle")!.IsChecked == true;
        snap.ScreenScraperUser     = (this.FindControl<TextBox>("SSUsernameBox")!.Text ?? "").Trim();
        snap.ScreenScraperPassword = this.FindControl<TextBox>("SSPasswordBox")!.Text ?? "";
        snap.PreferScreenScraper2D = this.FindControl<ToggleSwitch>("SSPrefer2DToggle")!.IsChecked == true;
        App.Configuration!.SetSnapConfiguration(snap);
        App.Configuration!.ScheduleSave();
        Models.Game.PreferScreenScraper2D = snap.PreferScreenScraper2D;
    }

    private async Task SnapsTestLoginAsync()
    {
        var btn = this.FindControl<Button>("SSTestBtn")!;
        var label = this.FindControl<TextBlock>("SSStatusLabel")!;
        btn.IsEnabled = false;
        label.Text = "Testing…";
        label.Foreground = Brush("TextMutedBrush");
        try
        {
            var (error, maxThreads) = await new Services.ScreenScraperService().TestLoginAsync(
                (this.FindControl<TextBox>("SSUsernameBox")!.Text ?? "").Trim(),
                this.FindControl<TextBox>("SSPasswordBox")!.Text ?? "");
            if (error == null)
            {
                label.Text = $"Verified — {maxThreads} thread{(maxThreads == 1 ? "" : "s")} available";
                label.Foreground = new SolidColorBrush(Color.Parse("#28C840"));
                var snap = App.Configuration?.GetSnapConfiguration();
                if (snap != null) { snap.ScreenScraperMaxThreads = maxThreads; App.Configuration!.SetSnapConfiguration(snap); App.Configuration!.ScheduleSave(); }
                Services.ScreenScraperService.SetMaxThreads(maxThreads);
            }
            else
            {
                label.Text = error;
                label.Foreground = Brush("AccentBrush");
            }
        }
        catch (Exception ex)
        {
            label.Text = $"Login failed: {ex.Message}";
            label.Foreground = Brush("AccentBrush");
        }
        finally { btn.IsEnabled = true; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 P1 — Core Options panel (per-core option schemas captured at launch)
    // ════════════════════════════════════════════════════════════════════════

    private readonly Services.CoreOptionsService _coreOptions = new();
    private string _selectedCoreOptionsName = "";
    private Dictionary<string, string> _pendingCoreOptionValues = new();

    private void BuildCoreOptionsTab()
    {
        var coreList = this.FindControl<StackPanel>("CoreOptionsCoreList")!;
        var optList  = this.FindControl<StackPanel>("CoreOptionsOptionList")!;
        var resetBtn = this.FindControl<Button>("CoreOptionsResetBtn")!;
        var saveBtn  = this.FindControl<Button>("CoreOptionsSaveBtn")!;
        coreList.Children.Clear();
        optList.Children.Clear();
        resetBtn.IsEnabled = false;
        saveBtn.IsEnabled = false;
        _selectedCoreOptionsName = "";
        _pendingCoreOptionValues = new();

        var cores = _coreOptions.GetCoresWithSchema();
        if (cores.Count == 0)
        {
            optList.Children.Add(new TextBlock
            {
                Text = "No core options have been discovered yet.\n\nLaunch a game for any system — options are captured automatically the first time a core loads.",
                FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0),
            });
            return;
        }

        // Flat list (manufacturer grouping folds in with ConsoleCategories at the Cores phase).
        string? first = null;
        foreach (var (coreName, displayName, consoleName) in cores)
        {
            first ??= coreName;
            string captured = coreName;
            string label = consoleName.Length > 0 ? $"{displayName} ({consoleName})" : displayName;
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Foreground = Brush("TextPrimaryBrush"),
                FontFamily = Font("PrimaryFont"), FontSize = 12, Padding = new Thickness(10, 8, 10, 8),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            btn.Click += (_, _) => LoadCoreOptionsForCore(captured);
            coreList.Children.Add(btn);
        }
        if (first != null) LoadCoreOptionsForCore(first);
    }

    private void LoadCoreOptionsForCore(string coreName)
    {
        _selectedCoreOptionsName = coreName;
        var optList  = this.FindControl<StackPanel>("CoreOptionsOptionList")!;
        var resetBtn = this.FindControl<Button>("CoreOptionsResetBtn")!;
        var saveBtn  = this.FindControl<Button>("CoreOptionsSaveBtn")!;
        optList.Children.Clear();

        var schema = _coreOptions.LoadSchema(coreName);
        if (schema == null || schema.Options.Count == 0)
        {
            optList.Children.Add(new TextBlock
            {
                Text = "No options found for this core.", FontSize = 12, FontFamily = Font("PrimaryFont"),
                Foreground = Brush("TextMutedBrush"), Margin = new Thickness(0, 16, 0, 0),
            });
            resetBtn.IsEnabled = false; resetBtn.Content = "Reset to Defaults"; saveBtn.IsEnabled = false;
            return;
        }

        _pendingCoreOptionValues = new Dictionary<string, string>(_coreOptions.LoadValues(coreName));
        resetBtn.IsEnabled = true; resetBtn.Content = $"Reset {schema.DisplayName} to Defaults"; saveBtn.IsEnabled = true;

        optList.Children.Add(new TextBlock
        {
            Text = schema.DisplayName, FontSize = 14, FontWeight = FontWeight.SemiBold,
            FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 16),
        });

        var comboTheme = this.TryFindResource("DarkComboBox", out var t) ? t as Avalonia.Styling.ControlTheme : null;
        var pco = _pendingCoreOptionValues;
        foreach (var opt in schema.Options)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            section.Children.Add(new TextBlock
            {
                Text = opt.Description, FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            });
            var combo = new ComboBox { MaxWidth = 400, HorizontalAlignment = HorizontalAlignment.Left };
            if (comboTheme != null) combo.Theme = comboTheme;
            foreach (var val in opt.ValidValues) combo.Items.Add(val);
            string current = pco.TryGetValue(opt.Key, out var sv) ? sv : opt.DefaultValue;
            combo.SelectedItem = current;
            if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;
            string capturedKey = opt.Key;
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string v) pco[capturedKey] = v; };
            section.Children.Add(combo);
            optList.Children.Add(section);
        }
    }

    private void CoreOptionsReset()
    {
        if (string.IsNullOrEmpty(_selectedCoreOptionsName)) return;
        // Wipe saved values so defaults apply on the next launch (never push mid-session — some
        // cores crash when critical options change while running).
        _coreOptions.DeleteValues(_selectedCoreOptionsName);
        LoadCoreOptionsForCore(_selectedCoreOptionsName);
    }

    private void CoreOptionsSave()
    {
        if (string.IsNullOrEmpty(_selectedCoreOptionsName)) return;
        _coreOptions.SaveValues(_selectedCoreOptionsName, _pendingCoreOptionValues);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 P2 — Media panel (screenshot/recording folders + hotkey + rec quality).
    //  Recording capture itself lands with the emulator/ffmpeg splinter; the
    //  encoder list is Linux/ffmpeg-oriented (Auto / x264 / VAAPI / NVENC).
    // ════════════════════════════════════════════════════════════════════════

    private bool _loadingMedia;
    private static readonly string[] RecQualities = { "Low", "Medium", "High", "Lossless" };
    private static readonly string[] RecEncoders  = { "Auto", "x264", "VAAPI", "NVENC" };
    private static readonly int[] RecAudioRates   = { 128, 192, 256, 320 };

    private void WireMedia()
    {
        this.FindControl<Button>("BrowseScreenshotsBtn")!.Click += (_, _) => _ = PickMediaFolderAsync(true);
        this.FindControl<Button>("BrowseRecordingsBtn")!.Click  += (_, _) => _ = PickMediaFolderAsync(false);
        this.FindControl<Button>("ClearScreenshotsBtn")!.Click  += (_, _) => ClearMediaFolder(true);
        this.FindControl<Button>("ClearRecordingsBtn")!.Click   += (_, _) => ClearMediaFolder(false);
        this.FindControl<Button>("ResetHotkeyBtn")!.Click       += (_, _) => SetScreenshotHotkey("");
        this.FindControl<TextBox>("ScreenshotHotkeyBox")!.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Tab or Key.Escape) return;
            SetScreenshotHotkey(e.Key.ToString());
            e.Handled = true;
        };
        foreach (var name in new[] { "RecQualityCombo", "RecScaleCombo", "RecEncoderCombo", "RecAudioBitrateCombo" })
            this.FindControl<ComboBox>(name)!.SelectionChanged += (_, _) => SaveRecordingSettings();
        this.FindControl<CheckBox>("RecHighChromaCheck")!.IsCheckedChanged += (_, _) => SaveRecordingSettings();
    }

    private void LoadMediaSettings()
    {
        _loadingMedia = true;
        var prefs = App.Configuration?.GetUserPreferences() ?? new Configuration.UserPreferences();

        this.FindControl<TextBlock>("ScreenshotsDefaultText")!.Text = $"Default: {System.IO.Path.Combine(AppPaths.DataRoot, "Screenshots")}";
        this.FindControl<TextBlock>("RecordingsDefaultText")!.Text  = $"Default: {System.IO.Path.Combine(AppPaths.DataRoot, "Recordings")}";
        SetFolderText("ScreenshotsFolderText", prefs.ScreenshotsFolder);
        SetFolderText("RecordingsFolderText", prefs.RecordingsFolder);
        this.FindControl<TextBox>("ScreenshotHotkeyBox")!.Text = string.IsNullOrEmpty(prefs.ScreenshotKey) ? "F12 (default)" : prefs.ScreenshotKey;

        var rec = App.Configuration?.GetRecordingConfiguration() ?? new Configuration.RecordingConfiguration();
        PopulateCombo("RecQualityCombo", RecQualities, rec.Quality, "High");
        PopulateCombo("RecScaleCombo", new[] { "1x", "2x", "3x", "4x" }, $"{Math.Clamp(rec.OutputScale, 1, 4)}x", "2x");
        PopulateCombo("RecEncoderCombo", RecEncoders, RecEncoders.Contains(rec.Encoder) ? rec.Encoder : "Auto", "Auto");
        PopulateCombo("RecAudioBitrateCombo", new[] { "128 kbps", "192 kbps", "256 kbps", "320 kbps" }, $"{rec.AudioBitrateKbps} kbps", "192 kbps");
        this.FindControl<CheckBox>("RecHighChromaCheck")!.IsChecked = rec.HighChroma;
        _loadingMedia = false;
    }

    private void PopulateCombo(string name, string[] items, string selected, string fallback)
    {
        var combo = this.FindControl<ComboBox>(name)!;
        if (combo.Items.Count == 0) foreach (var i in items) combo.Items.Add(new ComboBoxItem { Content = i });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(c => (string?)c.Content == selected)
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(c => (string?)c.Content == fallback);
    }

    private void SetFolderText(string name, string path)
    {
        var tb = this.FindControl<TextBlock>(name)!;
        bool set = !string.IsNullOrEmpty(path);
        tb.Text = set ? path : "Default";
        tb.Foreground = Brush(set ? "TextPrimaryBrush" : "TextSecondaryBrush");
    }

    private async Task PickMediaFolderAsync(bool screenshots)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = screenshots ? "Select screenshots folder" : "Select recordings folder", AllowMultiple = false });
        string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;
        var prefs = App.Configuration!.GetUserPreferences();
        if (screenshots) { prefs.ScreenshotsFolder = path; AppPaths.SetScreenshotsFolder(path); SetFolderText("ScreenshotsFolderText", path); }
        else             { prefs.RecordingsFolder = path; AppPaths.SetRecordingsFolder(path); SetFolderText("RecordingsFolderText", path); }
        App.Configuration!.SetUserPreferences(prefs); App.Configuration!.ScheduleSave();
    }

    private void ClearMediaFolder(bool screenshots)
    {
        var prefs = App.Configuration!.GetUserPreferences();
        if (screenshots) { prefs.ScreenshotsFolder = ""; AppPaths.SetScreenshotsFolder(""); SetFolderText("ScreenshotsFolderText", ""); }
        else             { prefs.RecordingsFolder = ""; AppPaths.SetRecordingsFolder(""); SetFolderText("RecordingsFolderText", ""); }
        App.Configuration!.SetUserPreferences(prefs); App.Configuration!.ScheduleSave();
    }

    private void SetScreenshotHotkey(string keyName)
    {
        var prefs = App.Configuration!.GetUserPreferences();
        prefs.ScreenshotKey = keyName;
        App.Configuration!.SetUserPreferences(prefs); App.Configuration!.ScheduleSave();
        this.FindControl<TextBox>("ScreenshotHotkeyBox")!.Text = string.IsNullOrEmpty(keyName) ? "F12 (default)" : keyName;
    }

    private void SaveRecordingSettings()
    {
        if (_loadingMedia) return;
        var rec = App.Configuration!.GetRecordingConfiguration();
        rec.Quality = ComboText("RecQualityCombo") ?? "High";
        rec.OutputScale = int.TryParse((ComboText("RecScaleCombo") ?? "2x").TrimEnd('x'), out var s) ? s : 2;
        rec.Encoder = ComboText("RecEncoderCombo") ?? "Auto";
        rec.AudioBitrateKbps = int.TryParse((ComboText("RecAudioBitrateCombo") ?? "192 kbps").Split(' ')[0], out var b) ? b : 192;
        rec.HighChroma = this.FindControl<CheckBox>("RecHighChromaCheck")!.IsChecked == true;
        App.Configuration!.SetRecordingConfiguration(rec); App.Configuration!.ScheduleSave();
    }

    private string? ComboText(string name) =>
        (this.FindControl<ComboBox>(name)!.SelectedItem as ComboBoxItem)?.Content as string;

    private IBrush? Brush(string key) => this.TryFindResource(key, out var v) ? v as IBrush : null;
    private FontFamily Font(string key) => this.TryFindResource(key, out var v) && v is FontFamily f ? f : FontFamily.Default;
    private static IBrush ParseBrush(string? hex, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(hex) ? fallback : hex)); }
        catch { return new SolidColorBrush(Color.Parse(fallback)); }
    }
}
