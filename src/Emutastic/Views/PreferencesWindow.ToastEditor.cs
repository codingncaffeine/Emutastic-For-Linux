using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Emutastic.Configuration;

namespace Emutastic.Views;

// ── A8f-4: achievement-toast appearance editor (port of upstream's procedural
//    BuildToastAppearanceUI / ThemeEditor pattern). Color rows are hex-entry +
//    live swatch — upstream's WinForms ColorDialog has no Linux counterpart.
//    The preview replica is styled by Services.ToastStyleRenderer; the live
//    in-game toast reads the same AchievementToastStyle in GlOsd. ──
public partial class PreferencesWindow
{
    private bool _toastUiBuilt;
    private bool _populatingToast;
    private readonly List<Action> _toastPopulators = new();
    private System.Threading.CancellationTokenSource? _toastSaveCts;
    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _previewImageCache = new();

    // Live config's style object — mutating it is what the toast reads.
    private AchievementToastStyle TsStyle => App.Configuration!.GetRetroAchievementsConfiguration().ToastStyle;

    private List<string>? _systemFontNamesCache;
    private List<string> SystemFontNames =>
        _systemFontNamesCache ??= FontManager.Current.SystemFonts
            .Select(f => f.Name).Distinct()
            // Hide families that can't render plain text (symbol/dingbat fonts) — picking one
            // made every toast character a box. Same glyph test the in-game renderer guards with.
            .Where(Platform.GlOsd.FontFamilyRendersText)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    // Dependent rows shown/hidden based on current selections.
    private Control? _rowGradStart, _rowGradEnd, _rowSolidColor,
                     _rowCornerRadius, _rowBadgeColor, _rowHeaderColor, _rowHeaderSize;

    private StackPanel TsHost => this.FindControl<StackPanel>("TsAppearanceHost")!;

    private IBrush TsRes(string key, string fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b
            ? b : new SolidColorBrush(Color.Parse(fallback));

    private void BuildToastAppearanceUI()
    {
        if (_toastUiBuilt) return;
        _toastUiBuilt = true;

        this.FindControl<Button>("TsPreviewBtn")!.Click += (_, _) => TsPreviewAnim();
        this.FindControl<Button>("TsResetBtn")!.Click += (_, _) => TsReset();

        AddComboRow("Shape", new[] { "Card", "Pill", "Rounded", "Sharp", "Custom" },
            s => s.Shape, (s, v) => s.Shape = v);
        _rowCornerRadius = AddSliderRow("Corner radius (Custom shape)", 0, 40, 1, true, "",
            s => s.CornerRadius, (s, v) => s.CornerRadius = v);

        AddSectionGap("Background");
        AddToggleRow("Use gradient (off = solid color)", s => s.UseGradient, (s, v) => s.UseGradient = v);
        _rowGradStart = AddColorRow("Gradient start", s => s.GradientStart, (s, v) => s.GradientStart = v);
        _rowGradEnd = AddColorRow("Gradient end", s => s.GradientEnd, (s, v) => s.GradientEnd = v);
        _rowSolidColor = AddColorRow("Solid color", s => s.BackgroundColor, (s, v) => s.BackgroundColor = v);
        AddSliderRow("Background opacity", 0, 100, 1, true, "%",
            s => s.BackgroundOpacity, (s, v) => s.BackgroundOpacity = (int)Math.Round(v));
        AddImageRow();

        AddSectionGap("Border");
        AddColorRow("Border color (blank = theme)", s => s.BorderColor, (s, v) => s.BorderColor = v);
        AddSliderRow("Border thickness", 0, 5, 0.5, false, "",
            s => s.BorderThickness, (s, v) => s.BorderThickness = v);

        AddSectionGap("Drop shadow");
        AddToggleRow("Enable drop shadow", s => s.ShadowEnabled, (s, v) => s.ShadowEnabled = v);
        AddColorRow("Shadow color", s => s.ShadowColor, (s, v) => s.ShadowColor = v);
        AddSliderRow("Shadow opacity", 0, 100, 1, true, "%",
            s => s.ShadowOpacity, (s, v) => s.ShadowOpacity = (int)Math.Round(v));
        AddSliderRow("Shadow blur", 0, 60, 1, true, "",
            s => s.ShadowBlur, (s, v) => s.ShadowBlur = v);
        AddSliderRow("Shadow depth", 0, 30, 1, true, "",
            s => s.ShadowDepth, (s, v) => s.ShadowDepth = v);

        AddSectionGap("Badge");
        AddToggleRow("Show badge", s => s.ShowBadge, (s, v) => s.ShowBadge = v);
        _rowBadgeColor = AddColorRow("Badge frame (blank = theme)", s => s.BadgeFrameColor, (s, v) => s.BadgeFrameColor = v);

        AddSectionGap("Header");
        AddToggleRow("Show header", s => s.ShowHeader, (s, v) => s.ShowHeader = v);
        _rowHeaderColor = AddColorRow("Header color (blank = theme)", s => s.HeaderColor, (s, v) => s.HeaderColor = v);
        _rowHeaderSize = AddSliderRow("Header size", 6, 20, 0.5, false, "",
            s => s.HeaderSize, (s, v) => s.HeaderSize = v);

        AddSectionGap("Title");
        AddColorRow("Title color", s => s.TitleColor, (s, v) => s.TitleColor = v);
        AddFontRow("Title font", s => s.TitleFont, (s, v) => s.TitleFont = v);
        AddSliderRow("Title size", 8, 32, 0.5, false, "",
            s => s.TitleSize, (s, v) => s.TitleSize = v);

        AddSectionGap("Description");
        AddColorRow("Description color", s => s.DescColor, (s, v) => s.DescColor = v);
        AddFontRow("Description font", s => s.DescFont, (s, v) => s.DescFont = v);
        AddSliderRow("Description size", 8, 24, 0.5, false, "",
            s => s.DescSize, (s, v) => s.DescSize = v);

        AddSectionGap("Points");
        AddColorRow("Points color (blank = theme)", s => s.PointsColor, (s, v) => s.PointsColor = v);
        AddSliderRow("Points size", 6, 20, 0.5, false, "",
            s => s.PointsSize, (s, v) => s.PointsSize = v);

        AddSectionGap("Layout");
        AddComboRow("Position",
            new[] { "TopLeft", "TopCenter", "TopRight", "BottomLeft", "BottomCenter", "BottomRight" },
            s => s.Position, (s, v) => s.Position = v);
        AddSliderRow("Duration (seconds)", 1, 12, 0.5, false, " s",
            s => s.DurationSec, (s, v) => s.DurationSec = v);

        BuildToastPreview();
    }

    private void UpdateToastConditionalRows()
    {
        if (!_toastUiBuilt) return;
        var s = TsStyle;
        void Show(Control? el, bool vis) { if (el != null) el.IsVisible = vis; }

        bool gradient = s.UseGradient;
        Show(_rowGradStart, gradient);
        Show(_rowGradEnd, gradient);
        Show(_rowSolidColor, !gradient);
        Show(_rowCornerRadius, string.Equals(s.Shape, "Custom", StringComparison.OrdinalIgnoreCase));
        Show(_rowBadgeColor, s.ShowBadge);
        Show(_rowHeaderColor, s.ShowHeader);
        Show(_rowHeaderSize, s.ShowHeader);
    }

    private void PopulateToastAppearance()
    {
        if (!_toastUiBuilt) return;
        _populatingToast = true;
        try { foreach (var p in _toastPopulators) p(); }
        finally { _populatingToast = false; }
        UpdateToastConditionalRows();
        RefreshToastPreview();
    }

    // ── Preview replica ────────────────────────────────────────────────────
    private Border? _pvRoot, _pvBadge;
    private TextBlock? _pvHeader, _pvTitle, _pvDesc, _pvPoints;
    private DispatcherTimer? _pvAnimTimer;

    private void BuildToastPreview()
    {
        var host = this.FindControl<ContentControl>("TsPreviewHost")!;
        if (_pvRoot != null) return;

        // Neutral backdrop so transparency / shadow read against something.
        var backdrop = new Border
        {
            Background = TsRes("BgTertiaryBrush", "#222226"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            MinHeight = 120,
        };

        _pvRoot = new Border
        {
            MinWidth = 320, MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid { Margin = new Thickness(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _pvBadge = new Border
        {
            Width = 58, Height = 58, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "🏆", FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(_pvBadge, 0);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        Grid.SetColumn(panel, 1);
        _pvHeader = new TextBlock { Text = "ACHIEVEMENT UNLOCKED", FontWeight = FontWeight.SemiBold, Opacity = 0.8 };
        _pvTitle  = new TextBlock { Text = "Sample Achievement", FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        _pvDesc   = new TextBlock { Text = "Earn this by doing the thing.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        _pvPoints = new TextBlock { Text = "10 points", Opacity = 0.9, Margin = new Thickness(0, 3, 0, 0) };
        panel.Children.Add(_pvHeader);
        panel.Children.Add(_pvTitle);
        panel.Children.Add(_pvDesc);
        panel.Children.Add(_pvPoints);

        grid.Children.Add(_pvBadge);
        grid.Children.Add(panel);
        _pvRoot.Child = grid;
        backdrop.Child = _pvRoot;
        host.Content = backdrop;
    }

    private void RefreshToastPreview()
    {
        if (_pvRoot == null) return;
        _pvRoot.Opacity = 1;
        Services.ToastStyleRenderer.ApplyTo(
            _pvRoot, _pvBadge!, _pvHeader!, _pvTitle!, _pvDesc!, _pvPoints!,
            TsStyle, LoadPreviewImage);
        // The renderer sets alignment/margin for on-screen Position; in the
        // preview box the replica stays centered (Position reads from its label).
        _pvRoot.HorizontalAlignment = HorizontalAlignment.Center;
        _pvRoot.VerticalAlignment = VerticalAlignment.Center;
        _pvRoot.Margin = new Thickness(0);
    }

    private Avalonia.Media.Imaging.Bitmap? LoadPreviewImage(string path)
    {
        try
        {
            if (_previewImageCache.TryGetValue(path, out var cached)) return cached;
            if (!System.IO.File.Exists(path)) return null;
            var bmp = new Avalonia.Media.Imaging.Bitmap(path);
            _previewImageCache[path] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    private void TsPreviewAnim()
    {
        if (_pvRoot == null) return;
        _pvAnimTimer?.Stop();
        RefreshToastPreview();
        _pvRoot.Opacity = 1;   // Avalonia: simple show/hide (WPF used a fade storyboard)
        _pvAnimTimer ??= new DispatcherTimer();
        _pvAnimTimer.Interval = Services.ToastStyleRenderer.Duration(TsStyle);
        _pvAnimTimer.Tick -= PvAnimTimer_Tick;
        _pvAnimTimer.Tick += PvAnimTimer_Tick;
        _pvRoot.Opacity = 0;
        DispatcherTimer.RunOnce(() => { if (_pvRoot != null) _pvRoot.Opacity = 1; }, TimeSpan.FromMilliseconds(250));
        _pvAnimTimer.Start();
    }

    private void PvAnimTimer_Tick(object? sender, EventArgs e)
    {
        _pvAnimTimer?.Stop();
        if (_pvRoot == null) return;
        _pvRoot.Opacity = 0.15;
        DispatcherTimer.RunOnce(() => { if (_pvRoot != null) _pvRoot.Opacity = 1; }, TimeSpan.FromMilliseconds(400));
    }

    private void TsReset()
    {
        var ra = App.Configuration!.GetRetroAchievementsConfiguration();
        ra.ToastStyle = new AchievementToastStyle();
        App.Configuration.SetRetroAchievementsConfiguration(ra);
        PopulateToastAppearance();
        App.Configuration.ScheduleSave();
    }

    // Debounced disk write (400ms after the last change) so slider drags
    // don't thrash settings.json.
    private void CommitToastStyle()
    {
        if (_suppressRaSave || !_achievementsLoaded) return;
        UpdateToastConditionalRows();
        RefreshToastPreview();
        App.Configuration!.SetRetroAchievementsConfiguration(App.Configuration.GetRetroAchievementsConfiguration());
        _toastSaveCts?.Cancel();
        _toastSaveCts = new System.Threading.CancellationTokenSource();
        var token = _toastSaveCts.Token;
        _ = Task.Delay(400, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() => App.Configuration!.ScheduleSave());
        }, TaskScheduler.Default);
    }

    // ── Row builders ────────────────────────────────────────────────────────
    private Control AddRow(string label, Control control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock
        {
            Text = label, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = TsRes("TextSecondaryBrush", "#B8B8B8"),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(lbl);
        grid.Children.Add(control);
        TsHost.Children.Add(grid);
        return grid;
    }

    private void AddSectionGap(string header)
    {
        TsHost.Children.Add(new TextBlock
        {
            Text = header, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Foreground = TsRes("TextPrimaryBrush", "#FFFFFF"),
            Margin = new Thickness(0, 14, 0, 8),
        });
    }

    private Control AddColorRow(string label,
        Func<AchievementToastStyle, string> get, Action<AchievementToastStyle, string> set)
    {
        // Hex entry + live swatch. (Upstream opened a WinForms ColorDialog from
        // the swatch — no Linux counterpart; the hex box covers the full space.)
        var swatch = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 22, Height = 22, RadiusX = 4, RadiusY = 4,
            Stroke = TsRes("BorderNormalBrush", "#3A3A3E"), StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var hex = new TextBox { Width = 110, FontSize = 12 };
        hex.TextChanged += (_, _) =>
        {
            UpdateSwatch(swatch, hex.Text);
            if (_populatingToast) return;
            set(TsStyle, (hex.Text ?? "").Trim());
            CommitToastStyle();
        };
        _toastPopulators.Add(() => { hex.Text = get(TsStyle); UpdateSwatch(swatch, hex.Text); });

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(swatch);
        panel.Children.Add(hex);
        return AddRow(label, panel);
    }

    private static void UpdateSwatch(Avalonia.Controls.Shapes.Rectangle swatch, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            swatch.Fill = Brushes.Transparent;
            ToolTip.SetTip(swatch, "Blank = theme default");
            return;
        }
        try
        {
            swatch.Fill = new SolidColorBrush(Color.Parse(hex.Trim()));
            ToolTip.SetTip(swatch, hex.Trim());
        }
        catch
        {
            swatch.Fill = Brushes.Transparent;
            ToolTip.SetTip(swatch, "Invalid color");
        }
    }

    private Control AddSliderRow(string label, double min, double max, double step, bool integer, string suffix,
        Func<AchievementToastStyle, double> get, Action<AchievementToastStyle, double> set)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Width = 160,
            TickFrequency = step, IsSnapToTickEnabled = step > 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueLbl = new TextBlock
        {
            Width = 54, FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TsRes("TextSecondaryBrush", "#B8B8B8"),
        };
        string Fmt(double v) => (integer ? ((int)Math.Round(v)).ToString() : v.ToString("0.#")) + suffix;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            valueLbl.Text = Fmt(slider.Value);
            if (_populatingToast) return;
            set(TsStyle, slider.Value);
            CommitToastStyle();
        };
        _toastPopulators.Add(() => { slider.Value = get(TsStyle); valueLbl.Text = Fmt(slider.Value); });

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(valueLbl);
        return AddRow(label, panel);
    }

    private void AddToggleRow(string label,
        Func<AchievementToastStyle, bool> get, Action<AchievementToastStyle, bool> set)
    {
        var t = new ToggleSwitch { OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        t.IsCheckedChanged += (_, _) =>
        {
            if (_populatingToast) return;
            set(TsStyle, t.IsChecked == true);
            CommitToastStyle();
        };
        _toastPopulators.Add(() => t.IsChecked = get(TsStyle));
        AddRow(label, t);
    }

    private void AddComboRow(string label, string[] items,
        Func<AchievementToastStyle, string> get, Action<AchievementToastStyle, string> set)
    {
        var combo = new ComboBox { Width = 180, VerticalAlignment = VerticalAlignment.Center };
        foreach (var it in items) combo.Items.Add(it);
        combo.SelectionChanged += (_, _) =>
        {
            if (_populatingToast) return;
            if (combo.SelectedItem is string v) { set(TsStyle, v); CommitToastStyle(); }
        };
        _toastPopulators.Add(() =>
        {
            var cur = get(TsStyle);
            combo.SelectedItem = items.FirstOrDefault(x => string.Equals(x, cur, StringComparison.OrdinalIgnoreCase))
                                 ?? items.First();
        });
        AddRow(label, combo);
    }

    private void AddFontRow(string label,
        Func<AchievementToastStyle, string> get, Action<AchievementToastStyle, string> set)
    {
        const string sentinel = "Default (theme)";
        var combo = new ComboBox { Width = 180, MaxDropDownHeight = 300, VerticalAlignment = VerticalAlignment.Center };
        combo.Items.Add(sentinel);
        foreach (var fam in SystemFontNames) combo.Items.Add(fam);
        combo.SelectionChanged += (_, _) =>
        {
            if (_populatingToast) return;
            if (combo.SelectedItem is string v) { set(TsStyle, v == sentinel ? "" : v); CommitToastStyle(); }
        };
        _toastPopulators.Add(() =>
        {
            var cur = get(TsStyle);
            combo.SelectedItem = string.IsNullOrWhiteSpace(cur)
                ? sentinel
                : (SystemFontNames.Contains(cur) ? cur : sentinel);
        });
        AddRow(label, combo);
    }

    private void AddImageRow()
    {
        var pathLbl = new TextBlock
        {
            Text = "No image", FontSize = 11, MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Foreground = TsRes("TextMutedBrush", "#9A9A9A"),
        };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0) };
        var clear = new Button { Content = "Clear", Padding = new Thickness(10, 4, 10, 4) };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose Toast Background Image",
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Image files")
                    { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } },
                },
            });
            string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path == null) return;
            try { TsStyle.BackgroundImage = Emutastic.AppPaths.ImportFileToDataRoot(path, "ToastBackgrounds"); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Toast background import failed: {ex.Message}");
                TsStyle.BackgroundImage = path;
            }
            pathLbl.Text = System.IO.Path.GetFileName(TsStyle.BackgroundImage);
            CommitToastStyle();
        };
        clear.Click += (_, _) =>
        {
            TsStyle.BackgroundImage = "";
            pathLbl.Text = "No image";
            CommitToastStyle();
        };
        _toastPopulators.Add(() => pathLbl.Text = string.IsNullOrWhiteSpace(TsStyle.BackgroundImage)
            ? "No image" : System.IO.Path.GetFileName(TsStyle.BackgroundImage));

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(pathLbl);
        panel.Children.Add(browse);
        panel.Children.Add(clear);
        AddRow("Background image", panel);
    }
}
