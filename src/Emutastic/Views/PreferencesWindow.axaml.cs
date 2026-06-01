using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

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
    }

    private void ShowPanel(string target)
    {
        foreach (var (_, panel) in Sections)
        {
            var grid = this.FindControl<Grid>(panel);
            if (grid != null) grid.IsVisible = panel == target;
        }
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
}
