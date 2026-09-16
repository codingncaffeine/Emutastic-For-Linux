using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>
/// Builds the sidebar's console list from <see cref="ConsoleCatalog"/> instead of the
/// hand-written markup that used to spell out every button.
///
/// ⛔ The DEFAULT MUST RENDER IDENTICALLY to the markup it replaced — same entries, same
/// order, same labels, same headings, same margins, same 20×20 icons. With an untouched
/// <see cref="LibraryConfiguration"/> every list below is empty, so the output is exactly
/// the old sidebar. Customisation only ever removes or reorders rows; it never restyles
/// them. `verify-console-catalog.cs` diffs the catalog against the pre-change markup.
/// </summary>
public partial class MainWindow : Window
{
    // One converter instance, matching how PreferencesWindow reuses it. It caches decoded
    // bitmaps internally, so rebuilding the sidebar doesn't re-decode 35 images.
    private readonly Converters.ConsoleTagToIconConverter _consoleIcon = new();

    /// <summary>
    /// Fills ConsoleNavPanel. Safe to call again after the user changes the layout in
    /// Preferences — it clears and rebuilds, then restores the current selection's
    /// highlight (which lives on the button, so a rebuild would otherwise drop it).
    /// </summary>
    public void BuildConsoleSidebar()
    {
        var panel = this.FindControl<StackPanel>("ConsoleNavPanel");
        if (panel == null) return;
        panel.Children.Clear();

        var itemTheme = this.TryFindResource("SidebarItemStyle", out var it)
            ? it as Avalonia.Styling.ControlTheme : null;
        var groupTheme = this.TryFindResource("SidebarExpander", out var gt)
            ? gt as Avalonia.Styling.ControlTheme : null;

        var cfg = App.Configuration?.GetLibraryConfiguration() ?? new LibraryConfiguration();
        var hiddenConsoles = new HashSet<string>(cfg.HiddenConsoles ?? new(), StringComparer.OrdinalIgnoreCase);
        var hiddenGroups = new HashSet<string>(cfg.HiddenGroups ?? new(), StringComparer.OrdinalIgnoreCase);

        bool Visible(ConsoleCatalogEntry e)
        {
            if (hiddenConsoles.Contains(e.Tag)) return false;
            if (e.Group != null && hiddenGroups.Contains(e.Group)) return false;
            // Only consult the database when the option is on — GetGameCountForConsole is a
            // query per console, and the default path must not pay for a feature nobody enabled.
            if (cfg.HideEmptyConsoles && (_db?.GetGameCountForConsole(e.Tag) ?? 0) == 0) return false;
            return true;
        }

        // Standalone entries (Arcade) sit above the manufacturer groups, as they always have.
        foreach (var entry in InUserOrder(ConsoleCatalog.Ungrouped.Where(Visible), cfg.ConsoleOrder, e => e.Tag))
            panel.Children.Add(MakeConsoleButton(entry, itemTheme));

        bool firstGroup = true;
        foreach (string group in InUserOrder(ConsoleCatalog.DefaultGroupOrder, cfg.GroupOrder, g => g))
        {
            if (hiddenGroups.Contains(group)) continue;

            var entries = InUserOrder(ConsoleCatalog.InGroup(group).Where(Visible), cfg.ConsoleOrder, e => e.Tag).ToList();
            if (entries.Count == 0) continue;   // never leave a heading with nothing under it

            var inner = new StackPanel();
            foreach (var entry in entries)
                inner.Children.Add(MakeConsoleButton(entry, itemTheme));

            var expander = new Expander
            {
                Header = group,
                IsExpanded = true,
                // The first group carries more top margin than the rest — this asymmetry is
                // in the original markup (6,4,0,0 vs 6,2,0,0) and is load-bearing for spacing.
                Margin = firstGroup ? new Thickness(6, 4, 0, 0) : new Thickness(6, 2, 0, 0),
                Content = inner,
            };
            if (groupTheme != null) expander.Theme = groupTheme;
            panel.Children.Add(expander);
            firstGroup = false;
        }

        // A rebuild replaces the buttons, so re-apply the "selected" class to the new one.
        HighlightSidebar(_currentNavTag);

        DumpSidebarDiag(panel);
    }

    /// <summary>
    /// Records what the sidebar ACTUALLY contains once built — read back out of the panel
    /// itself, never from the catalog it was built from, so a renderer that drops, reorders
    /// or un-icons a row shows up here instead of being masked by the data it was given.
    ///
    /// Off unless EMUTASTIC_SIDEBAR_DIAG=1 (mirrors EMUTASTIC_INPUT_DIAG). Reads only —
    /// it must never touch the tree it is measuring.
    /// </summary>
    private static void DumpSidebarDiag(StackPanel panel)
    {
        if (Environment.GetEnvironmentVariable("EMUTASTIC_SIDEBAR_DIAG") != "1") return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== sidebar built {DateTime.Now:HH:mm:ss.fff} ===");
            int rows = 0, noIcon = 0;

            void DumpButton(Button b, string indent)
            {
                rows++;
                string tag = b.CommandParameter as string ?? "(NO CommandParameter)";
                string label = "(NO label)";
                bool hasIcon = false, hasGlyph = false;
                if (b.Content is StackPanel sp)
                {
                    var texts = sp.Children.OfType<TextBlock>().ToList();
                    if (texts.Count > 0 && texts[^1].Text != null) label = texts[^1].Text!;
                    hasIcon = sp.Children.OfType<Image>().FirstOrDefault()?.Source != null;
                    hasGlyph = texts.Count > 1;
                }
                if (!hasIcon && !hasGlyph) noIcon++;
                string art = hasIcon ? "icon" : hasGlyph ? "glyph" : "MISSING";
                sb.AppendLine($"{indent}{tag,-14} | {label,-22} | {art}");
            }

            foreach (var child in panel.Children)
            {
                if (child is Button b) DumpButton(b, "  ");
                else if (child is Expander ex)
                {
                    sb.AppendLine($"  [{ex.Header}] margin={ex.Margin} expanded={ex.IsExpanded}");
                    if (ex.Content is StackPanel inner)
                        foreach (var c in inner.Children.OfType<Button>()) DumpButton(c, "      ");
                }
                else sb.AppendLine($"  ?? unexpected child: {child.GetType().Name}");
            }

            sb.AppendLine($"TOTAL console rows: {rows}   rows with no artwork: {noIcon}");
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "sidebar-diag.log"), sb.ToString());
        }
        catch { /* never throw from diagnostics */ }
    }

    /// <summary>
    /// One console row. The shape is not cosmetic: HighlightSidebar and the console context
    /// menu both identify a console by <c>CommandParameter</c>, and the context menu reads
    /// the display name from the LAST TextBlock in the content panel.
    /// </summary>
    private Button MakeConsoleButton(ConsoleCatalogEntry entry, Avalonia.Styling.ControlTheme? theme)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        if (string.Equals(entry.Tag, "Arcade", StringComparison.Ordinal))
        {
            // Arcade is drawn with a glyph rather than an icon asset (upstream Windows uses
            // arcade.png here; this port has always used the joystick character).
            row.Children.Add(new TextBlock
            {
                Text = ConsoleCatalog.ArcadeGlyph,
                FontSize = 16,
                Width = 20,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            row.Children.Add(new Image
            {
                Width = 20,
                Height = 20,
                Source = _consoleIcon.Convert(entry.Tag, typeof(IImage), null, CultureInfo.InvariantCulture) as IImage,
            });
        }

        row.Children.Add(new TextBlock { Text = entry.DisplayName, VerticalAlignment = VerticalAlignment.Center });

        var btn = new Button { Content = row, CommandParameter = entry.Tag };
        if (theme != null) btn.Theme = theme;
        btn.Command = _vm?.NavigateToConsoleCommand;
        return btn;
    }

    /// <summary>
    /// How many games this console has. Exposed for the Preferences sidebar-layout editor,
    /// which dims rows that the "hide consoles with no games" option would remove — the
    /// database itself stays private to the window.
    /// </summary>
    public int GameCountForConsole(string tag) => _db?.GetGameCountForConsole(tag) ?? 0;

    // Ordering lives in ConsoleCatalog.InUserOrder — shared with the Preferences editor so
    // the two can never disagree about what the user's order means.
    private static IEnumerable<T> InUserOrder<T>(IEnumerable<T> items, List<string>? order, Func<T, string> key)
        => ConsoleCatalog.InUserOrder(items, order, key);
}
