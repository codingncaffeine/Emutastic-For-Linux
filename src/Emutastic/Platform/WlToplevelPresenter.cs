using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// Presents through OUR OWN Wayland xdg_toplevel (via the native libwlpresent shim), instead of SDL's
    /// window — the fix for windowed sub-60: SDL's surface caps at ~55 (composited latch miss); a bare own
    /// top-level (RetroArch's model) hits ~60. SDL stays for gamepad + audio; this owns the game window +
    /// keyboard (wl_seat). Same surface members the decoupled present loop uses from GlPresenter, so it
    /// drops into PresentThreadProc behind EMUTASTIC_GL_TOPLEVEL.
    /// </summary>
    public sealed class WlToplevelPresenter : IGamePresenter
    {
        // Full-featured presenter: borderless shim window with OSD-drawn chrome plus all extras.
        public bool HasWindowChrome => true;
        public bool HasDecoLayers => true;
        public bool HasShaderChain => true;
        public bool HasCapture => true;

        const string LIB = "wlpresent";

        // Resolve libwlpresent.so: prefer alongside the app (production), fall back to the spike build dir
        // (dev). Registered once; harmless if the OS loader would have found it on LD_LIBRARY_PATH anyway.
        static WlToplevelPresenter()
        {
            NativeLibrary.SetDllImportResolver(typeof(WlToplevelPresenter).Assembly, (name, asm, search) =>
            {
                if (name != LIB) return IntPtr.Zero;
                foreach (var cand in new[]
                {
                    System.IO.Path.Combine(AppContext.BaseDirectory, "libwlpresent.so"),   // shipped beside the app (build copies it here)
                    System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "wlpresent", "libwlpresent.so"),  // dev: repo source tree
                })
                {
                    if (System.IO.File.Exists(cand) && NativeLibrary.TryLoad(cand, out var lib)) return lib;
                }
                return IntPtr.Zero;   // let the default loader try (LD_LIBRARY_PATH / system paths)
            });
        }

        [DllImport(LIB)] static extern IntPtr wlp_create(int w, int h, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        [DllImport(LIB)] static extern int wlp_present(IntPtr h, IntPtr bgra, int frameW, int frameH);
        [DllImport(LIB)] static extern int wlp_poll(IntPtr h);
        [DllImport(LIB)] static extern int wlp_poll_event(IntPtr h, out int type, out int a, out int b);
        [DllImport(LIB)] static extern void wlp_set_overlay(IntPtr h, IntPtr rgba, int w, int hh);
        [DllImport(LIB)] static extern void wlp_set_gameoverlay(IntPtr h, IntPtr rgba, int w, int hh);
        [DllImport(LIB)] static extern void wlp_set_fxoverlay(IntPtr h, IntPtr rgba, int w, int hh);
        [DllImport(LIB)] static extern void wlp_show_gameoverlay(IntPtr h, int on);
        [DllImport(LIB)] static extern void wlp_set_bezel(IntPtr h, IntPtr rgba, int w, int hh);
        [DllImport(LIB)] static extern void wlp_show_bezel(IntPtr h, int on);
        [DllImport(LIB)] static extern void wlp_set_shader(IntPtr h, int preset);
        [DllImport(LIB)] static extern int wlp_set_glslp(IntPtr h, [MarshalAs(UnmanagedType.LPUTF8Str)] string? presetPath);
        [DllImport(LIB)] static extern void wlp_request_capture(IntPtr h);
        [DllImport(LIB)] static extern int wlp_take_capture(IntPtr h, IntPtr outBuf, int maxBytes, out int w, out int hh);
        [DllImport(LIB)] static extern void wlp_set_insets(IntPtr h, int top, int bottom);
        [DllImport(LIB)] static extern void wlp_set_aspect(IntPtr h, double dar);
        [DllImport(LIB)] static extern void wlp_minimize(IntPtr h);
        [DllImport(LIB)] static extern void wlp_toggle_maximize(IntPtr h);
        [DllImport(LIB)] static extern int wlp_is_maximized(IntPtr h);
        [DllImport(LIB)] static extern void wlp_move(IntPtr h);
        [DllImport(LIB)] static extern void wlp_resize(IntPtr h, int edge);
        [DllImport(LIB)] static extern void wlp_set_cursor_shape(IntPtr h, int shape);
        [DllImport(LIB)] static extern void wlp_size(IntPtr h, out int w, out int hh);
        [DllImport(LIB)] static extern void wlp_destroy(IntPtr h);

        const int WLP_EV_KEY = 1, WLP_EV_MOUSE_MOVE = 2, WLP_EV_MOUSE_BTN = 3, WLP_EV_MOUSE_LEAVE = 4, WLP_EV_CLOSE = 5;

        private IntPtr _h;
        public string? LastError { get; private set; }
        public bool CloseRequested { get; private set; }
        public double LastSwapMs { get; private set; }
        public bool IsFocused => true;          // own top-level; KWin gives it the present path regardless
        public bool SelfPaced => true;          // FIFO swap in the shim is the clock — no stopwatch fallback
        public event Action<int, bool>? KeyEvent;   // (SDL scancode, isDown) — matches GlPresenter/OnGlKey
        public event Action? MouseMoved;
        public event Action? MouseLeft;
        public event Action<int, bool>? PointerButton;   // (button 0=left/1=right/2=mid, isDown) — for the HUD
        // Latest pointer position in WINDOW pixels + whether the pointer is over the window (HUD hit-testing).
        public int MouseX { get; private set; }
        public int MouseY { get; private set; }
        public bool MouseInside { get; private set; }

        // evdev keycode (linux/input-event-codes.h) → SDL scancode, for the keys EmulatorSession._glKeyMap +
        // the control keys use. Lets us reuse OnGlKey (which is keyed on SDL scancodes) unchanged.
        private static readonly Dictionary<int, int> _evdevToScancode = new()
        {
            {103,82},{108,81},{105,80},{106,79},   // Up/Down/Left/Right
            {44,29},{45,27},{30,4},{31,22},          // Z/X/A/S
            {28,40},{54,229},{16,20},{17,26},        // Enter/RShift/Q/W
            {1,41},{87,68},{25,19},                  // Esc/F11/P
            // Function row + PrintScreen — hotkeys (F5 quick-save, F7 quick-load, F9 record,
            // F12/PrintScreen screenshot, any configured F-key). evdev F1-F10 = 59-68, F12 = 88,
            // SYSRQ(PrtSc) = 99 → SDL 58-67 / 69 / 70.
            {59,58},{60,59},{61,60},{62,61},{63,62},{64,63},{65,64},{66,65},{67,66},{68,67},
            {88,69},{99,70},
        };

        public static WlToplevelPresenter? TryCreate(int w, int h, out string? error)
        {
            error = null;
            try
            {
                var p = new WlToplevelPresenter();
                p._h = wlp_create(w, h, "Emutastic");
                if (p._h == IntPtr.Zero) { error = "wlp_create returned null (not Wayland / missing globals / EGL fail)"; return null; }
                return p;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>Upload one BGRA frame and present it (vsync swap in the shim paces the loop).</summary>
        public unsafe bool Present(byte[] bgra, int frameW, int frameH)
        {
            if (_h == IntPtr.Zero || frameW <= 0 || frameH <= 0) return false;
            PumpEvents();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int rc;
            fixed (byte* p = bgra) rc = wlp_present(_h, (IntPtr)p, frameW, frameH);
            LastSwapMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return rc == 0;
        }

        /// <summary>Drain Wayland events (keyboard/mouse/close) without presenting. Emu/present thread.</summary>
        public void PumpEvents()
        {
            if (_h == IntPtr.Zero) return;
            if (wlp_poll(_h) == 1) CloseRequested = true;
            while (wlp_poll_event(_h, out int type, out int a, out int b) == 1)
            {
                switch (type)
                {
                    case WLP_EV_KEY:
                        if (_evdevToScancode.TryGetValue(a, out int sc)) KeyEvent?.Invoke(sc, b != 0);
                        break;
                    case WLP_EV_MOUSE_MOVE:  MouseX = a; MouseY = b; MouseInside = true; MouseMoved?.Invoke(); break;
                    case WLP_EV_MOUSE_BTN:   PointerButton?.Invoke(a, b != 0); break;
                    case WLP_EV_MOUSE_LEAVE: MouseInside = false; MouseLeft?.Invoke(); break;
                    case WLP_EV_CLOSE:       CloseRequested = true; break;
                }
            }
        }

        /// <summary>Hand the shim the OSD overlay (window-sized straight-alpha RGBA8, row 0 = top), or
        /// IntPtr.Zero to hide it. MUST be called on the present thread (the GL context lives there).</summary>
        public void SetOverlay(IntPtr rgba, int w, int h)
        {
            if (_h != IntPtr.Zero) wlp_set_overlay(_h, rgba, w, h);
        }

        /// <summary>Upload the Vectrex game overlay (straight-alpha RGBA8, stretched over the game rect),
        /// then show/hide via <see cref="ShowGameOverlay"/>. Present thread only (GL context).</summary>
        public unsafe void SetGameOverlay(byte[] rgba, int w, int h)
        {
            if (_h == IntPtr.Zero) return;
            fixed (byte* p = rgba) wlp_set_gameoverlay(_h, (IntPtr)p, w, h);
        }
        public void ShowGameOverlay(bool on) { if (_h != IntPtr.Zero) wlp_show_gameoverlay(_h, on ? 1 : 0); }

        public bool SupportsFxLayer => true;

        /// <summary>Upload the pause-effect layer (capped-res straight-alpha RGBA8, GPU-stretched to the
        /// whole window), or clear it with a null pointer on resume. Present thread only (GL context).</summary>
        public void SetFxOverlay(IntPtr rgba, int w, int h)
        {
            if (_h != IntPtr.Zero) wlp_set_fxoverlay(_h, rgba, w, h);
        }

        /// <summary>Upload the bezel frame (straight-alpha RGBA8, aspect-fit in the content area),
        /// then show/hide via <see cref="ShowBezel"/>. Present thread only (GL context).</summary>
        public unsafe void SetBezel(byte[] rgba, int w, int h)
        {
            if (_h == IntPtr.Zero) return;
            fixed (byte* p = rgba) wlp_set_bezel(_h, (IntPtr)p, w, h);
        }
        public void ShowBezel(bool on) { if (_h != IntPtr.Zero) wlp_show_bezel(_h, on ? 1 : 0); }

        /// <summary>Built-in shader preset on the game quad (0=None … 6=Smooth). Present thread only.
        /// Picking a built-in clears any downloaded chain.</summary>
        public void SetShader(int preset) { if (_h != IntPtr.Zero) wlp_set_shader(_h, preset); }

        /// <summary>Activate a downloaded .glslp chain (null clears). False = failed to load
        /// (plain quad stays). Present thread only — compiles every pass.</summary>
        public bool SetGlslp(string? presetPath)
            => _h != IntPtr.Zero && wlp_set_glslp(_h, presetPath) == 1;

        /// <summary>Arm a one-shot displayed-frame capture; the next Present fills it (pre-OSD).</summary>
        public void RequestCapture() { if (_h != IntPtr.Zero) wlp_request_capture(_h); }

        /// <summary>Collect a finished capture into buf (BGRA top-down). True once ready.</summary>
        public unsafe bool TryTakeCapture(byte[] buf, out int w, out int h)
        {
            w = h = 0;
            if (_h == IntPtr.Zero) return false;
            fixed (byte* p = buf) return wlp_take_capture(_h, (IntPtr)p, buf.Length, out w, out h) == 1;
        }

        public void GetSize(out int w, out int h)
        {
            w = 0; h = 0;
            if (_h != IntPtr.Zero) wlp_size(_h, out w, out h);
        }

        /// <summary>Reserve title-bar (top) + status-bar (bottom) chrome; the game is fit between them.</summary>
        public void SetInsets(int top, int bottom) { if (_h != IntPtr.Zero) wlp_set_insets(_h, top, bottom); }
        /// <summary>Display aspect ratio to render at (4:3 = 1.333…); 0 = use the frame's pixel ratio.</summary>
        public void SetAspect(double dar) { if (_h != IntPtr.Zero) wlp_set_aspect(_h, dar); }
        public void Minimize()       { if (_h != IntPtr.Zero) wlp_minimize(_h); }
        public void ToggleMaximize() { if (_h != IntPtr.Zero) wlp_toggle_maximize(_h); }
        public bool IsMaximized => _h != IntPtr.Zero && wlp_is_maximized(_h) != 0;
        public void StartMove()      { if (_h != IntPtr.Zero) wlp_move(_h); }
        public void StartResize(int edge) { if (_h != IntPtr.Zero) wlp_resize(_h, edge); }
        public void SetCursorShape(int shape) { if (_h != IntPtr.Zero) wlp_set_cursor_shape(_h, shape); }

        public void SetFullscreen(bool fullscreen) { /* TODO: xdg_toplevel.set_fullscreen via shim */ }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { try { wlp_destroy(_h); } catch { } _h = IntPtr.Zero; }
        }
    }
}
