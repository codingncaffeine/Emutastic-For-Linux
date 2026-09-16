using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Views;

// ── Sidebar layout editor (Preferences → Library). Rows come from ConsoleCatalog rather
//    than fixed markup, so the list stays right as consoles come and go. Applies LIVE:
//    each change writes LibraryConfiguration, schedules a save and rebuilds the sidebar in
//    the main window — a layout setting that only took effect after a restart would read
//    as broken. ──
public partial class PreferencesWindow
{
    // Populating rows assigns IsChecked, which raises IsCheckedChanged exactly as a user
    // click does. Without this guard the handler writes config and rebuilds the panel it is
    // currently being built from — an endless loop. Mirrors _populatingToast.
    private bool _populatingSidebarLayout;

    private StackPanel SidebarLayoutHost => this.FindControl<StackPanel>("SidebarLayoutPanel")!;

    private LibraryConfiguration Lib => App.Configuration!.GetLibraryConfiguration();

    /// <summary>The main window, whose sidebar these settings drive.</summary>
    private MainWindow? LibraryWindow
        => (this.Owner as MainWindow)
           ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    private void WireSidebarLayout()
    {
        var hideEmpty = this.FindControl<CheckBox>("HideEmptyConsolesCheck")!;
        hideEmpty.IsCheckedChanged += (_, _) =>
        {
            if (_populatingSidebarLayout) return;
            Lib.HideEmptyConsoles = hideEmpty.IsChecked == true;
            // Rebuild the editor too: this option changes which rows are dimmed as empty.
            CommitSidebarLayout(rebuildEditor: true, status: "");
        };

        this.FindControl<Button>("RestoreSidebarDefaultsBtn")!.Click += (_, _) =>
        {
            var lib = Lib;
            // CLEAR rather than write a copy of the defaults: empty means "use the built-in
            // layout", so the user keeps receiving consoles added by future updates.
            lib.ConsoleOrder.Clear();
            lib.GroupOrder.Clear();
            lib.HiddenConsoles.Clear();
            lib.HiddenGroups.Clear();
            lib.HideEmptyConsoles = false;

            _populatingSidebarLayout = true;
            try { this.FindControl<CheckBox>("HideEmptyConsolesCheck")!.IsChecked = false; }
            finally { _populatingSidebarLayout = false; }

            CommitSidebarLayout(rebuildEditor: true, status: "Sidebar restored to its default layout.");
        };
    }

    private void LoadSidebarLayout()
    {
        _populatingSidebarLayout = true;
        try { this.FindControl<CheckBox>("HideEmptyConsolesCheck")!.IsChecked = Lib.HideEmptyConsoles; }
        finally { _populatingSidebarLayout = false; }
        BuildSidebarLayoutEditor();
    }

    private void BuildSidebarLayoutEditor()
    {
        var host = SidebarLayoutHost;
        host.Children.Clear();
        var lib = Lib;

        bool previous = _populatingSidebarLayout;
        _populatingSidebarLayout = true;
        try
        {
            foreach (var entry in ConsoleCatalog.InUserOrder(ConsoleCatalog.Ungrouped, lib.ConsoleOrder, e => e.Tag))
                host.Children.Add(ConsoleRow(entry, lib, indent: 0));

            var groups = ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, g => g).ToList();
            for (int i = 0; i < groups.Count; i++)
            {
                host.Children.Add(GroupRow(groups[i], lib, first: i == 0, last: i == groups.Count - 1));
                foreach (var entry in ConsoleCatalog.InUserOrder(ConsoleCatalog.InGroup(groups[i]), lib.ConsoleOrder, e => e.Tag))
                    host.Children.Add(ConsoleRow(entry, lib, indent: 18));
            }
        }
        finally { _populatingSidebarLayout = previous; }

        if (Environment.GetEnvironmentVariable("EMUTASTIC_SIDEBAR_DIAG") == "1")
        {
            try
            {
                // Read the BUILT result, not the inputs: rows the editor failed to add have to
                // surface as a wrong count rather than being masked by the catalog it used.
                // Also record whether the main window resolved — if it is null every change
                // here would save config and update nothing, i.e. a dead setting.
                bool resolved = LibraryWindow != null;
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "sidebar-diag.log"),
                    $"=== layout editor built {DateTime.Now:HH:mm:ss.fff} === rows={host.Children.Count}"
                    + $" mainWindow={(resolved ? "resolved" : "NULL - live rebuild would silently do nothing")}\n");
            }
            catch { /* never throw from diagnostics */ }
        }
    }

    // ── Rows ────────────────────────────────────────────────────────────────────────────
    private Control GroupRow(string group, LibraryConfiguration lib, bool first, bool last)
    {
        bool hiddenGroup = lib.HiddenGroups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));

        var check = new CheckBox
        {
            IsChecked = !hiddenGroup,
            Content = group,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = TsRes("TextSecondaryBrush", "#9A9A9A"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_populatingSidebarLayout) return;
            SetHidden(Lib.HiddenGroups, group, hidden: check.IsChecked != true);
            CommitSidebarLayout(rebuildEditor: false, status: "");
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 10, 0, 2) };
        Grid.SetColumn(check, 0);
        row.Children.Add(check);
        row.Children.Add(ArrowButton("▲", 1, !first, () => MoveGroup(group, -1)));
        row.Children.Add(ArrowButton("▼", 2, !last, () => MoveGroup(group, +1)));
        return row;
    }

    private Control ConsoleRow(ConsoleCatalogEntry entry, LibraryConfiguration lib, int indent)
    {
        bool hidden = lib.HiddenConsoles.Any(c => string.Equals(c, entry.Tag, StringComparison.OrdinalIgnoreCase));
        bool groupHidden = entry.Group != null
            && lib.HiddenGroups.Any(g => string.Equals(g, entry.Group, StringComparison.OrdinalIgnoreCase));
        bool empty = lib.HideEmptyConsoles && (LibraryWindow?.GameCountForConsole(entry.Tag) ?? 1) == 0;

        string suffix = groupHidden ? "  (group hidden)" : empty ? "  (no games)" : "";
        var check = new CheckBox
        {
            IsChecked = !hidden,
            Content = entry.DisplayName + suffix,
            FontSize = 12,
            // Dim rows that are off for a reason other than their own checkbox, so it is
            // obvious why they are not in the sidebar.
            Foreground = groupHidden || empty
                ? TsRes("TextMutedBrush", "#6A6A6A")
                : TsRes("TextPrimaryBrush", "#E8E8E8"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_populatingSidebarLayout) return;
            SetHidden(Lib.HiddenConsoles, entry.Tag, hidden: check.IsChecked != true);
            CommitSidebarLayout(rebuildEditor: false, status: "");
        };

        bool isPinned = Favourites.Any(f => string.Equals(f, entry.Tag, StringComparison.OrdinalIgnoreCase));

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(indent, 1, 0, 1),
        };
        Grid.SetColumn(check, 0);
        row.Children.Add(check);
        row.Children.Add(ArrowButton(isPinned ? "★" : "☆", 1, true, () => TogglePinned(entry)));
        row.Children.Add(ArrowButton("▲", 2, true, () => MoveConsole(entry, -1)));
        row.Children.Add(ArrowButton("▼", 3, true, () => MoveConsole(entry, +1)));
        return row;
    }

    /// <summary>The pinned-console list, stored in UserPreferences (not LibraryConfiguration)
    /// because the field already existed there — it was simply never read by anything.</summary>
    private static List<string> Favourites => App.Configuration!.GetUserPreferences().FavoriteConsoles;

    private void TogglePinned(ConsoleCatalogEntry entry)
    {
        var favs = Favourites;
        if (favs.Any(f => string.Equals(f, entry.Tag, StringComparison.OrdinalIgnoreCase)))
            favs.RemoveAll(f => string.Equals(f, entry.Tag, StringComparison.OrdinalIgnoreCase));
        else
            favs.Add(entry.Tag);   // appended, so PINNED is ordered by when you pinned things

        App.Configuration!.SetUserPreferences(App.Configuration!.GetUserPreferences());
        CommitSidebarLayout(rebuildEditor: true, status: "");
    }

    private Button ArrowButton(string glyph, int column, bool enabled, Action onClick)
    {
        var btn = new Button
        {
            Content = glyph,
            FontSize = 10,
            Width = 26,
            IsEnabled = enabled,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (this.TryFindResource("PrefSecondaryBtn", out var t) && t is Avalonia.Styling.ControlTheme ct)
            btn.Theme = ct;
        btn.Click += (_, _) => { if (!_populatingSidebarLayout) onClick(); };
        Grid.SetColumn(btn, column);
        return btn;
    }

    // ── Mutations ───────────────────────────────────────────────────────────────────────
    private static void SetHidden(List<string> list, string value, bool hidden)
    {
        bool present = list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (hidden && !present) list.Add(value);
        else if (!hidden && present) list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Moves a console within its own group. The full effective order is written back, so
    /// the stored list is unambiguous however little the user had reordered before.
    /// </summary>
    private void MoveConsole(ConsoleCatalogEntry entry, int delta)
    {
        var lib = Lib;
        var siblings = SequenceFor(entry.Group, lib).ToList();
        int i = siblings.IndexOf(entry.Tag);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= siblings.Count) return;
        (siblings[i], siblings[j]) = (siblings[j], siblings[i]);

        var full = new List<string>();
        full.AddRange(entry.Group == null ? siblings : SequenceFor(null, lib));
        foreach (string g in ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, x => x))
            full.AddRange(string.Equals(g, entry.Group, StringComparison.Ordinal) ? siblings : SequenceFor(g, lib));

        lib.ConsoleOrder = full;
        CommitSidebarLayout(rebuildEditor: true, status: "");
    }

    private void MoveGroup(string group, int delta)
    {
        var lib = Lib;
        var order = ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, g => g).ToList();
        int i = order.IndexOf(group);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);

        lib.GroupOrder = order;
        CommitSidebarLayout(rebuildEditor: true, status: "");
    }

    private static IEnumerable<string> SequenceFor(string? group, LibraryConfiguration lib)
        => ConsoleCatalog.InUserOrder(
            group == null ? ConsoleCatalog.Ungrouped : ConsoleCatalog.InGroup(group),
            lib.ConsoleOrder, e => e.Tag).Select(e => e.Tag);

    private void CommitSidebarLayout(bool rebuildEditor, string status)
    {
        App.Configuration!.SetLibraryConfiguration(Lib);
        App.Configuration!.ScheduleSave();
        LibraryWindow?.BuildConsoleSidebar();          // live — never wait for a restart
        if (rebuildEditor) BuildSidebarLayoutEditor();
        this.FindControl<TextBlock>("SidebarLayoutStatusText")!.Text = status;
    }
}
