using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Platform;
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

    // Only one Preferences window at a time — a second would spin up a second SDL gamepad poller.
    private static PreferencesWindow? _open;

    /// <summary>Open Preferences (reusing the existing window if already open), optionally landing on
    /// the Controls panel with <paramref name="console"/> preselected, or on the nav section named by
    /// <paramref name="panel"/> (a RadioButton name, e.g. "NavCores").</summary>
    public static void OpenOrFocus(Window owner, string? console = null, string? panel = null)
    {
        if (_open != null)
        {
            if (!string.IsNullOrEmpty(console)) _open.SelectConsole(console);
            if (!string.IsNullOrEmpty(panel)) _open.SelectPanelNav(panel);
            _open.Activate();
            return;
        }
        _open = new PreferencesWindow(console);
        if (!string.IsNullOrEmpty(panel)) _open.SelectPanelNav(panel);
        _open.Show(owner);
    }

    /// <summary>Switch to a nav section by its RadioButton name (e.g. "NavCores").</summary>
    public void SelectPanelNav(string nav)
    {
        var rb = this.FindControl<RadioButton>(nav);
        if (rb != null) rb.IsChecked = true;   // IsCheckedChanged → ShowPanel
    }

    /// <summary>Reselect the Controls system combo (and reload its mappings) for a console tag.</summary>
    public void SelectConsole(string tag)
    {
        var combo = this.FindControl<ComboBox>("SystemComboBox");
        var match = combo?.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        if (match != null) combo!.SelectedItem = match;   // SelectionChanged → LoadConsole
    }

    public PreferencesWindow() : this(null) { }

    public PreferencesWindow(string? initialConsole)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(initialConsole)) _currentConsole = initialConsole;

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
        WireAchievements();
        this.FindControl<Button>("CoreOptionsResetBtn")!.Click += (_, _) => CoreOptionsReset();
        this.FindControl<Button>("CoreOptionsSaveBtn")!.Click += (_, _) => CoreOptionsSave();
        WireMedia();
        WireBackups();

        // Controls is the default tab (NavControls is checked in XAML before our handlers were
        // attached, so its IsCheckedChanged didn't fire) — init it explicitly.
        ShowPanel("PanelControls");
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowCts.Cancel();
        _ctrl?.Dispose();
        if (ReferenceEquals(_open, this)) _open = null;
        _pauseRunner?.Dispose();
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
        // Stop the pause-effect preview's frame timer whenever we leave the Theme panel.
        if (target != "PanelTheme") _pauseRunner?.Stop(immediate: true);
        if (target == "PanelAbout") LoadAboutSettings();
        if (target == "PanelTheme") LoadThemeSettings();
        if (target == "PanelLibrary") LoadLibrarySettings();
        if (target == "PanelSnaps") LoadSnapsSettings();
        if (target == "PanelAchievements") LoadAchievementsSettings();
        if (target == "PanelCoreOptions") BuildCoreOptionsTab();
        if (target == "PanelMedia") LoadMediaSettings();
        if (target == "PanelBackups") LoadBackupsSettings();
        if (target == "PanelSystemFiles") BuildBiosPanel();
        if (target == "PanelCores") BuildCoresPanel();
        if (target == "PanelControls") InitControls();
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
        this.FindControl<Button>("AboutUpdateNowBtn")!.Click += (_, _) => _ = RunInAppUpdateAsync();
        this.FindControl<Button>("AboutOpenLatestReleaseBtn")!.Click += (_, _) => OpenUrl(_latestReleaseUrl ?? GitHubReleasesUrl);
        this.FindControl<Button>("AboutRecheckBtn")!.Click += (_, _) => _ = CheckLatestReleaseAsync();
        this.FindControl<Button>("AboutLicenseBtn")!.Click += (_, _) => OpenUrl(GitHubRepoUrl + "/blob/main/LICENSE");
        this.FindControl<Button>("AboutCoresBtn")!.Click += (_, _) => OpenUrl(GitHubRepoUrl + "#credits");
    }

    private readonly List<Services.UpdateService.ReleaseAsset> _latestAssets = new();
    private Services.UpdateService.ReleaseAsset? _pendingUpdateAsset;
    private Services.UpdateService.InstallKind _pendingUpdateKind;

    /// <summary>In-app update: download the right artifact for this install kind and
    /// apply it (tarball self-swap or pkexec dpkg). On success the app restarts
    /// itself; this method only returns on failure/cancel.</summary>
    private async Task RunInAppUpdateAsync()
    {
        var asset = _pendingUpdateAsset;
        if (asset == null) return;
        var btn = this.FindControl<Button>("AboutUpdateNowBtn")!;
        var recheck = this.FindControl<Button>("AboutRecheckBtn")!;
        var status = this.FindControl<TextBlock>("AboutUpdateStatusText")!;
        btn.IsEnabled = false;
        recheck.IsEnabled = false;
        var progress = new Progress<(int pct, string msg)>(p => status.Text = p.msg);
        string? error = await Services.UpdateService.DownloadAndApplyAsync(
            asset, _pendingUpdateKind, progress, _windowCts.Token);
        // Reaching here means it did NOT hand off to the relaunch script.
        status.Text = error ?? "Update did not complete.";
        btn.IsEnabled = true;
        recheck.IsEnabled = true;
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
        this.FindControl<Button>("AboutUpdateNowBtn")!.IsVisible = false;
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

            // Release assets — what the in-app updater downloads (names are the
            // packaging/build-release.sh contract; see UpdateService).
            _latestAssets.Clear();
            if (obj["assets"] is Newtonsoft.Json.Linq.JArray assetsArr)
                foreach (var a in assetsArr)
                    _latestAssets.Add(new Services.UpdateService.ReleaseAsset(
                        a.Value<string>("name") ?? "",
                        a.Value<string>("browser_download_url") ?? "",
                        a.Value<long?>("size") ?? 0));

            latest.Text = string.IsNullOrWhiteSpace(tag) ? "—" : tag;

            if (TryCompareVersions(tag, out int cmp))
            {
                if (cmp > 0)
                {
                    var kind = Services.UpdateService.DetectInstallKind();
                    var asset = Services.UpdateService.PickAsset(kind, _latestAssets);
                    bool canSelfUpdate = asset != null
                        && kind is Services.UpdateService.InstallKind.Deb or Services.UpdateService.InstallKind.SelfContained;
                    status.Text = kind switch
                    {
                        Services.UpdateService.InstallKind.Dev => "A newer release is available. (Development build — update via git.)",
                        _ when !canSelfUpdate => "A newer release is available — open it on GitHub to update.",
                        _ => "A newer release is available.",
                    };
                    status.Foreground = this.TryFindResource("AccentBrush", out var a) ? a as IBrush : Brushes.OrangeRed;
                    openLatest.IsVisible = true;
                    this.FindControl<Button>("AboutUpdateNowBtn")!.IsVisible = canSelfUpdate;
                    _pendingUpdateAsset = canSelfUpdate ? asset : null;
                    _pendingUpdateKind = kind;
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
        Services.ShellOpen.Open(url);
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

        // Layout sliders — clamp, label, persist, and push into the grid resources live.
        WireLayoutSlider("PaddingSlider", "PaddingValueLabel", 8, 64, (c, v) => c.GridPadding = v);
        WireLayoutSlider("CardSizeSlider", "CardSizeValueLabel", 148, 280, (c, v) => c.CardWidth = v);
        WireLayoutSlider("SpacingSlider", "SpacingValueLabel", 4, 96, (c, v) => c.CardSpacing = v);

        // Pause-effect picker + intensity (live preview restarts on change).
        var pauseCombo = this.FindControl<ComboBox>("PauseEffectCombo")!;
        foreach (var entry in PauseEffects.PauseEffectRegistry.All)
            pauseCombo.Items.Add(new ComboBoxItem { Content = entry.DisplayName, Tag = entry.Id });
        pauseCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressPauseChange) return;
            if (pauseCombo.SelectedItem is ComboBoxItem { Tag: string id })
            {
                UpdateThemeConfig(c => c.PauseEffect = id);
                RestartPausePreview();
            }
        };
        var styleCombo = this.FindControl<ComboBox>("WindowStyleCombo")!;
        styleCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressPauseChange) return;   // shared load guard for the Theme panel
            if (styleCombo.SelectedItem is ComboBoxItem { Tag: string style })
            {
                UpdateThemeConfig(c => c.WindowButtonStyle = style);
                App.ApplyWindowButtonStyle(style);   // live, app-wide
            }
        };

        var intensity = this.FindControl<Slider>("PauseEffectIntensitySlider")!;
        intensity.PropertyChanged += (_, e) =>
        {
            if (_suppressPauseChange || e.Property.Name != nameof(Slider.Value)) return;
            int pct = System.Math.Clamp((int)intensity.Value, 50, 200);
            this.FindControl<TextBlock>("PauseEffectIntensityValueLabel")!.Text = $"{pct}%";
            UpdateThemeConfig(c => c.PauseEffectIntensity = pct / 100.0);
            // Re-seed the live effect in place (no fade/realloc flicker); full restart only if idle.
            if (_pauseRunner is { HasActiveEffect: true }) _pauseRunner.SetIntensity(pct / 100.0);
            else RestartPausePreview();
        };
    }

    private bool _suppressPauseChange;
    private PauseEffects.PauseEffectRunner? _pauseRunner;

    private void LoadPauseSettings()
    {
        var cfg = App.Configuration?.GetThemeConfiguration();
        if (cfg == null) return;
        _suppressPauseChange = true;
        var combo = this.FindControl<ComboBox>("PauseEffectCombo")!;
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == cfg.PauseEffect)
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        int pct = System.Math.Clamp((int)System.Math.Round(cfg.PauseEffectIntensity * 100), 50, 200);
        this.FindControl<Slider>("PauseEffectIntensitySlider")!.Value = pct;
        this.FindControl<TextBlock>("PauseEffectIntensityValueLabel")!.Text = $"{pct}%";

        var styleCombo = this.FindControl<ComboBox>("WindowStyleCombo")!;
        styleCombo.SelectedItem = styleCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == cfg.WindowButtonStyle)
            ?? styleCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _suppressPauseChange = false;
    }

    // (Re)start the live preview with the saved effect + intensity. "none" stops it.
    private void RestartPausePreview()
    {
        var host = this.FindControl<PauseEffects.PauseEffectHost>("PauseEffectPreview");
        if (host == null) return;
        _pauseRunner ??= new PauseEffects.PauseEffectRunner(host);

        var cfg = App.Configuration?.GetThemeConfiguration();
        string id = cfg?.PauseEffect ?? "none";
        double intensity = System.Math.Clamp(cfg?.PauseEffectIntensity ?? 1.0, 0.5, 2.0);
        var entry = PauseEffects.PauseEffectRegistry.Find(id);
        if (entry == null || entry.Id == PauseEffects.PauseEffectRegistry.NoneId)
        {
            _pauseRunner.Stop(immediate: true);
            return;
        }
        var inst = entry.Factory();
        if (entry.IsPixel) _pauseRunner.Start((PauseEffects.IPixelPauseEffect)inst, intensity);
        else _pauseRunner.Start((PauseEffects.IPauseEffect)inst, intensity);
    }

    private void WireLayoutSlider(string slider, string label, int min, int max, Action<Configuration.ThemeConfiguration, int> set)
    {
        var s = this.FindControl<Slider>(slider)!;
        s.PropertyChanged += (_, e) =>
        {
            if (_suppressLayoutChange || e.Property.Name != nameof(Slider.Value)) return;
            int v = System.Math.Clamp((int)s.Value, min, max);
            this.FindControl<TextBlock>(label)!.Text = v.ToString();
            UpdateThemeConfig(c => set(c, v));
            App.ApplyLibraryLayout();   // live re-layout of the box-art grid
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
        LoadLayoutSettings();
        LoadPauseSettings();
        RestartPausePreview();   // animate the preview while the Theme panel is open
    }

    private bool _suppressLayoutChange;
    private void LoadLayoutSettings()
    {
        var cfg = App.Configuration?.GetThemeConfiguration();
        if (cfg == null) return;
        _suppressLayoutChange = true;
        int padding = System.Math.Clamp(cfg.GridPadding, 8, 64);
        int cardW   = System.Math.Clamp(cfg.CardWidth, 148, 280);
        int spacing = System.Math.Clamp(cfg.CardSpacing, 4, 96);
        this.FindControl<Slider>("PaddingSlider")!.Value = padding;
        this.FindControl<TextBlock>("PaddingValueLabel")!.Text = padding.ToString();
        this.FindControl<Slider>("CardSizeSlider")!.Value = cardW;
        this.FindControl<TextBlock>("CardSizeValueLabel")!.Text = cardW.ToString();
        this.FindControl<Slider>("SpacingSlider")!.Value = spacing;
        this.FindControl<TextBlock>("SpacingValueLabel")!.Text = spacing.ToString();
        _suppressLayoutChange = false;
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
    //  A8b — Achievements panel (RetroAchievements login + hardcore toggle)
    // ════════════════════════════════════════════════════════════════════════

    private bool _achievementsLoaded;
    private bool _suppressRaSave;

    private void WireAchievements()
    {
        this.FindControl<ToggleSwitch>("RAHardcoreToggle")!.IsCheckedChanged += (_, _) => SaveAchievementsSettings();
        this.FindControl<ToggleSwitch>("RASyncFollowsToggle")!.IsCheckedChanged += (_, _) => SaveAchievementsSettings();
        this.FindControl<TextBox>("RAUsernameBox")!.LostFocus += (_, _) => SaveAchievementsSettings();
        this.FindControl<TextBox>("RAPasswordBox")!.LostFocus += (_, _) => SaveAchievementsSettings();
        this.FindControl<TextBox>("RAApiKeyBox")!.LostFocus += (_, _) => SaveAchievementsSettings();
        this.FindControl<Button>("RATestBtn")!.Click += (_, _) => _ = RaTestLoginAsync();
    }

    private void LoadAchievementsSettings()
    {
        var ra = App.Configuration?.GetRetroAchievementsConfiguration() ?? new Configuration.RetroAchievementsConfiguration();
        _suppressRaSave = true;
        this.FindControl<TextBox>("RAUsernameBox")!.Text = ra.Username;
        this.FindControl<TextBox>("RAPasswordBox")!.Text = ra.Password;
        this.FindControl<TextBox>("RAApiKeyBox")!.Text = ra.ApiKey;
        this.FindControl<ToggleSwitch>("RAHardcoreToggle")!.IsChecked = ra.HardcoreMode;
        this.FindControl<TextBlock>("RATokenStatus")!.Text = !string.IsNullOrEmpty(ra.Token)
            ? "Login token saved — password not required for future sessions."
            : "No login token yet — password required for first login.";
        this.FindControl<ToggleSwitch>("RASyncFollowsToggle")!.IsChecked = ra.SyncFollowsOnLaunch;

        // Toast-appearance customizer (built once, repopulated on every entry).
        BuildToastAppearanceUI();
        PopulateToastAppearance();

        _suppressRaSave = false;
        _achievementsLoaded = true;
        UpdateToastConditionalRows();
    }

    private void SaveAchievementsSettings()
    {
        if (_suppressRaSave || !_achievementsLoaded) return;
        var ra = App.Configuration?.GetRetroAchievementsConfiguration();
        if (ra == null) return;
        ra.Username     = (this.FindControl<TextBox>("RAUsernameBox")!.Text ?? "").Trim();
        ra.Password     = this.FindControl<TextBox>("RAPasswordBox")!.Text ?? "";
        ra.ApiKey       = (this.FindControl<TextBox>("RAApiKeyBox")!.Text ?? "").Trim();
        ra.HardcoreMode = this.FindControl<ToggleSwitch>("RAHardcoreToggle")!.IsChecked == true;
        ra.SyncFollowsOnLaunch = this.FindControl<ToggleSwitch>("RASyncFollowsToggle")!.IsChecked == true;
        App.Configuration!.SetRetroAchievementsConfiguration(ra);
        App.Configuration!.ScheduleSave();
    }

    private async Task RaTestLoginAsync()
    {
        var btn = this.FindControl<Button>("RATestBtn")!;
        var label = this.FindControl<TextBlock>("RAStatusLabel")!;
        btn.IsEnabled = false;
        label.Text = "Testing…";
        label.Foreground = Brush("TextMutedBrush");
        try
        {
            SaveAchievementsSettings();   // commit current fields before the round-trip
            var svc = new Services.RetroAchievementsService();
            var (error, token) = await svc.TestLoginAsync(
                (this.FindControl<TextBox>("RAUsernameBox")!.Text ?? "").Trim(),
                this.FindControl<TextBox>("RAPasswordBox")!.Text ?? "");
            if (error == null && !string.IsNullOrEmpty(token))
            {
                var ra = App.Configuration?.GetRetroAchievementsConfiguration();
                if (ra != null)
                {
                    ra.Token = token;
                    App.Configuration!.SetRetroAchievementsConfiguration(ra);
                    App.Configuration!.ScheduleSave();
                }
                this.FindControl<TextBlock>("RATokenStatus")!.Text =
                    "Login token saved — password not required for future sessions.";
                label.Text = "Connected";
                label.Foreground = new SolidColorBrush(Color.Parse("#28C840"));
            }
            else
            {
                label.Text = error ?? "Login failed";
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
        // Brief confirmation — upstream saves silently, which reads as "the button did nothing"
        // (values only take effect on the next game launch, so there's no other visible change).
        var saveBtn = this.FindControl<Button>("CoreOptionsSaveBtn")!;
        var original = saveBtn.Content;
        saveBtn.Content = "Saved ✓";
        saveBtn.IsEnabled = false;
        Avalonia.Threading.DispatcherTimer.RunOnce(
            () => { saveBtn.Content = original; saveBtn.IsEnabled = true; },
            TimeSpan.FromMilliseconds(1200));
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
            // Ignore navigation keys and bare modifiers — they're unusable as a standalone hotkey.
            if (e.Key is Key.Tab or Key.Escape or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift
                or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
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

    // ════════════════════════════════════════════════════════════════════════
    //  U5 P3 — Backups panel (local recursive-copy backup; cloud sync deferred)
    // ════════════════════════════════════════════════════════════════════════

    private void WireBackups()
    {
        this.FindControl<Button>("BrowseBackupFolderBtn")!.Click += (_, _) => _ = BrowseBackupFolderAsync();
        this.FindControl<Button>("ClearBackupFolderBtn")!.Click  += (_, _) =>
        {
            var prefs = App.Configuration!.GetUserPreferences();
            prefs.BackupFolder = "";
            App.Configuration!.SetUserPreferences(prefs); App.Configuration!.ScheduleSave();
            SetBackupFolderText("");
        };
        this.FindControl<Button>("BackupNowBtn")!.Click       += (_, _) => _ = BackupNowAsync();
        this.FindControl<Button>("RestoreBackupBtn")!.Click   += (_, _) => _ = RestoreBackupAsync();
    }

    private bool _cloudSyncWired;
    private bool _suppressCloudSave;

    private void LoadBackupsSettings()
    {
        SetBackupFolderText(App.Configuration?.GetUserPreferences().BackupFolder ?? "");
        WireCloudSync();
        _suppressCloudSave = true;
        try
        {
            var svc = Services.GitHubSyncService.Instance;
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            var status = this.FindControl<TextBlock>("CloudSyncStatusText")!;
            var signIn = this.FindControl<Button>("CloudSyncSignInBtn")!;
            var settings = this.FindControl<StackPanel>("CloudSyncSettingsPanel")!;

            if (!svc.IsConfigured)
            {
                // No OAuth client id baked into this build — surface it honestly
                // instead of a sign-in that can't work.
                status.Text = "Cloud sync isn't configured in this build (missing OAuth app id).";
                signIn.IsEnabled = false;
            }
            else if (svc.IsAuthenticated && !string.IsNullOrEmpty(svc.Username))
            {
                status.Text = $"Signed in as {svc.Username}";
                signIn.Content = "Sign Out";
                settings.IsVisible = true;
            }

            if (cfg != null)
            {
                this.FindControl<RadioButton>("SyncOnClose")!.IsChecked = cfg.SyncTiming == "on_close";
                this.FindControl<RadioButton>("SyncPeriodic")!.IsChecked = cfg.SyncTiming == "periodic";
                this.FindControl<RadioButton>("SyncManual")!.IsChecked = cfg.SyncTiming == "manual";
                this.FindControl<CheckBox>("SyncEncryptionEnabled")!.IsChecked = cfg.EncryptionEnabled;
                this.FindControl<StackPanel>("PassphrasePanel")!.IsVisible = cfg.EncryptionEnabled;
                this.FindControl<TextBlock>("PassphraseHint")!.IsVisible = cfg.EncryptionEnabled;
                this.FindControl<CheckBox>("SyncPerPcRepo")!.IsChecked = cfg.UsePerPcRepo;
                UpdatePerPcRepoCopy(cfg.UsePerPcRepo);
            }
        }
        finally { _suppressCloudSave = false; }
    }

    private void WireCloudSync()
    {
        if (_cloudSyncWired) return;
        _cloudSyncWired = true;
        this.FindControl<Button>("CloudSyncSignInBtn")!.Click += (_, _) => _ = CloudSyncSignInAsync();
        this.FindControl<Button>("SyncNowBtn")!.Click += (_, _) => _ = SyncNowAsync();
        this.FindControl<Button>("SyncPassphraseSaveBtn")!.Click += (_, _) => SyncPassphraseSave();
        foreach (var name in new[] { "SyncOnClose", "SyncPeriodic", "SyncManual" })
            this.FindControl<RadioButton>(name)!.IsCheckedChanged += (_, _) => SyncTimingChanged();
        this.FindControl<CheckBox>("SyncEncryptionEnabled")!.IsCheckedChanged += (_, _) => SyncEncryptionChanged();
        this.FindControl<CheckBox>("SyncPerPcRepo")!.IsCheckedChanged += (_, _) => SyncPerPcRepoChanged();
    }

    /// <summary>
    /// Mode-specific explainer under the per-PC repo toggle. The wording
    /// must make the tradeoff unmistakable: shared = follows you between
    /// PCs, separate = backup unique to this machine that other PCs never
    /// touch.
    /// </summary>
    private void UpdatePerPcRepoCopy(bool perPc)
    {
        this.FindControl<TextBlock>("SyncRepoExplainText")!.Text = perPc
            ? "On: this PC backs up to its own repository. Saves and the game "
              + "library on this PC stay unique to it — they will not appear on "
              + "your other machines, and other machines can't overwrite them."
            : "Off: this PC shares one cloud repository with your other PCs — "
              + "saves and your game library follow you between machines.";
        this.FindControl<TextBlock>("SyncRepoNameText")!.Text =
            $"Repository in use: {Services.GitHubSyncService.EffectiveRepoName}";
    }

    private void SyncPerPcRepoChanged()
    {
        if (_suppressCloudSave) return;
        var cfg = App.Configuration?.GetCloudSyncConfiguration();
        if (cfg == null) return;
        cfg.UsePerPcRepo = this.FindControl<CheckBox>("SyncPerPcRepo")!.IsChecked == true;
        App.Configuration!.SetCloudSyncConfiguration(cfg);
        App.Configuration!.ScheduleSave();

        // Everything cached from the previous repo is now wrong.
        var svc = Services.GitHubSyncService.Instance;
        svc.ResetRepoBinding();
        UpdatePerPcRepoCopy(cfg.UsePerPcRepo);

        // The shared repo was created at sign-in; a per-PC repo may not
        // exist yet — create it now so the first sync doesn't 404.
        if (svc.IsAuthenticated && cfg.UsePerPcRepo)
            _ = svc.EnsureRepoExistsAsync();
    }

    private async Task CloudSyncSignInAsync()
    {
        var svc = Services.GitHubSyncService.Instance;
        var status = this.FindControl<TextBlock>("CloudSyncStatusText")!;
        var signIn = this.FindControl<Button>("CloudSyncSignInBtn")!;
        var settings = this.FindControl<StackPanel>("CloudSyncSettingsPanel")!;
        var flowPanel = this.FindControl<Border>("DeviceFlowPanel")!;

        if (svc.IsAuthenticated)
        {
            svc.SignOut();
            status.Text = "Not signed in";
            signIn.Content = "Sign in with GitHub";
            settings.IsVisible = false;
            flowPanel.IsVisible = false;
            return;
        }

        try
        {
            signIn.IsEnabled = false;
            var flow = await svc.BeginDeviceFlowAsync();
            this.FindControl<TextBlock>("DeviceFlowCodeText")!.Text = flow.UserCode;
            flowPanel.IsVisible = true;
            Services.ShellOpen.Open(flow.VerificationUri);

            bool success = await svc.PollForTokenAsync(flow.DeviceCode, flow.Interval, flow.ExpiresIn);
            flowPanel.IsVisible = false;

            if (success)
            {
                await svc.EnsureRepoExistsAsync();
                await svc.RefreshShaCacheAsync();
                status.Text = $"Signed in as {svc.Username}";
                signIn.Content = "Sign Out";
                settings.IsVisible = true;
            }
            else
            {
                status.Text = "Authorization failed or timed out";
            }
        }
        catch (Exception ex)
        {
            flowPanel.IsVisible = false;
            status.Text = $"Error: {ex.Message}";
        }
        finally { signIn.IsEnabled = true; }
    }

    private void SyncTimingChanged()
    {
        if (_suppressCloudSave) return;
        var cfg = App.Configuration?.GetCloudSyncConfiguration();
        if (cfg == null) return;
        if (this.FindControl<RadioButton>("SyncOnClose")!.IsChecked == true) cfg.SyncTiming = "on_close";
        else if (this.FindControl<RadioButton>("SyncPeriodic")!.IsChecked == true) cfg.SyncTiming = "periodic";
        else if (this.FindControl<RadioButton>("SyncManual")!.IsChecked == true) cfg.SyncTiming = "manual";
        App.Configuration!.SetCloudSyncConfiguration(cfg);
        App.Configuration!.ScheduleSave();
    }

    private void SyncEncryptionChanged()
    {
        if (_suppressCloudSave) return;
        var cfg = App.Configuration?.GetCloudSyncConfiguration();
        if (cfg == null) return;
        cfg.EncryptionEnabled = this.FindControl<CheckBox>("SyncEncryptionEnabled")!.IsChecked == true;
        this.FindControl<StackPanel>("PassphrasePanel")!.IsVisible = cfg.EncryptionEnabled;
        this.FindControl<TextBlock>("PassphraseHint")!.IsVisible = cfg.EncryptionEnabled;
        App.Configuration!.SetCloudSyncConfiguration(cfg);
        App.Configuration!.ScheduleSave();
    }

    private void SyncPassphraseSave()
    {
        var cfg = App.Configuration?.GetCloudSyncConfiguration();
        if (cfg == null) return;
        var box = this.FindControl<TextBox>("SyncPassphraseBox")!;
        string passphrase = box.Text ?? "";
        if (string.IsNullOrEmpty(passphrase)) return;
        cfg.PassphraseProtected = Services.GitHubSyncService.ProtectString(passphrase);
        App.Configuration!.SetCloudSyncConfiguration(cfg);
        App.Configuration!.ScheduleSave();
        box.Text = "";
        this.FindControl<TextBlock>("SyncStatusText")!.Text = "Passphrase saved";
    }

    private async Task SyncNowAsync()
    {
        var svc = Services.GitHubSyncService.Instance;
        if (!svc.IsAuthenticated) return;
        var btn = this.FindControl<Button>("SyncNowBtn")!;
        var status = this.FindControl<TextBlock>("SyncStatusText")!;
        btn.IsEnabled = false;
        status.Text = "Syncing…";
        try
        {
            var db = new Services.DatabaseService();
            var result = await Task.Run(() => svc.FullSyncAsync(db));
            status.Text = $"Synced at {DateTime.Now:h:mm tt} — {result.Uploaded} up, {result.Downloaded} down"
                + (result.Errors > 0 ? $", {result.Errors} errors" : "");
        }
        catch (Exception ex) { status.Text = $"Sync failed: {ex.Message}"; }
        finally { btn.IsEnabled = true; }
    }

    private void SetBackupFolderText(string path)
    {
        var tb = this.FindControl<TextBlock>("BackupFolderPathText")!;
        bool set = !string.IsNullOrEmpty(path);
        tb.Text = set ? path : "Not set";
        tb.Foreground = Brush(set ? "TextPrimaryBrush" : "TextSecondaryBrush");
    }

    private async Task BrowseBackupFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select backup folder", AllowMultiple = false });
        string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;
        var prefs = App.Configuration!.GetUserPreferences();
        prefs.BackupFolder = path;
        App.Configuration!.SetUserPreferences(prefs); App.Configuration!.ScheduleSave();
        SetBackupFolderText(path);
    }

    private async Task BackupNowAsync()
    {
        string dest = App.Configuration?.GetUserPreferences().BackupFolder ?? "";
        if (string.IsNullOrEmpty(dest)) return;
        var btn = this.FindControl<Button>("BackupNowBtn")!;
        var status = this.FindControl<TextBlock>("BackupFolderStatusText")!;
        btn.IsEnabled = false; status.Text = "Backing up…";
        try
        {
            await Task.Run(() =>
            {
                string root = AppPaths.DataRoot;
                // Battery saves live in Saves/ (the session's RetroArch-style scheme),
                // not upstream's BatterySaves/ — the old name silently backed up nothing.
                string battery = System.IO.Path.Combine(root, "Saves");
                if (System.IO.Directory.Exists(battery)) CopyDirectoryRecursive(battery, System.IO.Path.Combine(dest, "Saves"));
                string states = System.IO.Path.Combine(root, "Save States");
                if (System.IO.Directory.Exists(states)) CopyDirectoryRecursive(states, System.IO.Path.Combine(dest, "Save States"));
                string db = System.IO.Path.Combine(root, "library.db");
                if (System.IO.File.Exists(db)) System.IO.File.Copy(db, System.IO.Path.Combine(dest, "library.db"), overwrite: true);
            });
            status.Text = $"Backup complete — {DateTime.Now:g}";
        }
        catch (Exception ex) { status.Text = $"Error: {ex.Message}"; }
        finally { btn.IsEnabled = true; }
    }

    private async Task RestoreBackupAsync()
    {
        string src = App.Configuration?.GetUserPreferences().BackupFolder ?? "";
        if (string.IsNullOrEmpty(src)) return;
        var status = this.FindControl<TextBlock>("BackupFolderStatusText")!;
        // Stat the (possibly removable/network) backup path off the UI thread.
        var (hasDb, hasSaves, hasStates) = await Task.Run(() => (
            System.IO.File.Exists(System.IO.Path.Combine(src, "library.db")),
            System.IO.Directory.Exists(System.IO.Path.Combine(src, "Saves"))
                || System.IO.Directory.Exists(System.IO.Path.Combine(src, "BatterySaves")),   // old backups
            System.IO.Directory.Exists(System.IO.Path.Combine(src, "Save States"))));
        if (!hasDb && !hasSaves && !hasStates) { status.Text = "No backup data found in that folder."; return; }

        var parts = new System.Collections.Generic.List<string>();
        if (hasDb) parts.Add("library database");
        if (hasSaves) parts.Add("battery saves");
        if (hasStates) parts.Add("save states");
        bool ok = await new ConfirmDialog("Restore from Backup",
            $"This will overwrite your current {string.Join(", ", parts)} with the backup copy.\n\nContinue?",
            "Restore", danger: true).ShowDialog<bool>(this);
        if (!ok) return;

        var btn = this.FindControl<Button>("RestoreBackupBtn")!;
        btn.IsEnabled = false; status.Text = "Restoring…";
        try
        {
            await Task.Run(() =>
            {
                string root = AppPaths.DataRoot;
                if (hasSaves)
                {
                    string newSrc = System.IO.Path.Combine(src, "Saves");
                    string oldSrc = System.IO.Path.Combine(src, "BatterySaves");
                    // New backups restore Saves/→Saves/; backups made before the
                    // folder-name fix restore BatterySaves/→Saves/ (same payload).
                    if (System.IO.Directory.Exists(newSrc)) CopyDirectoryRecursive(newSrc, System.IO.Path.Combine(root, "Saves"));
                    else if (System.IO.Directory.Exists(oldSrc)) CopyDirectoryRecursive(oldSrc, System.IO.Path.Combine(root, "Saves"));
                }
                if (hasStates) CopyDirectoryRecursive(System.IO.Path.Combine(src, "Save States"), System.IO.Path.Combine(root, "Save States"));
                if (hasDb)     System.IO.File.Copy(System.IO.Path.Combine(src, "library.db"), System.IO.Path.Combine(root, "library.db"), overwrite: true);
            });
            status.Text = "Restore complete — restart recommended";
        }
        catch (Exception ex) { status.Text = $"Error: {ex.Message}"; }
        finally { btn.IsEnabled = true; }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        System.IO.Directory.CreateDirectory(destDir);
        foreach (string file in System.IO.Directory.GetFiles(sourceDir))
            System.IO.File.Copy(file, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file)), overwrite: true);
        foreach (string dir in System.IO.Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir)));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 P4 — System Files (BIOS): DB-driven scan + Category→Console→BIOS
    //  accordion + drag-drop archive/MD5 importer.
    // ════════════════════════════════════════════════════════════════════════

    private static readonly (string Category, string[] ConsoleDisplays)[] BiosCategories =
    {
        ("Nintendo", new[] { "Famicom Disk System", "Game Boy Advance" }),
        ("Sega",     new[] { "Sega CD", "Saturn" }),
        ("Sony",     new[] { "PlayStation", "PlayStation 2" }),
        ("NEC",      new[] { "TurboGrafx-CD" }),
        ("Arcade",   new[] { "Neo Geo" }),   // NeoCD entries exist for launch pre-flight only (upstream hides them here)
        ("Other",    new[] { "3DO", "Philips CD-i" }),
    };

    private bool _biosDndWired;

    private async void BuildBiosPanel()
    {
        var panel = this.FindControl<StackPanel>("BiosPanel")!;
        if (!_biosDndWired)
        {
            var host = this.FindControl<Grid>("PanelSystemFiles")!;
            DragDrop.SetAllowDrop(host, true);
            host.AddHandler(DragDrop.DragOverEvent, (_, e) =>
                e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
            host.AddHandler(DragDrop.DropEvent, (_, e) => _ = OnBiosDropAsync(e));
            _biosDndWired = true;
        }

        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = "Scanning…", FontFamily = Font("PrimaryFont"), FontSize = 12,
            Foreground = Brush("TextMutedBrush"), Margin = new Thickness(2, 8, 0, 0) });

        string sysDir = AppPaths.GetFolder("System");
        var (existing, verified, romDirs) = await Task.Run(() => BiosScan(sysDir));
        // Bail if the user navigated away while scanning.
        if (!this.FindControl<Grid>("PanelSystemFiles")!.IsVisible) return;
        panel.Children.Clear();
        RenderBios(panel, sysDir, existing, verified, romDirs);
    }

    // Port of BuildBiosScan: DB games → per-console ROM dirs (+ their subdirs) → File.Exists set,
    // plus MD5 verification computed here (off-thread) so per-row render never hashes on the UI thread.
    private static (HashSet<string> Existing, HashSet<string> Verified, Dictionary<string, string[]> RomDirs) BiosScan(string sysDir)
    {
        var romDirs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var games = new Services.DatabaseService().GetAllGames();
            romDirs = games.Where(g => !string.IsNullOrEmpty(g.RomPath))
                .GroupBy(g => g.Console)
                .ToDictionary(grp => grp.Key, grp =>
                {
                    var baseDirs = grp.Select(g => System.IO.Path.GetDirectoryName(AppPaths.FromStoragePath(g.RomPath)))
                        .Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var expanded = new List<string>(baseDirs!);
                    foreach (var dir in baseDirs) { try { expanded.AddRange(System.IO.Directory.EnumerateDirectories(dir!)); } catch { } }
                    return expanded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }, StringComparer.OrdinalIgnoreCase);
        }
        catch { }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Note(Services.BiosEntry e, string path)
        {
            if (!SafeExists(path)) return;
            existing.Add(path);
            if (e.Md5 == null || VerifyMd5(path, e.Md5)) verified.Add(path);  // hashing stays off the UI thread
        }
        foreach (var e in Services.KnownBios.All)
        {
            Note(e, System.IO.Path.Combine(sysDir, e.Filename));
            if (romDirs.TryGetValue(e.Console, out var dirs))
            {
                string leaf = System.IO.Path.GetFileName(e.Filename);
                foreach (var dir in dirs)
                    if (!string.IsNullOrEmpty(dir)) Note(e, System.IO.Path.Combine(dir, leaf));
            }
        }
        return (existing, verified, romDirs);
    }

    private static bool SafeExists(string p) { try { return System.IO.File.Exists(p); } catch { return false; } }

    private void RenderBios(StackPanel panel, string sysDir, HashSet<string> existing, HashSet<string> verified, Dictionary<string, string[]> romDirs)
    {
        bool Has(string path) => existing.Contains(path);

        // Info banner.
        var accent = Brush("AccentBrush");
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#18E03535")), BorderBrush = new SolidColorBrush(Color.Parse("#40E03535")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12),
        };
        var bs = new StackPanel();
        bs.Children.Add(new TextBlock { Text = "Where to place BIOS files", FontSize = 12, FontWeight = FontWeight.SemiBold, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 4) });
        bs.Children.Add(new TextBlock { Text = $"System folder (recommended):  {sysDir}", FontSize = 11, FontFamily = "monospace", Foreground = Brush("TextMutedBrush"), TextWrapping = TextWrapping.Wrap });
        bs.Children.Add(new TextBlock { Text = "Alternatively, place a BIOS file in the same folder as the ROMs for that system — it will be found automatically.", FontSize = 11, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        bs.Children.Add(new TextBlock { Text = "Or just drag and drop BIOS files anywhere on this panel — they're identified by hash/size and copied here automatically.", FontSize = 11, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        banner.Child = bs;
        panel.Children.Add(banner);

        var byDisplay = Services.KnownBios.All.GroupBy(b => b.ConsoleDisplay).ToDictionary(g => g.Key, g => g.ToList());

        bool FoundFor(Services.BiosEntry e)
        {
            if (Has(System.IO.Path.Combine(sysDir, e.Filename))) return true;
            return romDirs.TryGetValue(e.Console, out var dirs) && dirs.Any(d =>
                !string.IsNullOrEmpty(d) && Has(System.IO.Path.Combine(d, System.IO.Path.GetFileName(e.Filename))));
        }

        foreach (var (category, displays) in BiosCategories)
        {
            var active = displays.Where(byDisplay.ContainsKey).ToList();
            if (active.Count == 0) continue;
            int catFound = active.Sum(d => byDisplay[d].Count(FoundFor));
            int catTotal = active.Sum(d => byDisplay[d].Count);

            var body = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            var nest = MakeAccordionNest(body);
            var header = MakeAccordionHeader(category, $"{active.Count} {(active.Count == 1 ? "system" : "systems")}", catFound, catTotal, nest, 14, $"sysfiles/{category}");
            panel.Children.Add(header);
            panel.Children.Add(nest);

            // Two-level accordion (Category → Console → BIOS files), matching upstream:
            // each console display gets its own nested sub-accordion with its own
            // found/total badge, so multi-console categories like Sony render as
            // clean separate "PlayStation" / "PlayStation 2" entries instead of one
            // flat run of rows.
            foreach (var display in active)
            {
                var entries = byDisplay[display];
                int dFound = entries.Count(FoundFor);

                var dBody = new StackPanel();
                var dNest = MakeAccordionNest(dBody);
                var dHeader = MakeAccordionHeader(display, $"{entries.Count} {(entries.Count == 1 ? "file" : "files")}", dFound, entries.Count, dNest, 13, $"sysfiles/{category}/{display}");
                body.Children.Add(dHeader);
                body.Children.Add(dNest);

                foreach (var entry in entries)
                    dBody.Children.Add(BuildBiosRow(entry, sysDir, romDirs, verified, FoundFor(entry)));
            }
        }
    }

    // Expansion state survives panel rebuilds (downloads/preferred-core clicks repaint the whole
    // Cores panel — without this every accordion snapped shut and the view jumped, unlike the
    // Windows app whose WPF accordion updates rows in place).
    private readonly HashSet<string> _accordionExpanded = new();

    // A clickable accordion header (chevron + label + summary + found/total badge) that toggles
    // `body`. Pass a stateKey to remember the open/closed state across rebuilds.
    private Border MakeAccordionHeader(string label, string summary, int found, int total, Control body, double fontSize, string? stateKey = null)
    {
        bool expanded = stateKey != null && _accordionExpanded.Contains(stateKey);
        var chevron = new TextBlock { Text = expanded ? "▾" : "▸", FontSize = fontSize, Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var grid = new Grid { Cursor = new Cursor(StandardCursorType.Hand), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Grid.SetColumn(chevron, 0);
        var lbl = new TextBlock { Text = label, FontSize = fontSize, FontWeight = FontWeight.SemiBold, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 1);
        var sum = new TextBlock { Text = summary, FontSize = 11, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(sum, 2);
        var badge = MakeFoundBadge(found, total);
        Grid.SetColumn(badge, 3);
        grid.Children.Add(chevron); grid.Children.Add(lbl); grid.Children.Add(sum); grid.Children.Add(badge);
        var header = new Border { Background = new SolidColorBrush(Color.Parse("#1F1F21")), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 6, 0, 0), Child = grid };
        body.IsVisible = expanded;
        header.PointerPressed += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            chevron.Text = body.IsVisible ? "▾" : "▸";
            if (stateKey != null)
            {
                if (body.IsVisible) _accordionExpanded.Add(stateKey);
                else _accordionExpanded.Remove(stateKey);
            }
        };
        return header;
    }

    // Indented body wrapper for nested accordion levels: a subtle left guide rule + inset, so
    // expanded content reads as belonging to its header (the flat layout looked unnested).
    private Border MakeAccordionNest(Control body) => new()
    {
        IsVisible = false,
        Child = body,
        BorderBrush = Brush("BorderSubtleBrush"),
        BorderThickness = new Thickness(1, 0, 0, 0),
        Margin = new Thickness(10, 2, 0, 4),
        Padding = new Thickness(14, 0, 0, 0),
    };

    private Border MakeFoundBadge(int found, int total)
    {
        string fill = found == total && total > 0 ? "#2230D158" : found > 0 ? "#22FFA500" : "#22888888";
        string fg   = found == total && total > 0 ? "#30D158"   : found > 0 ? "#FFA500"   : "#888888";
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(fill)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = $"{found}/{total}", FontSize = 10, FontFamily = Font("PrimaryFont"), Foreground = new SolidColorBrush(Color.Parse(fg)) },
        };
    }

    private Control BuildBiosRow(Services.BiosEntry entry, string sysDir, Dictionary<string, string[]> romDirs, HashSet<string> verifiedSet, bool exists)
    {
        string sysPath = System.IO.Path.Combine(sysDir, entry.Filename);
        bool inSys = exists && SafeExists(sysPath);
        string? foundPath = inSys ? sysPath : null;
        if (foundPath == null && exists && romDirs.TryGetValue(entry.Console, out var dirs))
            foundPath = dirs.Select(d => System.IO.Path.Combine(d, System.IO.Path.GetFileName(entry.Filename))).FirstOrDefault(SafeExists);
        bool verified = foundPath != null && verifiedSet.Contains(foundPath);   // computed off-thread in BiosScan

        // 5 columns (upstream layout): status icon · filename · description · size · MD5 (click to reveal).
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("24,Auto,*,Auto,Auto"), Margin = new Thickness(0) };
        var icon = new TextBlock { Text = verified ? "✓" : "⚠", FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(verified ? "#30D158" : "#E03535")) };
        Grid.SetColumn(icon, 0);

        var filename = new TextBlock { Text = System.IO.Path.GetFileName(entry.Filename), FontSize = 13, FontFamily = "monospace",
            MinWidth = 200, Foreground = Brush("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 16, 0) };
        Grid.SetColumn(filename, 1);

        string descText = entry.Description;
        if (!exists) descText += (descText.Length > 0 ? " — " : "") + "Missing";
        else if (entry.Md5 != null && !verified) descText += (descText.Length > 0 ? " — " : "") + "Hash mismatch";
        else if (!inSys) descText += (descText.Length > 0 ? " — " : "") + "found in game folder";
        var desc = new TextBlock { Text = descText, FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(desc, 2);

        if (entry.ExpectedSize > 0)
        {
            var size = new TextBlock { Text = $"{entry.ExpectedSize / 1024} KB", FontSize = 11, FontFamily = Font("PrimaryFont"),
                Foreground = Brush("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(size, 3);
            grid.Children.Add(size);
        }
        if (entry.Md5 != null)
        {
            bool revealed = false;
            var md5 = new TextBlock { Text = $"MD5: {entry.Md5[..8]}…", FontSize = 11, FontFamily = "monospace", Cursor = new Cursor(StandardCursorType.Hand),
                Foreground = Brush("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            ToolTip.SetTip(md5, "Click to reveal the full MD5");
            md5.PointerPressed += (_, _) => { revealed = !revealed; md5.Text = revealed ? $"MD5: {entry.Md5}" : $"MD5: {entry.Md5[..8]}…"; };
            Grid.SetColumn(md5, 4);
            grid.Children.Add(md5);
        }
        grid.Children.Add(icon); grid.Children.Add(filename); grid.Children.Add(desc);

        return new Border { Background = new SolidColorBrush(Color.Parse("#1F1F21")), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(12, 0, 0, 4), Child = grid };
    }

    private static bool VerifyMd5(string path, string expectedMd5)
    {
        try { return string.Equals(ComputeMd5(path), expectedMd5, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string? ComputeMd5(string path)
    {
        try
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var fs = System.IO.File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    // ── Drag-drop BIOS importer ──
    private async Task OnBiosDropAsync(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return;
        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count == 0) return;

        string sysDir = AppPaths.GetFolder("System");
        var messages = new List<string>();
        var result = await Task.Run(() =>
        {
            int imported = 0, skipped = 0;
            foreach (var src in paths)
            {
                if (!System.IO.File.Exists(src)) { skipped++; continue; }
                ProcessDroppedFile(src, sysDir, messages, ref imported, ref skipped);
            }
            return (imported, skipped);
        });

        if (result.imported > 0) BuildBiosPanel(); // rescan + repaint
        string summary = result.imported > 0
            ? $"Imported {result.imported} file{(result.imported == 1 ? "" : "s")}" + (result.skipped > 0 ? $" ({result.skipped} skipped)" : "")
            : "No BIOS files recognized";
        await new ConfirmDialog("BIOS drop", summary + (messages.Count > 0 ? "\n\n" + string.Join("\n", messages) : ""), "OK", infoOnly: true).ShowDialog<bool>(this);
    }

    private static void ProcessDroppedFile(string src, string sysDir, List<string> messages, ref int imported, ref int skipped)
    {
        long size;
        try { size = new System.IO.FileInfo(src).Length; } catch { skipped++; return; }
        string srcName = System.IO.Path.GetFileName(src);
        string srcExt = System.IO.Path.GetExtension(srcName);
        bool isArchive = srcExt.Equals(".zip", StringComparison.OrdinalIgnoreCase) || srcExt.Equals(".7z", StringComparison.OrdinalIgnoreCase) || srcExt.Equals(".rar", StringComparison.OrdinalIgnoreCase);
        bool anyHashed = Services.KnownBios.All.Any(b => b.Md5 != null);

        if (isArchive)
        {
            int extractedHere = 0;
            try
            {
                using var archive = Services.Archives.RomArchive.Open(src);
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
                    string entryName = System.IO.Path.GetFileName(entry.Key);
                    if (string.IsNullOrEmpty(entryName)) continue;
                    string? entryMd5 = null;
                    if (anyHashed)
                    {
                        try { using var ms = entry.OpenEntryStream(); using var md5 = System.Security.Cryptography.MD5.Create(); entryMd5 = Convert.ToHexString(md5.ComputeHash(ms)).ToLowerInvariant(); } catch { }
                    }
                    var match = MatchKnownBios(entryName, entry.Size, entryMd5);
                    if (match == null) continue;
                    try { using var es = entry.OpenEntryStream(); CopyEntryToSystem(match, es, sysDir); messages.Add($"✓ {srcName} → {System.IO.Path.GetFileName(match.Filename)} ({match.ConsoleDisplay})"); imported++; extractedHere++; }
                    catch (Exception ex) { messages.Add($"⚠ {srcName}:{entryName}: {ex.Message}"); skipped++; }
                }
            }
            catch (Exception ex) { messages.Add($"⚠ {srcName}: archive open failed ({ex.Message})"); }
            if (extractedHere > 0) return; // else fall through (archive IS the BIOS, e.g. neogeo.zip)
        }

        string? fileMd5 = anyHashed ? ComputeMd5(src) : null;
        var fileMatch = MatchKnownBios(srcName, size, fileMd5);
        string destPath; string label;
        if (fileMatch != null) { destPath = System.IO.Path.Combine(sysDir, fileMatch.Filename); label = $"{System.IO.Path.GetFileName(fileMatch.Filename)} → {fileMatch.ConsoleDisplay}"; }
        else { messages.Add($"• {srcName}: not a recognized BIOS"); skipped++; return; }
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath)!);
            System.IO.File.Copy(src, destPath, overwrite: true);
            messages.Add($"✓ {label}"); imported++;
        }
        catch (Exception ex) { messages.Add($"⚠ {srcName}: {ex.Message}"); skipped++; }
    }

    private static Services.BiosEntry? MatchKnownBios(string entryName, long size, string? md5)
    {
        if (md5 != null)
        {
            var hashMatch = Services.KnownBios.All.FirstOrDefault(b => b.Md5 != null && string.Equals(b.Md5, md5, StringComparison.OrdinalIgnoreCase));
            if (hashMatch != null) return hashMatch;
        }
        var sizeMatch = Services.KnownBios.All.FirstOrDefault(b =>
            string.Equals(System.IO.Path.GetFileName(b.Filename), entryName, StringComparison.OrdinalIgnoreCase) && (b.ExpectedSize == 0 || b.ExpectedSize == size));
        if (sizeMatch != null) return sizeMatch;
        return Services.KnownBios.All.FirstOrDefault(b => string.Equals(System.IO.Path.GetFileName(b.Filename), entryName, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyEntryToSystem(Services.BiosEntry match, System.IO.Stream source, string sysDir)
    {
        string destPath = System.IO.Path.Combine(sysDir, match.Filename);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath)!);
        using var dest = System.IO.File.Create(destPath);
        source.CopyTo(dest);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  U5 P5 — Cores panel (download accordion). Revert/backup + per-console
    //  CoreSpecificOptions overrides are a noted follow-up (P5b).
    // ════════════════════════════════════════════════════════════════════════

    private static readonly (string Category, string[] Consoles)[] ConsoleCategories =
    {
        ("Nintendo", new[] { "NES", "FDS", "SNES", "N64", "GameCube", "GB", "GBC", "GBA", "NDS", "3DS", "VirtualBoy" }),
        ("Sega",     new[] { "Genesis", "SegaCD", "Sega32X", "Saturn", "SMS", "GameGear", "SG1000", "Dreamcast" }),
        ("Sony",     new[] { "PS1", "PS2", "PSP" }),
        ("NEC",      new[] { "TG16", "TGCD" }),
        ("Atari",    new[] { "Atari2600", "Atari7800", "Jaguar" }),
        ("Arcade",   new[] { "Arcade", "NeoGeo", "NeoCD" }),
        ("Other",    new[] { "NGP", "ColecoVision", "Vectrex", "3DO", "CDi" }),
    };

    private readonly Services.CoreDownloadService _coreDownloader = new();
    private readonly Dictionary<string, TextBlock> _coreUpdatePills = new();
    private bool _coresBuilding;

    private async void BuildCoresPanel()
    {
        // Guard against overlapping rebuilds (a download completing while the user clicks a
        // preferred ●/○ both call this within the off-thread scan window → doubled panel).
        if (_coresBuilding) return;
        _coresBuilding = true;
        var panel = this.FindControl<StackPanel>("CoresListPanel")!;
        // Rebuilds happen mid-interaction (a download finishing, a preferred-core click) — keep
        // the user's place: scroll offset captured here, restored after the repaint lays out.
        var coresScroller = panel.FindAncestorOfType<ScrollViewer>();
        var savedScroll = coresScroller?.Offset ?? default;
        panel.Children.Clear();
        _coreUpdatePills.Clear();
        panel.Children.Add(new TextBlock { Text = "Scanning…", FontFamily = Font("PrimaryFont"), FontSize = 12, Foreground = Brush("TextMutedBrush"), Margin = new Thickness(2, 8, 0, 0) });

        string coresFolder = AppPaths.GetCoresFolder();
        var installed = await Task.Run(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var f in System.IO.Directory.EnumerateFiles(coresFolder, "*.so")) set.Add(System.IO.Path.GetFileName(f)); } catch { }
            return set;
        });
        if (!this.FindControl<Grid>("PanelCores")!.IsVisible) { _coresBuilding = false; return; }
        bool IsInstalled(string dll) => installed.Contains(dll);
        panel.Children.Clear();

        // Download All Recommended + Update All row.
        var recommended = Services.CoreDownloadService.Catalog.Where(c => c.Recommended).ToList();
        int recInstalled = recommended.Count(c => IsInstalled(c.FileName));
        var dlAllBtn = new Button { Content = "Download All Recommended", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefActionBtn", out var t1) ? t1 : null), VerticalAlignment = VerticalAlignment.Center };
        var dlAllSummary = new TextBlock { Text = $"{recInstalled} of {recommended.Count} installed", FontSize = 11, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var updateAllBtn = new Button { Content = "Update All", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefActionBtn", out var t2) ? t2 : null), IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetColumn(dlAllBtn, 0); Grid.SetColumn(dlAllSummary, 1); Grid.SetColumn(updateAllBtn, 2);
        topRow.Children.Add(dlAllBtn); topRow.Children.Add(dlAllSummary); topRow.Children.Add(updateAllBtn);
        panel.Children.Add(topRow);

        // Overall download progress (matches upstream allProgressBar): a thin bar that fills across
        // the whole batch — value = (completed*100 + currentCore%) / total.
        var allProgressBar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0,
            IsVisible = false, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(allProgressBar);

        dlAllBtn.Click += async (_, _) =>
        {
            var todo = recommended.Where(c => !IsInstalled(c.FileName)).ToList();
            if (todo.Count == 0) { dlAllSummary.Text = "All recommended cores already installed."; return; }

            dlAllBtn.IsEnabled = false; dlAllBtn.Content = "Downloading…";
            allProgressBar.IsVisible = true; allProgressBar.Value = 0;
            int done = 0, failed = 0;
            foreach (var entry in todo)
            {
                int n = done + 1, completed = done;
                dlAllSummary.Text = $"Downloading {entry.DisplayName}… ({n}/{todo.Count})";
                var prog = new Progress<int>(p =>
                {
                    dlAllSummary.Text = $"Downloading {entry.DisplayName}… {p}%  ({n}/{todo.Count})";
                    allProgressBar.Value = (completed * 100 + p) / (double)todo.Count;
                });
                try { await _coreDownloader.DownloadAsync(entry, coresFolder, prog); }
                catch (Exception ex) { failed++; System.Diagnostics.Trace.WriteLine($"[Cores] {entry.FileName} failed: {ex.Message}"); }
                done++;
                allProgressBar.Value = done * 100 / (double)todo.Count;
            }
            dlAllBtn.IsEnabled = true; dlAllBtn.Content = "Download All Recommended";
            allProgressBar.Value = 100; allProgressBar.IsVisible = false;
            dlAllSummary.Text = failed == 0 ? $"Downloaded {done} core(s)." : $"Done — {done - failed} ok, {failed} failed (see Logs/cores.log).";
            BuildCoresPanel();
        };

        // Category → Console → Core accordion.
        var prefs = App.Configuration?.GetCorePreferences() ?? new Configuration.CorePreferences();
        foreach (var (category, consoles) in ConsoleCategories)
        {
            var catConsoles = consoles.Where(c => Services.CoreManager.ConsoleCoreMap.ContainsKey(c)).ToList();
            if (catConsoles.Count == 0) continue;
            int catInstalled = 0, catTotal = 0;
            foreach (var c in catConsoles) { var cs = Services.CoreManager.ConsoleCoreMap[c]; catTotal += cs.Length; catInstalled += cs.Count(IsInstalled); }

            var catBody = new StackPanel();
            var catNest = MakeAccordionNest(catBody);
            panel.Children.Add(MakeAccordionHeader(category, $"{catConsoles.Count} {(catConsoles.Count == 1 ? "system" : "systems")}", catInstalled, catTotal, catNest, 14, $"cores/{category}"));
            panel.Children.Add(catNest);

            foreach (var consoleName in catConsoles)
            {
                var candidates = Services.CoreManager.ConsoleCoreMap[consoleName];
                int conInstalled = candidates.Count(IsInstalled);
                string? savedPref = prefs.PreferredCores.TryGetValue(consoleName, out var pp) ? pp : null;
                string activeDll = candidates.FirstOrDefault(d => d == savedPref && IsInstalled(d)) ?? candidates.FirstOrDefault(IsInstalled) ?? "";

                var conBody = new StackPanel();
                var conNest = MakeAccordionNest(conBody);
                catBody.Children.Add(MakeAccordionHeader(consoleName, IsInstalled(activeDll) ? FormatCoreName(activeDll) : "Not installed", conInstalled, candidates.Length, conNest, 13, $"cores/{category}/{consoleName}"));
                catBody.Children.Add(conNest);

                foreach (var dll in candidates)
                    conBody.Children.Add(BuildCoreRow(dll, consoleName, coresFolder, IsInstalled(dll), dll == activeDll && IsInstalled(dll), candidates.Count(IsInstalled) > 1));
            }
        }

        // Reveal "update available" pills + Update All once the staleness check returns.
        _ = DecorateCoreUpdatesAsync(coresFolder, updateAllBtn, allProgressBar, dlAllSummary);

        AppendExtrasSection(panel);
        // Restore the pre-rebuild scroll position once the new content has been laid out.
        if (coresScroller != null && savedScroll != default)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => coresScroller.Offset = savedScroll,
                Avalonia.Threading.DispatcherPriority.Loaded);
        _coresBuilding = false;
    }

    // ── P6: Extras — DAT downloads (the Linux-relevant part of upstream's Extras).
    //  Native libs (SDL3/ffmpeg) are system packages on Linux → dropped. Shader pack +
    //  Vectrex overlays defer to the shader/overlay splinter (noted in the UI).
    private static readonly (string Tag, string Label, string? RedumpSlug, string? DirectUrl)[] KnownDats =
    {
        ("Arcade",       "Arcade (FBNeo)",          null, "https://raw.githubusercontent.com/libretro/FBNeo/master/dats/FinalBurn%20Neo%20(ClrMame%20Pro%20XML%2C%20Arcade%20only).dat"),
        ("mame2003plus", "Arcade (MAME 2003-Plus)", null, "https://raw.githubusercontent.com/libretro/mame2003-plus-libretro/master/metadata/mame2003-plus.xml"),
        ("NeoGeo",       "Neo Geo (Geolith)",       null, "https://raw.githubusercontent.com/libretro/libretro-database/master/dat/SNK%20-%20Neo%20Geo.dat"),
        ("NeoCD",        "Neo Geo CD",              null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/redump/SNK%20-%20Neo%20Geo%20CD.dat"),
        ("SegaCD", "Sega CD / Mega CD", "mcd", null),
        ("Saturn", "Sega Saturn",       "ss",  null),
        ("PS1",    "PlayStation",        "psx", null),
        ("PS2",    "PlayStation 2",      "ps2", null),
        ("TGCD",   "TurboGrafx-CD",     "pce", null),
        ("3DO",    "3DO",                "3do", null),
        ("CDi",    "Philips CD-i",       "cdi", null),
        ("NGP",    "Neo Geo Pocket",       null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/SNK%20-%20Neo%20Geo%20Pocket.dat"),
        ("NGPC",   "Neo Geo Pocket Color", null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/SNK%20-%20Neo%20Geo%20Pocket%20Color.dat"),
    };

    private void AppendExtrasSection(StackPanel panel)
    {
        string datsDir = AppPaths.GetDatsFolder();
        try { System.IO.Directory.CreateDirectory(datsDir); } catch { }

        // ── Cheats database (libretro cheats.zip) ──
        panel.Children.Add(new TextBlock { Text = "CHEATS DATABASE", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefLabel", out var tc) ? tc : null), Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(new Border { Height = 1, Background = Brush("BorderNormalBrush"), Margin = new Thickness(0, 0, 0, 8) });
        {
            bool installed = Services.CheatDatabaseService.IsInstalled();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 16) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = "libretro community cheats", FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush") });
            var status = new TextBlock { FontSize = 10, FontFamily = Font("PrimaryFont"),
                Text = installed ? $"Installed — {Services.CheatDatabaseService.TotalFileCount()} files, {Services.CheatDatabaseService.InstalledSystemCount()} systems" : "Not installed (~37 MB download)",
                Foreground = new SolidColorBrush(Color.Parse(installed ? "#30D158" : "#888888")) };
            info.Children.Add(status);
            Grid.SetColumn(info, 0);
            var bar = new ProgressBar { Height = 3, Width = 80, Minimum = 0, Maximum = 100, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(bar, 1);
            var btn = new Button { Content = installed ? "Update" : "Download", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var tb2) ? tb2 : null), Padding = new Thickness(10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btn, 2);
            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false; bar.IsVisible = true;
                try
                {
                    int n = await Services.CheatDatabaseService.DownloadAndExtractAsync((pct, msg) =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => { bar.Value = pct; status.Text = msg; }));
                    status.Text = $"Installed — {n} files, {Services.CheatDatabaseService.InstalledSystemCount()} systems";
                    status.Foreground = new SolidColorBrush(Color.Parse("#30D158"));
                    btn.Content = "Update";
                }
                catch (Exception ex) { status.Text = $"Failed: {ex.Message}"; status.Foreground = Brush("AccentBrush"); }
                finally { bar.IsVisible = false; btn.IsEnabled = true; }
            };
            row.Children.Add(info); row.Children.Add(bar); row.Children.Add(btn);
            panel.Children.Add(row);
        }

        // ── Video shaders (upstream's "Video Shaders (libretro)" row). Windows uses the slang pack
        // through librashader (D3D11, no Linux binaries) — Linux downloads the GLSL pack instead:
        // the same shader library in .glslp form, run by our GL chain in the game host. ──
        panel.Children.Add(new TextBlock { Text = "VIDEO SHADERS", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefLabel", out var tvs) ? tvs : null), Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(new Border { Height = 1, Background = Brush("BorderNormalBrush"), Margin = new Thickness(0, 0, 0, 8) });
        {
            int shCount = Services.ShaderCatalog.IsInstalled() ? Services.ShaderCatalog.GetDownloaded().Count : 0;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 16) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = "Video Shaders (libretro)", FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush") });
            var status = new TextBlock { FontSize = 10, FontFamily = Font("PrimaryFont"),
                Text = shCount > 0 ? $"{shCount} presets installed — pick in-game via the cog → Shader"
                                   : "Community shader pack: CRT, LCD, NTSC, scalers and more. The built-in shaders stay available without this download.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse(shCount > 0 ? "#30D158" : "#888888")) };
            info.Children.Add(status);
            Grid.SetColumn(info, 0);
            var bar = new ProgressBar { Height = 3, Width = 80, Minimum = 0, Maximum = 100, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(bar, 1);
            var btn = new Button { Content = shCount > 0 ? "Re-download" : "Download", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var tvb) ? tvb : null), Padding = new Thickness(10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btn, 2);
            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false; bar.IsVisible = true; bar.Value = 0;
                status.Text = "Downloading shader pack…"; status.Foreground = Brush("TextMutedBrush");
                try
                {
                    string root = Services.ShaderCatalog.GlslRoot;
                    string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shaders_glsl-{Guid.NewGuid():N}.zip");
                    await Task.Run(async () =>
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                        http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
                        using var resp = await http.GetAsync("https://buildbot.libretro.com/assets/frontend/shaders_glsl.zip",
                            System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? 0;
                        await using (var src = await resp.Content.ReadAsStreamAsync())
                        await using (var dst = System.IO.File.Create(tmp))
                        {
                            var chunk = new byte[1 << 16]; long got = 0; int n;
                            while ((n = await src.ReadAsync(chunk)) > 0)
                            {
                                await dst.WriteAsync(chunk.AsMemory(0, n)); got += n;
                                if (total > 0)
                                {
                                    long pct = got * 70 / total;
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() => bar.Value = pct);
                                }
                            }
                        }
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => { bar.Value = 75; status.Text = "Extracting…"; });
                        // Extract, stripping a single top-level "shaders_glsl/" component if the
                        // zip carries one, so presets land directly under Shaders/glsl/.
                        using (var zip = System.IO.Compression.ZipFile.OpenRead(tmp))
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry
                                string rel = entry.FullName.Replace('\\', '/');
                                if (rel.StartsWith("shaders_glsl/", StringComparison.OrdinalIgnoreCase))
                                    rel = rel.Substring("shaders_glsl/".Length);
                                if (rel.Length == 0 || rel.Contains("..")) continue;
                                string dest = System.IO.Path.Combine(root, rel);
                                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                                using var es = entry.Open();
                                using var os = System.IO.File.Create(dest);
                                es.CopyTo(os);
                            }
                        }
                        System.IO.File.WriteAllText(System.IO.Path.Combine(root, ".installed"),
                            DateTime.UtcNow.ToString("o"));
                        try { System.IO.File.Delete(tmp); } catch { }
                    });
                    int n2 = Services.ShaderCatalog.GetDownloaded().Count;
                    bar.Value = 100;
                    status.Text = $"{n2} presets installed — pick in-game via the cog → Shader";
                    status.Foreground = new SolidColorBrush(Color.Parse("#30D158"));
                    btn.Content = "Re-download";
                }
                catch (Exception ex) { status.Text = $"Failed: {ex.Message}"; status.Foreground = Brush("AccentBrush"); }
                finally { bar.IsVisible = false; btn.IsEnabled = true; }
            };
            row.Children.Add(info); row.Children.Add(bar); row.Children.Add(btn);
            panel.Children.Add(row);
        }

        // ── Overlays & bezels (upstream's Bezels row; Vectrex overlays are folder-driven) ──
        panel.Children.Add(new TextBlock { Text = "OVERLAYS & BEZELS", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefLabel", out var tob) ? tob : null), Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(new Border { Height = 1, Background = Brush("BorderNormalBrush"), Margin = new Thickness(0, 0, 0, 8) });
        {
            // Arcade / Neo Geo bezels (The Bezel Project). "Enable" turns on per-game on-demand
            // fetch (each ~1-4 MB bezel downloads at first launch + caches); "Download all"
            // pre-fetches the whole ~1.5 GB set for offline use. Toggled per game from the in-game cog.
            bool bezelsOn = Services.BezelService.FeatureEnabled;
            int cached = Services.BezelService.CachedCount();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = "Arcade / Neo Geo bezels (The Bezel Project)", FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush") });
            var status = new TextBlock { FontSize = 10, FontFamily = Font("PrimaryFont"),
                Text = bezelsOn ? (cached > 0 ? $"On — {cached} bezels cached" : "On — fetched per game on first launch") : "Off",
                Foreground = new SolidColorBrush(Color.Parse(bezelsOn ? "#30D158" : "#888888")) };
            info.Children.Add(status);
            Grid.SetColumn(info, 0);
            var bar = new ProgressBar { Height = 3, Width = 80, Minimum = 0, Maximum = 100, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(bar, 1);
            var dlBtn = new Button { Content = "Download all", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var tb3) ? tb3 : null), Padding = new Thickness(10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(dlBtn, 2);
            var toggleBtn = new Button { Content = bezelsOn ? "Disable" : "Enable", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var tb4) ? tb4 : null), Padding = new Thickness(10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(toggleBtn, 3);
            toggleBtn.Click += (_, _) =>
            {
                bool now = !Services.BezelService.FeatureEnabled;
                Services.BezelService.FeatureEnabled = now;
                toggleBtn.Content = now ? "Disable" : "Enable";
                int c = Services.BezelService.CachedCount();
                status.Text = now ? (c > 0 ? $"On — {c} bezels cached" : "On — fetched per game on first launch") : "Off";
                status.Foreground = new SolidColorBrush(Color.Parse(now ? "#30D158" : "#888888"));
            };
            dlBtn.Click += async (_, _) =>
            {
                dlBtn.IsEnabled = false; toggleBtn.IsEnabled = false; bar.IsVisible = true; bar.Value = 0;
                status.Text = "Listing bezels…"; status.Foreground = Brush("TextMutedBrush");
                try
                {
                    var progress = new Progress<(int done, int total)>(p =>
                    {
                        if (p.total > 0) { bar.Value = (double)p.done * 100 / p.total; status.Text = $"Downloading bezels… {p.done}/{p.total}"; }
                    });
                    var (_, total) = await Services.BezelService.DownloadAllAsync(progress);
                    status.Text = total > 0 ? $"On — {Services.BezelService.CachedCount()} bezels cached" : "No bezels found.";
                    status.Foreground = new SolidColorBrush(Color.Parse("#30D158"));
                    toggleBtn.Content = "Disable";   // downloading the set implies the feature is on
                }
                catch (Exception ex) { status.Text = $"Failed: {ex.Message}"; status.Foreground = Brush("AccentBrush"); }
                finally { bar.IsVisible = false; dlBtn.IsEnabled = true; toggleBtn.IsEnabled = true; }
            };
            row.Children.Add(info); row.Children.Add(bar); row.Children.Add(dlBtn); row.Children.Add(toggleBtn);
            panel.Children.Add(row);

            // Vectrex overlays (upstream's "Vectrex Overlays" Extras row): ~38 game-specific screen
            // overlays from libretro/overlay-borders, enabled by default when present. Custom PNGs
            // can also be dropped into the folder (matched to ROMs by filename).
            int ovCount = 0;
            try { if (System.IO.Directory.Exists(Services.VectrexOverlayService.OverlayDir)) ovCount = System.IO.Directory.GetFiles(Services.VectrexOverlayService.OverlayDir, "*.png").Length; } catch { }
            bool ovPresent = ovCount >= 30;   // expect ~38, like upstream
            var vrow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 16) };
            var vinfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            vinfo.Children.Add(new TextBlock { Text = "Vectrex overlays", FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush") });
            var vstatus = new TextBlock { FontSize = 10, FontFamily = Font("PrimaryFont"),
                Text = ovCount > 0 ? $"{ovCount} overlays installed — enabled by default, matched to ROMs by filename" : "Game-specific screen overlays — enabled by default when present",
                Foreground = new SolidColorBrush(Color.Parse(ovCount > 0 ? "#30D158" : "#888888")) };
            vinfo.Children.Add(vstatus);
            Grid.SetColumn(vinfo, 0);
            var vbar = new ProgressBar { Height = 3, Width = 80, Minimum = 0, Maximum = 100, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(vbar, 1);
            var vdlBtn = new Button { Content = ovPresent ? "Re-download" : "Download", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var tb6) ? tb6 : null), Padding = new Thickness(10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(vdlBtn, 2);
            var openBtn = new Button { Content = "Open folder", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var tb5) ? tb5 : null), Padding = new Thickness(10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(openBtn, 3);
            openBtn.Click += (_, _) => Services.ShellOpen.Open(Services.VectrexOverlayService.OverlayDir);
            vdlBtn.Click += async (_, _) =>
            {
                vdlBtn.IsEnabled = false; vbar.IsVisible = true; vbar.Value = 0;
                vstatus.Text = "Fetching overlay list…"; vstatus.Foreground = Brush("TextMutedBrush");
                try
                {
                    var progress = new Progress<(int done, int total)>(p =>
                    {
                        if (p.total > 0) { vbar.Value = (double)p.done * 100 / p.total; vstatus.Text = $"Downloading overlays… {p.done}/{p.total}"; }
                    });
                    var (done, _) = await Services.VectrexOverlayService.DownloadAllAsync(progress);
                    vstatus.Text = $"Downloaded {done} overlays successfully.";
                    vstatus.Foreground = new SolidColorBrush(Color.Parse("#30D158"));
                    vdlBtn.Content = "Re-download";
                }
                catch (Exception ex) { vstatus.Text = $"Failed: {ex.Message}"; vstatus.Foreground = Brush("AccentBrush"); }
                finally { vbar.IsVisible = false; vdlBtn.IsEnabled = true; }
            };
            vrow.Children.Add(vinfo); vrow.Children.Add(vbar); vrow.Children.Add(vdlBtn); vrow.Children.Add(openBtn);
            panel.Children.Add(vrow);
        }

        panel.Children.Add(new TextBlock { Text = "DAT FILES (ROM IDENTIFICATION)", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefLabel", out var t) ? t : null), Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(new Border { Height = 1, Background = Brush("BorderNormalBrush"), Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Reference DAT files improve ROM identification for disc/arcade systems. Native libraries (SDL3, ffmpeg) are provided by your system packages on Linux; shader packs arrive with the shader splinter.",
            FontSize = 11, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        // Shared download routine — used by each row's button AND the "Download
        // All" button. Each call updates its own row's bar/status so a batch run
        // shows per-system progress.
        async Task DownloadDatAsync(string? slug, string? directUrl, string datPath,
            ProgressBar bar, TextBlock status, Button btn)
        {
            btn.IsEnabled = false; bar.IsVisible = true; bar.Value = 10; status.Text = "Downloading…"; status.Foreground = Brush("TextMutedBrush");
            try
            {
                string url = directUrl ?? $"http://redump.org/datfile/{slug}/";
                byte[] bytes = await Task.Run(async () =>
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
                    return await http.GetByteArrayAsync(url);
                });
                bar.Value = 90;

                // redump.org serves DATs as a .zip (Content-Type: application/zip)
                // containing a single .dat. The DAT matcher parses the saved file
                // as raw XML, so unwrap the archive before writing — otherwise the
                // saved "{tag}.dat" is a zip the matcher silently fails to load.
                // Direct GitHub URLs (Arcade/NeoGeo/NGP/…) are already raw .dat/.xml.
                if (bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K' &&
                    bytes[2] == 0x03 && bytes[3] == 0x04)
                {
                    using var zipMs   = new System.IO.MemoryStream(bytes);
                    using var archive = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Read);
                    var entry = archive.Entries.FirstOrDefault(e =>
                                    e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                                    e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                                ?? archive.Entries.FirstOrDefault();
                    if (entry == null) throw new Exception("Downloaded archive was empty.");
                    using var entryStream = entry.Open();
                    using var outMs       = new System.IO.MemoryStream();
                    await entryStream.CopyToAsync(outMs);
                    bytes = outMs.ToArray();
                }

                await System.IO.File.WriteAllBytesAsync(datPath, bytes);
                bar.Value = 100; bar.IsVisible = false;
                status.Text = "Present"; status.Foreground = new SolidColorBrush(Color.Parse("#30D158"));
                btn.Content = "Re-download";
            }
            catch (Exception ex) { bar.IsVisible = false; status.Text = $"Failed: {ex.Message}"; status.Foreground = Brush("AccentBrush"); }
            finally { btn.IsEnabled = true; }
        }

        // Per-row download actions, collected for the "Download All" button.
        var datDownloadActions = new List<Func<Task>>();

        // ── Download All row ──
        var downloadAllBtn = new Button { Content = "Download All", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var tAll) ? tAll : null), Padding = new Thickness(10, 4), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        downloadAllBtn.Click += async (_, _) =>
        {
            downloadAllBtn.IsEnabled = false;
            int n = datDownloadActions.Count;
            for (int k = 0; k < n; k++)
            {
                downloadAllBtn.Content = $"Downloading… ({k + 1}/{n})";
                // Sequential — gentle on redump.org and lets each row report its
                // own progress as it goes.
                await datDownloadActions[k]();
            }
            downloadAllBtn.Content = "Download All"; downloadAllBtn.IsEnabled = true;
        };
        panel.Children.Add(downloadAllBtn);

        foreach (var (tag, label, slug, directUrl) in KnownDats)
        {
            string datPath = System.IO.Path.Combine(datsDir, $"{tag}.dat");
            bool present = System.IO.File.Exists(datPath);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = label, FontSize = 12, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextPrimaryBrush") });
            var status = new TextBlock { Text = present ? "Present" : "", FontSize = 10, FontFamily = Font("PrimaryFont"),
                Foreground = new SolidColorBrush(Color.Parse(present ? "#30D158" : "#888888")) };
            info.Children.Add(status);
            Grid.SetColumn(info, 0);

            var bar = new ProgressBar { Height = 3, Width = 60, Minimum = 0, Maximum = 100, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(bar, 1);
            var btn = new Button { Content = present ? "Re-download" : "Download", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var t2) ? t2 : null), Padding = new Thickness(10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btn, 2);

            // slug/directUrl/datPath/bar/status/btn are per-iteration locals, so
            // both the row button and the batch action capture this row's own set.
            btn.Click += async (_, _) => await DownloadDatAsync(slug, directUrl, datPath, bar, status, btn);
            datDownloadActions.Add(() => DownloadDatAsync(slug, directUrl, datPath, bar, status, btn));

            row.Children.Add(info); row.Children.Add(bar); row.Children.Add(btn);
            panel.Children.Add(row);
        }
    }

    private Control BuildCoreRow(string dll, string consoleName, string coresFolder, bool installed, bool preferred, bool canChoosePreferred)
    {
        var entry = Services.CoreDownloadService.Catalog.FirstOrDefault(c => c.FileName.Equals(dll, StringComparison.OrdinalIgnoreCase));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 4, 0, 0) };

        // Preferred indicator (● preferred / ○ other installed); click to set when >1 installed.
        var pref = new TextBlock { Text = installed ? (preferred ? "●" : "○") : " ", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Foreground = preferred ? new SolidColorBrush(Color.Parse("#30D158")) : Brush("TextMutedBrush"),
            Cursor = new Cursor(installed && canChoosePreferred ? StandardCursorType.Hand : StandardCursorType.Arrow) };
        if (installed && canChoosePreferred)
        {
            ToolTip.SetTip(pref, "Set as preferred core");
            pref.PointerPressed += (_, _) =>
            {
                var cp = App.Configuration!.GetCorePreferences();
                cp.PreferredCores[consoleName] = dll;
                App.Configuration!.SetCorePreferences(cp); App.Configuration!.ScheduleSave();
                BuildCoresPanel();
            };
        }
        Grid.SetColumn(pref, 0);

        var name = new TextBlock { Text = FormatCoreName(dll), FontSize = 12, FontFamily = Font("PrimaryFont"),
            FontWeight = installed ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Brush(installed ? "TextPrimaryBrush" : "TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, dll);
        Grid.SetColumn(name, 1);

        // Right inset pulls the ⟳/↓ buttons clear of the overlay scrollbar, which floats over the
        // content's right edge — flush buttons were getting mis-clicked as scrollbar drags.
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 22, 0) };
        if (entry != null)
        {
            var pill = new TextBlock { Text = "Update available", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
            if (installed) _coreUpdatePills[dll] = pill;
            var bar = new ProgressBar { Height = 3, Width = 60, Minimum = 0, Maximum = 100, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            var statusTb = new TextBlock { FontSize = 10, Foreground = Brush("TextMutedBrush"), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            var dlBtn = new Button { Content = installed ? "⟳" : "↓", Theme = (Avalonia.Styling.ControlTheme?)(this.TryFindResource("PrefSecondaryBtn", out var t) ? t : null), Width = 30, Height = 26, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(dlBtn, installed ? "Re-download" : "Download");
            dlBtn.Click += async (_, _) => await DownloadCoreAsync(entry, coresFolder, bar, statusTb, dlBtn);
            btnPanel.Children.Add(pill); btnPanel.Children.Add(bar); btnPanel.Children.Add(statusTb); btnPanel.Children.Add(dlBtn);
        }
        else
        {
            btnPanel.Children.Add(new TextBlock { Text = installed ? "installed" : "—", FontSize = 10, FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        }
        Grid.SetColumn(btnPanel, 2);

        row.Children.Add(pref); row.Children.Add(name); row.Children.Add(btnPanel);
        return row;
    }

    private async Task DownloadCoreAsync(Services.CoreEntry entry, string coresFolder, ProgressBar bar, TextBlock statusTb, Button dlBtn)
    {
        dlBtn.IsEnabled = false; bar.IsVisible = true; statusTb.IsVisible = true; statusTb.Text = "…"; bar.Value = 0;
        try
        {
            await _coreDownloader.DownloadAsync(entry, coresFolder, new Progress<int>(v => bar.Value = v));
            statusTb.Text = "Done"; bar.IsVisible = false;
            BuildCoresPanel(); // rescan + repaint (re-download / install reflected)
        }
        catch (Exception ex)
        {
            statusTb.Text = $"Error: {ex.Message}"; dlBtn.IsEnabled = true;
            System.Diagnostics.Trace.WriteLine($"[CoreDownload] FAILED {entry.FileName}: {ex.Message}");
        }
    }

    private async Task DecorateCoreUpdatesAsync(string coresFolder, Button updateAllBtn, ProgressBar allProgressBar, TextBlock summary)
    {
        try
        {
            var updates = await _coreDownloader.CheckAllForUpdatesAsync(coresFolder);
            if (!this.FindControl<Grid>("PanelCores")!.IsVisible || updates.Count == 0) return;
            foreach (var e in updates)
                if (_coreUpdatePills.TryGetValue(e.FileName, out var pill)) pill.IsVisible = true;
            updateAllBtn.IsVisible = true;
            updateAllBtn.Click += async (_, _) =>
            {
                updateAllBtn.IsEnabled = false; updateAllBtn.Content = "Updating…";
                allProgressBar.IsVisible = true; allProgressBar.Value = 0;
                int done = 0, failed = 0;
                foreach (var e in updates)
                {
                    int n = done + 1, completed = done;
                    summary.Text = $"Updating {FormatCoreName(e.FileName)}… ({n}/{updates.Count})";
                    var prog = new Progress<int>(p =>
                    {
                        summary.Text = $"Updating {FormatCoreName(e.FileName)}… {p}%  ({n}/{updates.Count})";
                        allProgressBar.Value = (completed * 100 + p) / (double)updates.Count;
                    });
                    try { await _coreDownloader.DownloadAsync(e, coresFolder, prog); }
                    catch (Exception ex) { failed++; System.Diagnostics.Trace.WriteLine($"[Cores] update {e.FileName} failed: {ex.Message}"); }
                    done++;
                    allProgressBar.Value = done * 100 / (double)updates.Count;
                }
                allProgressBar.IsVisible = false;
                summary.Text = failed == 0 ? $"Updated {done} core(s)." : $"Done — {done - failed} ok, {failed} failed (see Logs/cores.log).";
                BuildCoresPanel();
            };
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Cores] update check failed: {ex.Message}"); }
    }

    private static string FormatCoreName(string dllName) => Services.CoreOptionsService.DisplayNameFor(dllName);

    // ════════════════════════════════════════════════════════════════════════
    //  Controls panel (CI3) — per-console input mapping with live key/gamepad capture.
    //  Keyboard capture stores the Avalonia Key enum name. Disk Swap captures a CHORD
    //  (two sequential presses → "A+B"), mirroring upstream's CommitChordMapping flow.
    // ════════════════════════════════════════════════════════════════════════

    private Services.ControllerManager? _ctrl;
    private bool _controlsInit;
    private string _currentConsole = "SNES";
    private int _currentPlayer = 1;             // 1-based (matches upstream + the runtime config key)
    private bool _isKeyboardMode = true;
    private int _waitingRowIndex = -1;
    // Chord capture state (Disk Swap): first half pending until a DIFFERENT second press commits.
    private Key? _chordFirstKey;
    private uint? _chordFirstCtrl;
    private string? _chordFirstDisplay;
    private bool _suppressControlsAutoSave;
    // null until the user picks (or the first populate runs) — so a connected controller,
    // not Keyboard, wins the default when the Controls tab first opens.
    private string? _selectedDevice;
    private readonly List<CtrlRow> _ctrlRows = new();
    private readonly Dictionary<string, Services.InputMapping> _ctrlMappings = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CtrlRow { public string ButtonName = ""; public Border Box = null!; public TextBlock BoxLabel = null!; }

    private void InitControls()
    {
        var sysCombo = this.FindControl<ComboBox>("SystemComboBox")!;
        if (_controlsInit) { PopulateInputDevices(); return; }
        _controlsInit = true;

        _ctrl = new Services.ControllerManager();
        _ctrl.ButtonChanged += OnControllerButtonChanged;     // fires on the UI timer thread
        _ctrl.ConnectionChanged += _ => PopulateInputDevices();
        this.AddHandler(KeyDownEvent, OnControlsKeyDown, RoutingStrategies.Tunnel);

        PopulateSystemComboBox(sysCombo);
        sysCombo.SelectionChanged += (_, _) => { if (sysCombo.SelectedItem is ComboBoxItem { Tag: string tag }) { StopWaiting(); LoadConsole(tag); } };

        var playerCombo = this.FindControl<ComboBox>("PlayerComboBox")!;
        playerCombo.SelectedIndex = 0;
        playerCombo.SelectionChanged += (_, _) => { _currentPlayer = playerCombo.SelectedIndex + 1; StopWaiting(); LoadConsole(_currentConsole); };

        var slotCombo = this.FindControl<ComboBox>("ControllerSlotComboBox")!;
        slotCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressControlsAutoSave) return;
            var cfg = App.Configuration!.GetInputConfiguration(ConfigKey);
            cfg.ControllerSlot = slotCombo.SelectedIndex <= 0 ? -1 : slotCombo.SelectedIndex - 1;  // 0=Default→-1
            App.Configuration!.SetInputConfiguration(ConfigKey, cfg); App.Configuration!.ScheduleSave();
        };

        var devCombo = this.FindControl<ComboBox>("InputDeviceComboBox")!;
        devCombo.SelectionChanged += (_, _) =>
        {
            _selectedDevice = devCombo.SelectedItem as string ?? "Keyboard";
            _isKeyboardMode = _selectedDevice == "Keyboard";
            if (_ctrl != null) _ctrl.SetActiveDevice(Math.Max(0, devCombo.SelectedIndex - 1));
            StopWaiting();
            LoadConsole(_currentConsole);
        };

        this.FindControl<Button>("RefreshDevicesBtn")!.Click += (_, _) => PopulateInputDevices();
        this.FindControl<Button>("ResetDefaultsBtn")!.Click += (_, _) => ResetControlsDefaults();
        this.FindControl<Button>("SaveControlsBtn")!.Click += async (_, _) =>
        {
            SaveMappingsToConfig();
            // Persist NOW (not the 400ms debounce) so a running game host re-reads the new map, then
            // tell it to rebind live — a Controls edit applies to the running game, like on Windows,
            // instead of only at the next launch.
            try { if (App.Configuration != null) await App.Configuration.SaveAsync(); } catch { }
            Services.GameHostLauncher.BroadcastToHosts("reload-input");
            this.FindControl<Button>("SaveControlsBtn")!.Content = "Saved";
        };

        PopulateInputDevices();
        LoadConsole(_currentConsole);
    }

    private string ConfigKey => $"{_currentConsole}_P{_currentPlayer}";

    private void PopulateSystemComboBox(ComboBox combo)
    {
        var iconConv = new Converters.ConsoleTagToIconConverter();
        combo.Items.Clear();
        ComboBoxItem? selected = null;
        foreach (var (tag, name) in Configuration.ControllerDefinitions.GetSupportedConsoles())
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (iconConv.Convert(tag, typeof(IImage), null, System.Globalization.CultureInfo.InvariantCulture) is IImage icon)
                sp.Children.Add(new Image { Width = 18, Height = 18, Source = icon });
            sp.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(name) ? tag : name, VerticalAlignment = VerticalAlignment.Center });
            var item = new ComboBoxItem { Content = sp, Tag = tag };
            combo.Items.Add(item);
            if (tag == _currentConsole) selected = item;
        }
        combo.SelectedItem = selected ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void PopulateInputDevices()
    {
        var devCombo = this.FindControl<ComboBox>("InputDeviceComboBox")!;
        var devices = new List<string> { "Keyboard" };
        if (_ctrl != null) devices.AddRange(_ctrl.GetDeviceNames());
        _suppressControlsAutoSave = true;
        devCombo.ItemsSource = devices;
        // Keep the user's in-session pick if it still exists; otherwise default to the
        // first connected controller (devices[1] — Keyboard is always devices[0]).
        devCombo.SelectedItem = _selectedDevice != null && devices.Contains(_selectedDevice) ? _selectedDevice
            : devices.Count > 1 ? devices[1] : "Keyboard";
        _suppressControlsAutoSave = false;
        _selectedDevice = devCombo.SelectedItem as string ?? "Keyboard";
        _isKeyboardMode = _selectedDevice == "Keyboard";
    }

    private void LoadConsole(string tag)
    {
        _currentConsole = tag;
        if (!Configuration.ControllerDefinitions.AllControllers.TryGetValue(tag, out var def)) return;
        SetControllerImage(def.ControllerImage);
        LoadMappingsFromConfig();
        RebuildButtonsPanel(def);
    }

    private void SetControllerImage(string path)
    {
        var img = this.FindControl<Image>("ControllerImage")!;
        try
        {
            // WPF "/Assets/images/..." → avares://. Pass the path raw (spaces and all) — matches the
            // proven ConsoleTagToIconConverter loader; Avalonia unescapes when matching the resource key.
            var uri = new Uri("avares://Emutastic" + path);
            img.Source = new Bitmap(AssetLoader.Open(uri));
        }
        catch { img.Source = null; }
        // Upstream shrinks the over-zoomed FDS diagram to 0.7× (reference PreferencesWindow.xaml.cs:213).
        img.RenderTransformOrigin = RelativePoint.Center;
        img.RenderTransform = _currentConsole == "FDS" ? new ScaleTransform(0.7, 0.7) : null;
    }

    private void RebuildButtonsPanel(Configuration.ControllerDefinition def)
    {
        var panel = this.FindControl<StackPanel>("ButtonsPanel")!;
        panel.Children.Clear();
        _ctrlRows.Clear();

        foreach (var group in def.Buttons.GroupBy(b => string.IsNullOrEmpty(b.Group) ? "Buttons" : b.Group))
        {
            panel.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold,
                FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Height = 1, Fill = Brush("BorderNormalBrush"), Margin = new Thickness(0, 0, 0, 6) });
            foreach (var btn in group) panel.Children.Add(BuildMappingRow(btn.Name));
        }

        // Frontend-only Disk Swap action (upstream parity) — for consoles whose cores expose the
        // libretro disk control interface. Captured as a chord; unbound default is L3 + Start.
        if (Emulator.EmulatorSession.ConsoleSupportsDiskSwap(_currentConsole))
        {
            panel.Children.Add(new TextBlock { Text = "FRONTEND", FontSize = 10, FontWeight = FontWeight.SemiBold,
                FontFamily = Font("PrimaryFont"), Foreground = Brush("TextMutedBrush"), Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Height = 1, Fill = Brush("BorderNormalBrush"), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(BuildMappingRow("Disk Swap"));
        }
        RefreshAllRows();
    }

    private Control BuildMappingRow(string buttonName)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ColumnDefinitions = new ColumnDefinitions("*,110") };
        var nameLabel = new TextBlock { Text = buttonName, Foreground = Brush("TextPrimaryBrush"), FontSize = 12, FontFamily = Font("PrimaryFont"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(nameLabel, 0);

        var boxLabel = new TextBlock { Text = "—", Foreground = Brush("TextMutedBrush"), FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var box = new Border { Background = Brush("BgTertiaryBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4),
            Cursor = new Cursor(StandardCursorType.Hand), Child = boxLabel, Tag = buttonName };
        Grid.SetColumn(box, 1);
        box.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            int idx = _ctrlRows.FindIndex(r => r.ButtonName == buttonName);
            if (idx >= 0) StartWaiting(idx);
            Focus();
        };
        grid.Children.Add(nameLabel); grid.Children.Add(box);
        _ctrlRows.Add(new CtrlRow { ButtonName = buttonName, Box = box, BoxLabel = boxLabel });
        return grid;
    }

    private void RefreshAllRows() { for (int i = 0; i < _ctrlRows.Count; i++) RefreshRow(i); }

    private void RefreshRow(int idx)
    {
        if (idx < 0 || idx >= _ctrlRows.Count) return;
        var row = _ctrlRows[idx];
        if (idx == _waitingRowIndex)
        {
            row.Box.Background = Brush("AccentBrush"); row.BoxLabel.Text = "Press…"; row.BoxLabel.Foreground = Brushes.White; return;
        }
        if (_ctrlMappings.TryGetValue(row.ButtonName, out var m) && !string.IsNullOrEmpty(m.DisplayText) && m.DisplayText != "Not mapped")
        {
            row.Box.Background = Brush("BgQuaternaryBrush"); row.BoxLabel.Text = m.DisplayText; row.BoxLabel.Foreground = Brush("TextPrimaryBrush");
        }
        else { row.Box.Background = Brush("BgTertiaryBrush"); row.BoxLabel.Text = "—"; row.BoxLabel.Foreground = Brush("TextMutedBrush"); }
    }

    private void StartWaiting(int rowIndex)
    {
        var saveBtn = this.FindControl<Button>("SaveControlsBtn");
        if (saveBtn != null) saveBtn.Content = "Save";   // clear a prior "Saved" once the user edits again
        int prev = _waitingRowIndex;
        _waitingRowIndex = rowIndex;
        _chordFirstKey = null; _chordFirstCtrl = null; _chordFirstDisplay = null;   // drop a half-captured chord
        if (_ctrl != null) _ctrl.RawMode = true;
        if (prev >= 0) RefreshRow(prev);
        RefreshRow(rowIndex);
    }

    private void StopWaiting()
    {
        int prev = _waitingRowIndex;
        _waitingRowIndex = -1;
        _chordFirstKey = null; _chordFirstCtrl = null; _chordFirstDisplay = null;
        if (_ctrl != null) _ctrl.RawMode = false;
        if (prev >= 0) RefreshRow(prev);
    }

    // Chord commit (upstream CommitChordMapping): both halves packed into ChordIdentifier as
    // "A+B" — Avalonia Key names for keyboard, panel raw ids for controller. SaveMappingsToConfig
    // already serializes ChordIdentifier when present.
    private void CommitChordMapping(string buttonName, string display,
        Key keyA = Key.None, Key keyB = Key.None, uint ctrlA = uint.MaxValue, uint ctrlB = uint.MaxValue)
    {
        _ctrlMappings[buttonName] = new Services.InputMapping
        {
            ConsoleName = _currentConsole, ButtonName = buttonName,
            InputType = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
            Key = keyA, ControllerButtonId = ctrlA == uint.MaxValue ? 0 : ctrlA,
            DisplayText = display,
            ChordIdentifier = _isKeyboardMode ? $"{keyA}+{keyB}" : $"{ctrlA}+{ctrlB}",
        };
        _chordFirstKey = null; _chordFirstCtrl = null; _chordFirstDisplay = null;
        int cur = _waitingRowIndex;
        _waitingRowIndex = -1;
        RefreshRow(cur);
        if (_ctrl != null) _ctrl.RawMode = false;   // Disk Swap is the last row — no auto-advance
    }

    private void CommitMapping(string buttonName, string displayText, Key key = Key.None, uint controllerId = 0)
    {
        _ctrlMappings[buttonName] = new Services.InputMapping
        {
            ConsoleName = _currentConsole, ButtonName = buttonName,
            InputType = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
            Key = key, ControllerButtonId = controllerId, DisplayText = displayText,
        };
        int cur = _waitingRowIndex;
        _waitingRowIndex = -1;
        RefreshRow(cur);
        if (cur + 1 < _ctrlRows.Count) StartWaiting(cur + 1);   // auto-advance
        else if (_ctrl != null) _ctrl.RawMode = false;
    }

    private void OnControlsKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isKeyboardMode || _waitingRowIndex < 0) return;
        if (e.Key == Key.Escape) { StopWaiting(); e.Handled = true; return; }
        string btnName = _ctrlRows[_waitingRowIndex].ButtonName;
        string display = KeyToDisplayString(e.Key);

        // Disk Swap is captured as a CHORD: first press becomes the candidate first half,
        // a second DIFFERENT press commits both as "KeyA+KeyB" (upstream parity).
        if (string.Equals(btnName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            if (_chordFirstKey == null)
            {
                _chordFirstKey = e.Key; _chordFirstDisplay = display;
                _ctrlRows[_waitingRowIndex].BoxLabel.Text = $"{display} + …";
                return;
            }
            if (e.Key == _chordFirstKey.Value) return;   // ignore repeat of the same key
            CommitChordMapping(btnName, $"{_chordFirstDisplay} + {display}",
                keyA: _chordFirstKey.Value, keyB: e.Key);
            return;
        }

        CommitMapping(btnName, display, key: e.Key);
        e.Handled = true;
    }

    private void OnControllerButtonChanged(uint rawId, bool isPressed)
    {
        if (_isKeyboardMode || !isPressed || _waitingRowIndex < 0) return;
        string btnName = _ctrlRows[_waitingRowIndex].ButtonName;
        string display = rawId >= 110 ? StickDirToString(rawId)
            : rawId == 100 ? "L2" : rawId == 101 ? "R2" : $"Button {rawId}";

        // Disk Swap chord on the controller too: "rawA+rawB" in the panel id space.
        if (string.Equals(btnName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
        {
            if (_chordFirstCtrl == null)
            {
                _chordFirstCtrl = rawId; _chordFirstDisplay = display;
                _ctrlRows[_waitingRowIndex].BoxLabel.Text = $"{display} + …";
                return;
            }
            if (rawId == _chordFirstCtrl.Value) return;   // ignore repeat
            CommitChordMapping(btnName, $"{_chordFirstDisplay} + {display}",
                ctrlA: _chordFirstCtrl.Value, ctrlB: rawId);
            return;
        }

        CommitMapping(btnName, display, controllerId: rawId);
    }

    private void LoadMappingsFromConfig()
    {
        _ctrlMappings.Clear();
        var config = App.Configuration!.GetInputConfiguration(ConfigKey);
        var source = _isKeyboardMode ? config.KeyboardMappings : config.ControllerMappings;
        foreach (var m in source)
        {
            // Chord mappings ("A+B", authored by the Disk Swap row) keep the raw composite
            // identifier instead of parsing it to None.
            bool isChord = !string.IsNullOrEmpty(m.InputIdentifier) && m.InputIdentifier.Contains('+');
            _ctrlMappings[m.ButtonName] = new Services.InputMapping
            {
                ConsoleName = _currentConsole, ButtonName = m.ButtonName,
                InputType = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
                Key = _isKeyboardMode && !isChord && Enum.TryParse<Key>(m.InputIdentifier, out var k) ? k : Key.None,
                ControllerButtonId = !_isKeyboardMode && !isChord && uint.TryParse(m.InputIdentifier, out var bid) ? bid : 0,
                DisplayText = m.DisplayName,
                ChordIdentifier = isChord ? m.InputIdentifier : null,
            };
        }
        _suppressControlsAutoSave = true;
        int slot = config.ControllerSlot;
        this.FindControl<ComboBox>("ControllerSlotComboBox")!.SelectedIndex = (slot >= 0 && slot <= 3) ? slot + 1 : 0;
        _suppressControlsAutoSave = false;
    }

    private void SaveMappingsToConfig()
    {
        var config = App.Configuration!.GetInputConfiguration(ConfigKey);
        if (_isKeyboardMode)
        {
            config.KeyboardMappings.Clear();
            foreach (var m in _ctrlMappings.Values.Where(m => m.InputType == Services.InputType.Keyboard && (m.Key != Key.None || !string.IsNullOrEmpty(m.ChordIdentifier))))
                config.KeyboardMappings.Add(new Configuration.ButtonMapping { ButtonName = m.ButtonName,
                    InputIdentifier = string.IsNullOrEmpty(m.ChordIdentifier) ? m.Key.ToString() : m.ChordIdentifier!,
                    InputType = Configuration.InputType.Keyboard, DisplayName = m.DisplayText });
        }
        else
        {
            config.ControllerMappings.Clear();
            foreach (var m in _ctrlMappings.Values.Where(m => m.InputType == Services.InputType.Controller))
                config.ControllerMappings.Add(new Configuration.ButtonMapping { ButtonName = m.ButtonName,
                    InputIdentifier = string.IsNullOrEmpty(m.ChordIdentifier) ? m.ControllerButtonId.ToString() : m.ChordIdentifier!,
                    InputType = Configuration.InputType.Controller, DisplayName = m.DisplayText });
        }
        App.Configuration!.SetInputConfiguration(ConfigKey, config);
    }

    private void ResetControlsDefaults()
    {
        _ctrlMappings.Clear();
        var defaults = _isKeyboardMode
            ? Configuration.ConfigurationExtensions.GetDefaultKeyboardMappings(_currentConsole)
            : Configuration.ConfigurationExtensions.GetDefaultControllerMappings(_currentConsole);
        foreach (var d in defaults)
            _ctrlMappings[d.ButtonName] = new Services.InputMapping
            {
                ConsoleName = _currentConsole, ButtonName = d.ButtonName,
                InputType = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
                Key = _isKeyboardMode && Enum.TryParse<Key>(d.InputIdentifier, out var k) ? k : Key.None,
                ControllerButtonId = !_isKeyboardMode && uint.TryParse(d.InputIdentifier, out var bid) ? bid : 0,
                DisplayText = d.DisplayName,
            };
        StopWaiting();
        RefreshAllRows();
    }

    private static string StickDirToString(uint rawId) => rawId switch
    {
        110 => "L-Stick ←", 111 => "L-Stick →", 112 => "L-Stick ↑", 113 => "L-Stick ↓",
        114 => "R-Stick ←", 115 => "R-Stick →", 116 => "R-Stick ↑", 117 => "R-Stick ↓", _ => $"Axis {rawId}",
    };

    private static string KeyToDisplayString(Key key) => key switch
    {
        Key.Space => "Space", Key.Enter => "Enter", Key.Back => "Backspace", Key.Escape => "Escape", Key.Tab => "Tab",
        Key.LeftShift => "L Shift", Key.RightShift => "R Shift", Key.LeftCtrl => "L Ctrl", Key.RightCtrl => "R Ctrl",
        Key.LeftAlt => "L Alt", Key.RightAlt => "R Alt", Key.Up => "↑", Key.Down => "↓", Key.Left => "←", Key.Right => "→",
        _ => key.ToString(),
    };

    private IBrush? Brush(string key) => this.TryFindResource(key, out var v) ? v as IBrush : null;
    private FontFamily Font(string key) => this.TryFindResource(key, out var v) && v is FontFamily f ? f : FontFamily.Default;
    private static IBrush ParseBrush(string? hex, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(hex) ? fallback : hex)); }
        catch { return new SolidColorBrush(Color.Parse(fallback)); }
    }
}
