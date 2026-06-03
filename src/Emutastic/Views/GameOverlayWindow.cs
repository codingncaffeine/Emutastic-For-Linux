using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Emutastic.Emulator;

namespace Emutastic.Views
{
    /// <summary>
    /// OVERLAY FEASIBILITY TEST (EMUTASTIC_OVERLAY_TEST=1). A transparent, borderless, topmost, NON-
    /// activating Avalonia window floated OVER the WINDOWED SDL-GL game window in the SAME process. It
    /// follows the game window's position/size, and an empty X11 input region makes it click-through so the
    /// game underneath keeps focus + input (and therefore stays smooth). A visible pill proves the menu
    /// layer paints over the running game. If this coexists cleanly, the real cog overlay is built the same
    /// way. See docs/gl-present-phase1-host-process-design.md.
    /// </summary>
    public sealed class GameOverlayWindow : Window
    {
        private readonly EmulatorSession _session;
        private readonly DispatcherTimer _follow;
        private readonly DispatcherTimer _autoHide;
        private readonly Border _pill;
        private bool _clickThroughSet;

        public GameOverlayWindow(EmulatorSession session)
        {
            _session = session;
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;
            Topmost = true;
            CanResize = false;
            ShowInTaskbar = false;
            ShowActivated = false;   // never steal focus from the game window (focus = smoothness)
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 960; Height = 720;

            _pill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(210, 18, 18, 22)),
                CornerRadius = new CornerRadius(22),
                Padding = new Thickness(20, 10),
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = 0,   // hidden during play; reveals on mouse-move, hides on leave/idle
                Child = new TextBlock
                {
                    Text = "⏻   ⏸   ↻   💾   ⚙",
                    Foreground = Brushes.White,
                    FontSize = 18,
                },
            };
            Content = new Grid { Children = { _pill } };

            _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _follow.Tick += (_, _) => FollowGameWindow();
            _autoHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            _autoHide.Tick += (_, _) => { _autoHide.Stop(); _pill.Opacity = 0; };

            // The game window owns the cursor (we're click-through), so it tells us when to reveal/hide.
            _session.GameMouseMoved += () => Dispatcher.UIThread.Post(RevealPill);
            _session.GameMouseLeft  += () => Dispatcher.UIThread.Post(HidePill);

            Opened += OnOpened;
        }

        private void RevealPill()
        {
            _pill.Opacity = 1;
            _autoHide.Stop();
            _autoHide.Start();   // fade out after a couple idle seconds even if the cursor stays in-window
        }

        private void HidePill()
        {
            _autoHide.Stop();
            _pill.Opacity = 0;
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                if (!_session.Start(out string? err))
                {
                    System.Diagnostics.Trace.WriteLine($"[Overlay] session start failed: {err}");
                    Dispatcher.UIThread.Post(Close);
                    return;
                }
                _session.WaitForExit();   // game closed (Esc) → close the overlay too
                _session.Dispose();
                Dispatcher.UIThread.Post(Close);
            });
            _follow.Start();
        }

        // Track the game window: match its screen rect so the pill sits over it, keep above it, and make
        // ourselves click-through once the handle exists so input falls to the game beneath.
        private void FollowGameWindow()
        {
            if (!_session.TryGetGameWindowRect(out int x, out int y, out int w, out int h) || w <= 0 || h <= 0)
                return;
            Position = new PixelPoint(x, y);
            Width = w; Height = h;
            if (!_clickThroughSet) { TrySetClickThrough(); _clickThroughSet = true; }
        }

        // ── X11 (Xwayland) click-through: empty input shape → all events pass to the window beneath ──
        [DllImport("libX11")] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11")] private static extern int XFlush(IntPtr dpy);
        [DllImport("libXext")] private static extern void XShapeCombineRectangles(
            IntPtr dpy, IntPtr win, int destKind, int xOff, int yOff, IntPtr rects, int nRects, int op, int ordering);
        private const int ShapeInput = 2, ShapeSet = 0, Unsorted = 0;

        private void TrySetClickThrough()
        {
            try
            {
                var hnd = TryGetPlatformHandle();
                if (hnd == null || hnd.Handle == IntPtr.Zero) { System.Diagnostics.Trace.WriteLine("[Overlay] no X11 handle"); return; }
                IntPtr dpy = XOpenDisplay(IntPtr.Zero);
                if (dpy == IntPtr.Zero) { System.Diagnostics.Trace.WriteLine("[Overlay] XOpenDisplay failed"); return; }
                XShapeCombineRectangles(dpy, hnd.Handle, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
                XFlush(dpy);
                System.Diagnostics.Trace.WriteLine($"[Overlay] click-through set on XID 0x{hnd.Handle.ToInt64():X}");
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Overlay] click-through failed: {ex.Message}"); }
        }
    }
}
