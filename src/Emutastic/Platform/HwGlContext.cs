using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// Offscreen GL hardware-render context for 3D libretro cores (GameCube/PSP/Dreamcast/N64-GL),
    /// backed by libwlpresent's wl_hwgl.c. A single global context on the emu thread; the core renders
    /// into its FBO and we read it back to a BGRA frame for the normal present path. Phase 1 = GL only.
    /// </summary>
    public static class HwGlContext
    {
        const string LIB = "wlpresent";

        static HwGlContext()
        {
            // Ensure WlToplevelPresenter's DllImport resolver for "wlpresent" is registered before any
            // P/Invoke here (it may run before the present thread that normally touches that type).
            RuntimeHelpers.RunClassConstructor(typeof(WlToplevelPresenter).TypeHandle);
        }

        [DllImport(LIB)] static extern int wlp_hw_init(int ctxType, int major, int minor, int wantDepth, int wantStencil, int maxw, int maxh, int useGlx);
        [DllImport(LIB)] static extern void wlp_hw_make_current();
        [DllImport(LIB)] static extern uint wlp_hw_fbo();
        [DllImport(LIB)] static extern IntPtr wlp_hw_proc([MarshalAs(UnmanagedType.LPStr)] string sym);
        [DllImport(LIB)] static extern int wlp_hw_readback(IntPtr outBgra, int curW, int curH, int bottomLeft, out int outW, out int outH);
        [DllImport(LIB)] static extern void wlp_hw_destroy();
        [DllImport(LIB)] static extern IntPtr wlp_hw_info();
        [DllImport(LIB)] static extern void wlp_hw_readback_times(out double issueMs, out double mapMs);
        [DllImport(LIB)] static extern void wlp_hw_readback_times2(out double mapcallMs, out double copyMs);
        [DllImport(LIB)] static extern void wlp_hw_set_present_target(int w, int h);

        /// <summary>Downscale-before-readback hint: when the core's FBO is much larger than the
        /// presented window, the native side blits it down to (at most) this size before the PBO
        /// read, making the per-frame memcpy window-sized regardless of internal resolution.
        /// (0,0) disables. Safe from any thread (plain int stores).</summary>
        public static void SetPresentTarget(int w, int h) => wlp_hw_set_present_target(w, h);

        /// <summary>Create the offscreen GL context + FBO (call on the emu thread, after retro_load_game).
        /// useGlx: create the context via GLX/XWayland instead of surfaceless EGL — required by
        /// GLEW-bootstrapped cores (PPSSPP) whose glewInit() needs a GLX display.</summary>
        public static bool Init(int ctxType, int major, int minor, bool depth, bool stencil, int maxW, int maxH, bool useGlx = false)
            => wlp_hw_init(ctxType, major, minor, depth ? 1 : 0, stencil ? 1 : 0, maxW, maxH, useGlx ? 1 : 0) != 0;

        public static void MakeCurrent() => wlp_hw_make_current();
        public static uint Fbo() => wlp_hw_fbo();
        public static IntPtr Proc(string sym) => wlp_hw_proc(sym);
        public static void Destroy() => wlp_hw_destroy();

        /// <summary>GL_RENDERER/vendor/version + asyncReadback flag — which device the surfaceless
        /// context actually landed on (llvmpipe ↔ real GPU flips readback cost ~1ms ↔ ~11ms).</summary>
        public static string Info() => Marshal.PtrToStringAnsi(wlp_hw_info()) ?? "";

        /// <summary>Readback EMA split: issue = glReadPixels enqueue, map = MapBuffer wait + copy,
        /// further split into mapcall (GPU sync) vs copy (mapped-memory read speed).</summary>
        public static (double issueMs, double mapMs, double mapcallMs, double copyMs) ReadbackTimes()
        {
            wlp_hw_readback_times(out double i, out double m);
            wlp_hw_readback_times2(out double mc, out double cp);
            return (i, m, mc, cp);
        }

        /// <summary>Issue the current FBO read and return the PREVIOUS frame's pixels into <paramref name="bgra"/>
        /// (async PBO; 1-frame latency, no GPU stall). <paramref name="bgra"/> must be FBO-max-sized.
        /// Returns true and sets outW/outH when a frame was produced. Emu thread, context current.</summary>
        public static unsafe bool Readback(byte[] bgra, int curW, int curH, bool bottomLeftOrigin, out int outW, out int outH)
        {
            fixed (byte* p = bgra) return wlp_hw_readback((IntPtr)p, curW, curH, bottomLeftOrigin ? 1 : 0, out outW, out outH) != 0;
        }
    }
}
