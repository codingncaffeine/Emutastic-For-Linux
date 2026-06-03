using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// A borderless top-level X11 window dedicated to Vulkan present, floated over the Avalonia window's
    /// video viewport (the upstream Windows WS_POPUP/Vulkan-overlay model). This is the ONLY configuration
    /// that achieved clean vsync on KWin/Xwayland: a reparented child surface composites at ~28fps, but an
    /// independent top-level gets a proper present path (≈60 windowed-composited, and direct-scanout 60
    /// when fullscreen). Created and driven entirely on the emu present thread (its own X Display); the UI
    /// thread only feeds it a target screen rectangle + fullscreen flag.
    ///
    /// Borderless (no titlebar) via _MOTIF_WM_HINTS; kept above + off the taskbar via _NET_WM_STATE; never
    /// takes keyboard focus, so input continues to go to the Avalonia window. Fullscreen state is baked at
    /// creation (toggling recreates the window) to avoid EWMH ClientMessage plumbing.
    /// </summary>
    public sealed class VkOverlay : IDisposable
    {
        const string X = "libX11.so.6";
        [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(X)] static extern int XCloseDisplay(IntPtr dpy);
        [DllImport(X)] static extern IntPtr XDefaultRootWindow(IntPtr dpy);
        [DllImport(X)] static extern IntPtr XCreateSimpleWindow(IntPtr dpy, IntPtr parent, int x, int y, uint w, uint h, uint bw, IntPtr border, IntPtr bg);
        [DllImport(X)] static extern int XDestroyWindow(IntPtr dpy, IntPtr w);
        [DllImport(X)] static extern int XMapRaised(IntPtr dpy, IntPtr w);
        [DllImport(X)] static extern int XMoveResizeWindow(IntPtr dpy, IntPtr w, int x, int y, uint width, uint height);
        [DllImport(X)] static extern int XRaiseWindow(IntPtr dpy, IntPtr w);
        [DllImport(X)] static extern int XStoreName(IntPtr dpy, IntPtr w, [MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport(X)] static extern int XFlush(IntPtr dpy);
        [DllImport(X)] static extern int XSync(IntPtr dpy, int discard);
        [DllImport(X)] static extern int XPending(IntPtr dpy);
        [DllImport(X)] static extern int XNextEvent(IntPtr dpy, byte[] ev);
        [DllImport(X)] static extern IntPtr XInternAtom(IntPtr dpy, [MarshalAs(UnmanagedType.LPStr)] string name, bool onlyIfExists);
        [DllImport(X)] static extern int XChangeProperty(IntPtr dpy, IntPtr w, IntPtr prop, IntPtr type, int format, int mode, byte[] data, int n);
        [DllImport(X)] static extern int XSetWMHints(IntPtr dpy, IntPtr w, ref XWMHints hints);

        // XWMHints (LP64 layout). We only set flags=InputHint + input=False so the WM never gives the
        // overlay keyboard focus — keys keep flowing to the Avalonia window (game input stays alive).
        [StructLayout(LayoutKind.Sequential)]
        struct XWMHints
        {
            public long flags; public int input; public int initial_state;
            public IntPtr icon_pixmap, icon_window; public int icon_x, icon_y;
            public IntPtr icon_mask, window_group;
        }
        const long InputHint = 1L;

        const int PropModeReplace = 0;
        static readonly IntPtr XA_ATOM = (IntPtr)4;

        private IntPtr _dpy, _win;
        private VulkanPresenter? _presenter;
        private int _x, _y, _w, _h;
        private bool _fullscreen;
        private readonly byte[] _evBuf = new byte[256];

        public string? LastError { get; private set; }
        public bool Active => _presenter != null;
        public bool Fullscreen => _fullscreen;

        /// <summary>Create the overlay window at a screen rect (or fullscreen) and a Vulkan presenter on it.
        /// Returns false (with <see cref="LastError"/>) on any failure → caller falls back.</summary>
        public bool Create(int x, int y, int w, int h, bool fullscreen)
        {
            try
            {
                w = Math.Max(1, w); h = Math.Max(1, h);
                _dpy = XOpenDisplay(IntPtr.Zero);
                if (_dpy == IntPtr.Zero) { LastError = "XOpenDisplay failed"; return false; }
                IntPtr root = XDefaultRootWindow(_dpy);
                _win = XCreateSimpleWindow(_dpy, root, x, y, (uint)w, (uint)h, 0, IntPtr.Zero, IntPtr.Zero);
                if (_win == IntPtr.Zero) { LastError = "XCreateSimpleWindow failed"; return false; }
                XStoreName(_dpy, _win, "Emutastic — Game");

                SetBorderless();
                SetWmState(fullscreen);
                // Tell the WM we never want keyboard focus → keys keep going to the Avalonia window.
                var hints = new XWMHints { flags = InputHint, input = 0 };
                XSetWMHints(_dpy, _win, ref hints);

                XMoveResizeWindow(_dpy, _win, x, y, (uint)w, (uint)h);
                XMapRaised(_dpy, _win);
                XSync(_dpy, 0);

                _presenter = VulkanPresenter.TryCreate(_dpy, _win, w, h, out string? err);
                if (_presenter == null) { LastError = err; return false; }
                _x = x; _y = y; _w = w; _h = h; _fullscreen = fullscreen;
                Trace.WriteLine($"[VkOverlay] created {w}x{h} at ({x},{y}) fullscreen={fullscreen}");
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        // Remove window-manager decorations (titlebar/border): _MOTIF_WM_HINTS, flags=MWM_HINTS_DECORATIONS,
        // decorations=0. Layout: 5 × CARD32 (stored as 'long' for format-32 on LP64).
        private void SetBorderless()
        {
            IntPtr motif = XInternAtom(_dpy, "_MOTIF_WM_HINTS", false);
            var data = new byte[5 * 8];
            BitConverter.GetBytes(2L).CopyTo(data, 0);   // flags = MWM_HINTS_DECORATIONS
            // functions(8..) = 0, decorations(16..) = 0, input_mode(24..) = 0, status(32..) = 0
            XChangeProperty(_dpy, _win, motif, motif, 32, PropModeReplace, data, 5);
        }

        // _NET_WM_STATE = ABOVE + SKIP_TASKBAR + SKIP_PAGER (+ FULLSCREEN). Set before map.
        private void SetWmState(bool fullscreen)
        {
            IntPtr netState = XInternAtom(_dpy, "_NET_WM_STATE", false);
            var atoms = new System.Collections.Generic.List<long>
            {
                (long)XInternAtom(_dpy, "_NET_WM_STATE_ABOVE", false),
                (long)XInternAtom(_dpy, "_NET_WM_STATE_SKIP_TASKBAR", false),
                (long)XInternAtom(_dpy, "_NET_WM_STATE_SKIP_PAGER", false),
            };
            if (fullscreen) atoms.Add((long)XInternAtom(_dpy, "_NET_WM_STATE_FULLSCREEN", false));
            var data = new byte[atoms.Count * 8];
            for (int i = 0; i < atoms.Count; i++) BitConverter.GetBytes(atoms[i]).CopyTo(data, i * 8);
            XChangeProperty(_dpy, _win, netState, XA_ATOM, 32, PropModeReplace, data, atoms.Count);
        }

        /// <summary>Match the overlay to a new target rect / fullscreen state. Recreates the window+presenter
        /// if the fullscreen state changed; otherwise just moves/resizes (and resizes the swapchain).
        /// Returns false if a needed recreate failed (caller falls back). Call from the present thread.</summary>
        public bool Update(int x, int y, int w, int h, bool fullscreen, out bool recreated)
        {
            recreated = false;
            if (_presenter == null) return false;
            w = Math.Max(1, w); h = Math.Max(1, h);

            if (fullscreen != _fullscreen)   // fullscreen toggle → recreate (avoids EWMH ClientMessage)
            {
                Destroy();
                recreated = true;
                return Create(x, y, w, h, fullscreen);
            }
            if (!fullscreen && (x != _x || y != _y || w != _w || h != _h))
            {
                XMoveResizeWindow(_dpy, _win, x, y, (uint)w, (uint)h);
                XRaiseWindow(_dpy, _win);
                XFlush(_dpy);
                if (w != _w || h != _h) { try { _presenter.Resize(w, h); } catch (Exception ex) { Trace.WriteLine($"[VkOverlay] resize: {ex.Message}"); } }
                _x = x; _y = y; _w = w; _h = h;
            }
            return true;
        }

        /// <summary>Present one BGRA frame (blocks on vsync). Drains pending X events first so the WM/WSI
        /// connection stays healthy.</summary>
        public bool Present(byte[] bgra, int frameW, int frameH)
        {
            while (_dpy != IntPtr.Zero && XPending(_dpy) > 0) XNextEvent(_dpy, _evBuf);
            return _presenter?.Present(bgra, frameW, frameH) ?? false;
        }

        /// <summary>Block until the last present is actually on screen (lockstep pacing). No-op if
        /// present_wait isn't available.</summary>
        public void WaitForLastPresent() => _presenter?.WaitForLastPresent();
        public bool PresentWaitAvailable => _presenter?.PresentWaitAvailable ?? false;

        public void Destroy()
        {
            try { _presenter?.Dispose(); } catch { }
            _presenter = null;
            if (_win != IntPtr.Zero && _dpy != IntPtr.Zero) { try { XDestroyWindow(_dpy, _win); } catch { } _win = IntPtr.Zero; }
            if (_dpy != IntPtr.Zero) { try { XCloseDisplay(_dpy); } catch { } _dpy = IntPtr.Zero; }
        }

        public void Dispose() => Destroy();
    }
}
