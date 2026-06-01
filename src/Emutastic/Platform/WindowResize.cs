using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Emutastic.Platform;

/// <summary>
/// Restores edge/corner resize for borderless windows (WindowDecorations="None").
/// WPF gave this free via CanResizeWithGrip; with no OS border, Avalonia needs us to
/// hit-test the window edges, show the right resize cursor, and call BeginResizeDrag.
/// Pure pointer handling — no layout/markup change, so the aesthetic is untouched.
/// </summary>
internal static class WindowResize
{
    private const double EdgeThickness = 6;    // straight-edge grab band
    private const double CornerSize     = 16;   // larger corner grab box (the easy target)

    public static void Enable(Window w)
    {
        // Tunnel + handledEventsToo so we see moves/presses before children consume them
        // (lets an edge press claim the resize even over the list/sidebar).
        w.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (w.WindowState != WindowState.Normal) { w.Cursor = Cursor.Default; return; }
            var edge = EdgeAt(w, e.GetPosition(w));
            w.Cursor = edge is { } ed ? CursorFor(ed) : Cursor.Default;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        w.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (w.WindowState != WindowState.Normal) return;
            if (!e.GetCurrentPoint(w).Properties.IsLeftButtonPressed) return;
            if (EdgeAt(w, e.GetPosition(w)) is { } edge)
            {
                e.Handled = true;
                w.BeginResizeDrag(edge, e);
            }
        }, RoutingStrategies.Tunnel);
    }

    private static WindowEdge? EdgeAt(Window w, Point p)
    {
        double width = w.Bounds.Width, height = w.Bounds.Height;
        bool nearLeft   = p.X <= CornerSize;
        bool nearRight  = p.X >= width - CornerSize;
        bool nearTop    = p.Y <= CornerSize;
        bool nearBottom = p.Y >= height - CornerSize;

        // Corners first (bigger grab target).
        if (nearTop && nearLeft)     return WindowEdge.NorthWest;
        if (nearTop && nearRight)    return WindowEdge.NorthEast;
        if (nearBottom && nearLeft)  return WindowEdge.SouthWest;
        if (nearBottom && nearRight) return WindowEdge.SouthEast;

        // Straight edges (thinner band).
        if (p.X <= EdgeThickness)          return WindowEdge.West;
        if (p.X >= width - EdgeThickness)  return WindowEdge.East;
        if (p.Y <= EdgeThickness)          return WindowEdge.North;
        if (p.Y >= height - EdgeThickness) return WindowEdge.South;
        return null;
    }

    // Cache one Cursor per type — PointerMoved fires constantly; don't reallocate.
    private static readonly System.Collections.Generic.Dictionary<StandardCursorType, Cursor> _cursors = new();

    private static Cursor CursorFor(WindowEdge edge)
    {
        var type = edge switch
        {
            WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
            WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
            WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
            WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
            _ => StandardCursorType.SizeNorthSouth,
        };
        if (!_cursors.TryGetValue(type, out var c)) _cursors[type] = c = new Cursor(type);
        return c;
    }
}
