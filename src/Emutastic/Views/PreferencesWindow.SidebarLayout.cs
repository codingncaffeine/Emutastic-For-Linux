using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Views;

// ── Sidebar layout editor (Preferences → Library). Rows come from ConsoleCatalog rather
//    than fixed markup, so the list stays right as consoles come and go. Applies LIVE:
//    each change writes LibraryConfiguration, schedules a save and rebuilds the sidebar in
//    the main window — a layout setting that only took effect after a restart would read
//    as broken.
//
//    ⛔ Buttons here must NOT set Width. PrefSecondaryBtn has Padding="14,8" plus a 1px
//    border, so it needs ~30px before any content fits; a forced Width=26 collapsed the
//    content area and every button rendered BLANK. Labels are words, not glyphs, so no
//    font-coverage problem can blank them either. ──
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
           ?? (Application.Current?.ApplicationLifetime
               as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    private void WireSidebarLayout()
    {
        var hideEmpty = this.FindControl<CheckBox>("HideEmptyConsolesCheck")!;
        hideEmpty.IsCheckedChanged += (_, _) =>
        {
            if (_populatingSidebarLayout) return;
            Lib.HideEmptyConsoles = hideEmpty.IsChecked == true;
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
            // Say what the controls do, in the panel — a button you have to click to find out
            // is the same as no button at all.
            host.Children.Add(new TextBlock
            {
                Text = "Untick a console to hide it. Up / Down reorder within a group; on a "
                     + "manufacturer heading they move that whole group and its consoles together.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = TsRes("TextMutedBrush", "#6A6A6A"),
            });

            // Positions are materialised so each row knows whether it can actually move. A
            // button that is enabled but hits an early return is worse than a disabled one:
            // the click silently does nothing and the feature looks broken.
            _rowItems.Clear();

            var ungrouped = ConsoleCatalog.InUserOrder(ConsoleCatalog.Ungrouped, lib.ConsoleOrder, e => e.Tag).ToList();
            for (int u = 0; u < ungrouped.Count; u++)
            {
                var row = ConsoleRow(ungrouped[u], lib, indent: 0,
                                     first: u == 0, last: u == ungrouped.Count - 1);
                host.Children.Add(row);
                MakeDraggable(row, ungrouped[u]);
            }

            var groups = ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, g => g).ToList();
            for (int i = 0; i < groups.Count; i++)
            {
                var head = GroupRow(groups[i], lib, first: i == 0, last: i == groups.Count - 1);
                host.Children.Add(head);
                MakeDraggable(head, groups[i]);

                var inGroup = ConsoleCatalog.InUserOrder(ConsoleCatalog.InGroup(groups[i]), lib.ConsoleOrder, e => e.Tag).ToList();
                for (int c = 0; c < inGroup.Count; c++)
                {
                    var row = ConsoleRow(inGroup[c], lib, indent: 18,
                                         first: c == 0, last: c == inGroup.Count - 1);
                    host.Children.Add(row);
                    MakeDraggable(row, inGroup[c]);
                }
            }
        }
        finally { _populatingSidebarLayout = previous; }

        if (Environment.GetEnvironmentVariable("EMUTASTIC_SIDEBAR_DIAG") == "1")
        {
            try
            {
                // Read the BUILT result, not the inputs. Also record whether the main window
                // resolved — if it is null every change here saves config and updates nothing.
                bool resolved = LibraryWindow != null;
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "sidebar-diag.log"),
                    $"=== layout editor built {DateTime.Now:HH:mm:ss.fff} === rows={host.Children.Count}"
                    + $" mainWindow={(resolved ? "resolved" : "NULL - live rebuild would silently do nothing")}\n");

                // A build says nothing about whether a button is VISIBLE: the previous version
                // compiled cleanly and rendered every button blank, because a forced Width was
                // narrower than the theme's padding and clipped the label away. So measure the
                // rendered bounds after a layout pass. The full-size Restore button is logged as
                // a reference: if these numbers were being read before layout they would all be
                // zero, and that would show here rather than passing quietly.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        int measured = 0, clipped = 0, disabled = 0;
                        foreach (var child in host.Children.OfType<Grid>())
                            foreach (var b in child.Children.OfType<Button>().Where(x => (x.Tag as string) == "move"))
                            {
                                measured++;
                                if (b.Bounds.Width < 32) clipped++;
                                // A move button at the end of its list must be DISABLED: enabled
                                // but inert means the click silently does nothing.
                                if (!b.IsEnabled) disabled++;
                                if (measured <= 4)
                                    sb.AppendLine($"    button '{b.Content}' w={b.Bounds.Width:F1} h={b.Bounds.Height:F1}");
                            }
                        double reference = this.FindControl<Button>("RestoreSidebarDefaultsBtn")?.Bounds.Width ?? -1;
                        sb.AppendLine($"    move buttons measured={measured} clipped(<32px)={clipped} disabled={disabled}"
                                    + $"  [reference 'Restore Default Layout' w={reference:F1}]");
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "sidebar-diag.log"), sb.ToString());
                    }
                    catch { /* never throw from diagnostics */ }
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
            catch { /* never throw from diagnostics */ }
        }
    }

    // ── Rows ────────────────────────────────────────────────────────────────────────────
    private Grid ThreeColumnRow(Thickness margin)
        => new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = margin };

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

        var row = ThreeColumnRow(new Thickness(0, 12, 0, 2));
        Grid.SetColumn(check, 0);
        row.Children.Add(check);
        row.Children.Add(MoveButton("Up", 1, !first, $"Move the whole {group} group, and its consoles, up",
                                    () => MoveGroup(group, -1)));
        row.Children.Add(MoveButton("Down", 2, !last, $"Move the whole {group} group, and its consoles, down",
                                    () => MoveGroup(group, +1)));
        return row;
    }

    private Control ConsoleRow(ConsoleCatalogEntry entry, LibraryConfiguration lib, int indent,
                               bool first, bool last)
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

        string where = entry.Group == null ? "the top of the list" : $"the {entry.Group} group";
        var row = ThreeColumnRow(new Thickness(indent, 1, 0, 1));
        Grid.SetColumn(check, 0);
        row.Children.Add(check);
        row.Children.Add(MoveButton("Up", 1, !first, $"Move {entry.DisplayName} up within {where}",
                                    () => MoveConsole(entry, -1)));
        row.Children.Add(MoveButton("Down", 2, !last, $"Move {entry.DisplayName} down within {where}",
                                    () => MoveConsole(entry, +1)));
        return row;
    }

    /// <summary>
    /// A small labelled move button. ⛔ Never sets Width: PrefSecondaryBtn's 14,8 padding plus
    /// its border needs ~30px before content fits, so a forced narrow width clips the label to
    /// nothing and the button renders blank. Padding is overridden locally instead, which beats
    /// the theme's setter and lets the button size to its text.
    /// </summary>
    private Button MoveButton(string label, int column, bool enabled, string tip, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = 11,
            Padding = new Thickness(10, 3),
            // A shared minimum so "Up" and "Down" columns line up instead of sitting ragged
            // at their natural text widths (measured 37 vs 52). Still no fixed Width.
            MinWidth = 54,
            IsEnabled = enabled,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "move",   // lets the diagnostic count move buttons only — CheckBox derives
                            // from ToggleButton derives from Button, so OfType<Button> catches
                            // every checkbox too and over-reports.
        };
        if (this.TryFindResource("PrefSecondaryBtn", out var t) && t is Avalonia.Styling.ControlTheme ct)
            btn.Theme = ct;
        ToolTip.SetTip(btn, tip);
        btn.Click += (_, _) => { if (!_populatingSidebarLayout) onClick(); };
        Grid.SetColumn(btn, column);
        return btn;
    }

    // ── Drag to reorder ─────────────────────────────────────────────────────────────────
    // Implemented with pointer events rather than Avalonia's DragDrop: that stack goes through
    // the platform, and this app already documents the X11 backend not implementing a drop
    // target. Pointer capture is in-process and behaves the same everywhere.
    //
    // Semantics match the Up/Down buttons: a console moves WITHIN its group (a console's
    // manufacturer comes from ConsoleCatalog, not configuration), and a heading moves the whole
    // group. A drop onto anything else is ignored rather than guessed at.

    private readonly Dictionary<Control, object> _rowItems = new();
    private Control? _dragRow;
    private object? _dragItem;
    private double _dragStartY;
    private bool _dragging;

    private void MakeDraggable(Control row, object item)
    {
        _rowItems[row] = item;

        row.PointerPressed += (_, e) =>
        {
            // Never start a drag from a control the row already uses — the checkbox and the
            // move buttons must keep working as plain clicks.
            if (IsInteractive(e.Source as Control, row)) return;
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;

            _dragRow = row;
            _dragItem = item;
            _dragStartY = e.GetPosition(SidebarLayoutHost).Y;
            _dragging = false;
            e.Pointer.Capture(row);
        };

        row.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(_dragRow, row)) return;
            double y = e.GetPosition(SidebarLayoutHost).Y;
            if (!_dragging && Math.Abs(y - _dragStartY) < 4) return;   // ignore click jitter
            _dragging = true;
            row.Opacity = 0.55;                                         // it is being carried
        };

        row.PointerReleased += (_, e) =>
        {
            if (!ReferenceEquals(_dragRow, row)) return;
            e.Pointer.Capture(null);
            row.Opacity = 1;
            // Read what is being dragged BEFORE EndDrag clears it — otherwise every drop
            // silently does nothing, which is the hardest kind of bug to notice.
            bool wasDragging = _dragging;
            object? dropped = _dragItem;
            double y = e.GetPosition(SidebarLayoutHost).Y;
            EndDrag();
            if (wasDragging && dropped != null) DropAt(dropped, y);
        };

        // A capture lost to anything else (window deactivation, a popup) must not leave the row
        // dimmed and the editor half in a drag.
        row.AddHandler(InputElement.PointerCaptureLostEvent, (object? _, PointerCaptureLostEventArgs _) =>
        {
            row.Opacity = 1;
            EndDrag();
        });
    }

    private void EndDrag()
    {
        _dragRow = null;
        _dragItem = null;
        _dragging = false;
    }

    /// <summary>True when the press landed on the row's checkbox or a move button.</summary>
    private static bool IsInteractive(Control? source, Control row)
    {
        for (var c = source; c != null && !ReferenceEquals(c, row); c = c.Parent as Control)
            if (c is Button or CheckBox) return true;
        return false;
    }

    private void DropAt(object dropped, double y)
    {
        // Which row is under the pointer?
        Control? target = SidebarLayoutHost.Children
            .OfType<Control>()
            .FirstOrDefault(c => y >= c.Bounds.Top && y <= c.Bounds.Bottom);
        if (target == null || !_rowItems.TryGetValue(target, out object? onto)) return;

        if (dropped is ConsoleCatalogEntry from && onto is ConsoleCatalogEntry to)
        {
            if (!string.Equals(from.Group, to.Group, StringComparison.Ordinal)) return;  // group is catalog data
            if (string.Equals(from.Tag, to.Tag, StringComparison.Ordinal)) return;
            ReorderConsole(from, to);
        }
        else if (dropped is string fromGroup && onto is string ontoGroup
                 && !string.Equals(fromGroup, ontoGroup, StringComparison.Ordinal))
        {
            ReorderGroup(fromGroup, ontoGroup);
        }
    }

    private void ReorderConsole(ConsoleCatalogEntry from, ConsoleCatalogEntry to)
    {
        var lib = Lib;
        var siblings = SequenceFor(from.Group, lib).ToList();
        int target = siblings.IndexOf(to.Tag);
        if (!siblings.Remove(from.Tag) || target < 0) return;
        siblings.Insert(Math.Min(target, siblings.Count), from.Tag);

        var full = new List<string>();
        full.AddRange(from.Group == null ? siblings : SequenceFor(null, lib));
        foreach (string g in ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, x => x))
            full.AddRange(string.Equals(g, from.Group, StringComparison.Ordinal) ? siblings : SequenceFor(g, lib));

        lib.ConsoleOrder = full;
        LogLayoutAction($"drag {from.Tag} onto {to.Tag} in {from.Group ?? "(ungrouped)"}");
        CommitSidebarLayout(rebuildEditor: true, status: "");
    }

    private void ReorderGroup(string from, string onto)
    {
        var lib = Lib;
        var order = ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, g => g).ToList();
        int target = order.IndexOf(onto);
        if (!order.Remove(from) || target < 0) return;
        order.Insert(Math.Min(target, order.Count), from);

        lib.GroupOrder = order;
        LogLayoutAction($"drag group {from} onto {onto}");
        CommitSidebarLayout(rebuildEditor: true, status: "");
    }

    /// <summary>Drag is the one part of this editor that cannot be verified without a human
    /// pointer, so record what a drop actually did.</summary>
    private static void LogLayoutAction(string what)
    {
        if (Environment.GetEnvironmentVariable("EMUTASTIC_SIDEBAR_DIAG") != "1") return;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "sidebar-diag.log"),
                $"    ACTION {DateTime.Now:HH:mm:ss.fff} {what}\n");
        }
        catch { /* never throw from diagnostics */ }
    }

    // ── Mutations ───────────────────────────────────────────────────────────────────────
    private static void SetHidden(List<string> list, string value, bool hidden)
    {
        bool present = list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (hidden && !present) list.Add(value);
        else if (!hidden && present) list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Moves a console within its own group. The full effective order is written back, so the
    /// stored list is unambiguous however little the user had reordered before.
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
