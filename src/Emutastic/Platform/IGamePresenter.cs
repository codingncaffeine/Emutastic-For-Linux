using System;

namespace Emutastic.Platform
{
    /// <summary>
    /// The surface the decoupled present/OSD loop drives. Two implementations:
    /// <see cref="WlToplevelPresenter"/> (Wayland — our own xdg_toplevel via the wlpresent shim,
    /// borderless with OSD-drawn chrome, deco layers, shader chain, displayed-res capture) and
    /// <see cref="GlPresenter"/> (X11/SDL fallback — native WM decorations, game + OSD quads only).
    /// Capability flags let the shared loop skip what a presenter can't do instead of forking the
    /// whole loop per backend (the fork is how X11 shipped with no in-game UI at all).
    /// All methods are present-thread-only unless noted (the GL context lives there).
    /// </summary>
    public interface IGamePresenter : IDisposable
    {
        // ── capabilities ───────────────────────────────────────────────────────────────────────
        /// <summary>OSD draws the window title bar + caption buttons (borderless shim). False =
        /// the windowing system provides real decorations; the loop suppresses OSD chrome.</summary>
        bool HasWindowChrome { get; }
        /// <summary>Bezel + per-game (Vectrex) overlay quads are supported.</summary>
        bool HasDecoLayers { get; }
        /// <summary>Built-in shader presets + downloaded .glslp chains are supported.</summary>
        bool HasShaderChain { get; }
        /// <summary>Displayed-resolution screenshot readback is supported (else the session falls
        /// back to the native-res published frame).</summary>
        bool HasCapture { get; }

        // ── state ──────────────────────────────────────────────────────────────────────────────
        bool CloseRequested { get; }
        double LastSwapMs { get; }
        bool IsMaximized { get; }
        int MouseX { get; }
        int MouseY { get; }
        bool MouseInside { get; }

        // ── events ─────────────────────────────────────────────────────────────────────────────
        event Action<int, bool>? KeyEvent;        // (SDL scancode, isDown)
        event Action? MouseMoved;
        event Action? MouseLeft;
        event Action<int, bool>? PointerButton;   // (0=left/1=right/2=mid, isDown)

        // ── frame + events ─────────────────────────────────────────────────────────────────────
        bool Present(byte[] bgra, int frameW, int frameH);
        void PumpEvents();
        void GetSize(out int w, out int h);

        // ── OSD overlay (window-sized straight-alpha RGBA8, row 0 = top; IntPtr.Zero hides) ─────
        void SetOverlay(IntPtr rgba, int w, int h);

        /// <summary>True if the presenter composites the pause effect on its own GPU layer
        /// (capped-res, GPU-stretched) instead of having it baked into the window-sized OSD —
        /// avoids re-rendering the whole OSD every frame while paused. Wayland shim only.</summary>
        bool SupportsFxLayer => false;
        /// <summary>Upload the capped pause-effect frame to the GPU fx layer, or IntPtr.Zero to
        /// clear it. No-op unless <see cref="SupportsFxLayer"/>. Present thread only.</summary>
        void SetFxOverlay(IntPtr rgba, int w, int h) { }
        /// <summary>Reserve top/bottom strips (title/status chrome) out of the game fit-rect.</summary>
        void SetInsets(int top, int bottom);
        void SetAspect(double dar);

        // ── window management (no-ops where the native WM owns the chrome) ─────────────────────
        void Minimize();
        void ToggleMaximize();
        void StartMove();
        void StartResize(int edge);
        void SetCursorShape(int shape);
        void SetFullscreen(bool fullscreen);

        // ── deco layers / shaders / capture (guard with the capability flags) ──────────────────
        void SetBezel(byte[] rgba, int w, int h);
        void ShowBezel(bool on);
        void SetGameOverlay(byte[] rgba, int w, int h);
        void ShowGameOverlay(bool on);
        void SetShader(int preset);
        bool SetGlslp(string? presetPath);
        void RequestCapture();
        bool TryTakeCapture(byte[] buf, out int w, out int h);
    }
}
