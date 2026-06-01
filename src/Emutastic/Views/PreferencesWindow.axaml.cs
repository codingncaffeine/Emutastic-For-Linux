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

    private IBrush? Brush(string key) => this.TryFindResource(key, out var v) ? v as IBrush : null;
    private FontFamily Font(string key) => this.TryFindResource(key, out var v) && v is FontFamily f ? f : FontFamily.Default;
    private static IBrush ParseBrush(string? hex, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(hex) ? fallback : hex)); }
        catch { return new SolidColorBrush(Color.Parse(fallback)); }
    }
}
