using System;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// P/Invoke for the RetroArch-style GL present path: an SDL3-owned window + GL context with a real
    /// vsync swap (SDL handles native-Wayland EGL or X11 GLX automatically), plus the GL 1.1 calls needed
    /// to upload a BGRA frame to a texture and draw it as a fullscreen quad. Mirrors RetroArch's gl1
    /// driver: one window, blocking SwapWindow as the only clock. See <see cref="GlPresenter"/>.
    /// </summary>
    public static class Gl
    {
        const string SDL = "SDL3";
        const string GL = "libGL.so.1";
        const string EGL = "libEGL.so.1";

        // EGL swap-interval, called DIRECTLY on the current display. RetroArch's wayland_ctx paces vsync via
        // egl_set_swap_interval -> eglSwapInterval(dpy, 1); on native Wayland that is what makes Mesa's FIFO
        // swap actually block to vblank. SDL_GL_SetSwapInterval may not set this on the Wayland EGL surface
        // (it has its own throttle path), so we force it ourselves to match RetroArch.
        // Which video backend SDL actually chose ("wayland" vs "x11"). RetroArch uses native "wayland";
        // if SDL picked "x11" we're on Xwayland/GLX, a different (worse) present path.
        [DllImport(SDL)] public static extern IntPtr SDL_GetCurrentVideoDriver();
        [DllImport(EGL)] public static extern IntPtr eglGetCurrentDisplay();
        [DllImport(EGL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool eglSwapInterval(IntPtr dpy, int interval);
        // Present DIRECTLY through EGL (RetroArch's egl_swap_buffers), bypassing SDL_GL_SwapWindow — SDL's
        // Wayland SwapWindow blocks on a wl_surface frame callback every frame, serializing us to one frame
        // in flight (full-vblank swap). Calling eglSwapBuffers ourselves lets Mesa's FIFO pipeline.
        public const int EGL_DRAW = 0x3059;
        [DllImport(EGL)] public static extern IntPtr eglGetCurrentSurface(int readdraw);
        [DllImport(EGL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool eglSwapBuffers(IntPtr dpy, IntPtr surface);

        // ---- SDL3 video + GL ----
        public const uint SDL_INIT_VIDEO = 0x00000020;
        public const ulong SDL_WINDOW_OPENGL = 0x0000000000000002UL;
        public const ulong SDL_WINDOW_RESIZABLE = 0x0000000000000020UL;
        public const ulong SDL_WINDOW_HIDDEN = 0x0000000000000008UL;
        // SDL_GLAttr
        public const int SDL_GL_DOUBLEBUFFER = 5, SDL_GL_CONTEXT_MAJOR_VERSION = 17, SDL_GL_CONTEXT_MINOR_VERSION = 18, SDL_GL_CONTEXT_PROFILE_MASK = 21;
        public const int SDL_GL_CONTEXT_PROFILE_COMPATIBILITY = 0x0002;

        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_InitSubSystem(uint flags);
        [DllImport(SDL)] public static extern void SDL_QuitSubSystem(uint flags);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GL_SetAttribute(int attr, int value);
        [DllImport(SDL)] public static extern IntPtr SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int w, int h, ulong flags);
        [DllImport(SDL)] public static extern void SDL_DestroyWindow(IntPtr window);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_RaiseWindow(IntPtr window);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_ShowWindow(IntPtr window);
        [DllImport(SDL)] public static extern IntPtr SDL_GL_CreateContext(IntPtr window);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GL_DestroyContext(IntPtr ctx);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GL_MakeCurrent(IntPtr window, IntPtr ctx);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GL_SetSwapInterval(int interval);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GL_GetSwapInterval(out int interval);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GL_SwapWindow(IntPtr window);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GetWindowSizeInPixels(IntPtr window, out int w, out int h);
        [DllImport(SDL)] public static extern ulong SDL_GetWindowFlags(IntPtr window);
        public const ulong SDL_WINDOW_INPUT_FOCUS = 0x0000000000000200UL;   // window has keyboard focus
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GetWindowPosition(IntPtr window, out int x, out int y);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_GetWindowSize(IntPtr window, out int w, out int h);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_SetWindowFullscreen(IntPtr window, [MarshalAs(UnmanagedType.I1)] bool fullscreen);
        [DllImport(SDL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SDL_PollEvent(byte[] ev);   // SDL_Event union; we only drain
        [DllImport(SDL)] public static extern IntPtr SDL_GetError();
        [DllImport(SDL)] public static extern uint SDL_GetWindowID(IntPtr window);

        // ---- GL 1.1 (BGRA texture → fullscreen quad, fixed-function; matches RetroArch gl1) ----
        public const uint GL_TEXTURE_2D = 0x0DE1, GL_RGBA = 0x1908, GL_RGBA8 = 0x8058, GL_BGRA = 0x80E1,
            GL_UNSIGNED_BYTE = 0x1401, GL_TEXTURE_MAG_FILTER = 0x2800, GL_TEXTURE_MIN_FILTER = 0x2801,
            GL_NEAREST = 0x2600, GL_LINEAR = 0x2601, GL_QUADS = 0x0007, GL_COLOR_BUFFER_BIT = 0x4000,
            GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F,
            GL_UNPACK_ALIGNMENT = 0x0CF5, GL_UNPACK_ROW_LENGTH = 0x0CF2;

        [DllImport(GL)] public static extern void glGenTextures(int n, out uint textures);
        [DllImport(GL)] public static extern void glDeleteTextures(int n, ref uint textures);
        [DllImport(GL)] public static extern void glBindTexture(uint target, uint texture);
        [DllImport(GL)] public static extern void glTexParameteri(uint target, uint pname, int param);
        [DllImport(GL)] public static extern void glPixelStorei(uint pname, int param);
        [DllImport(GL)] public static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr pixels);
        [DllImport(GL)] public static extern void glTexSubImage2D(uint target, int level, int xoff, int yoff, int w, int h, uint format, uint type, IntPtr pixels);
        [DllImport(GL)] public static extern void glViewport(int x, int y, int w, int h);
        [DllImport(GL)] public static extern void glClearColor(float r, float g, float b, float a);
        [DllImport(GL)] public static extern void glClear(uint mask);
        [DllImport(GL)] public static extern void glEnable(uint cap);
        [DllImport(GL)] public static extern void glBegin(uint mode);
        [DllImport(GL)] public static extern void glEnd();
        [DllImport(GL)] public static extern void glTexCoord2f(float s, float t);
        [DllImport(GL)] public static extern void glVertex2f(float x, float y);

        public static string? SdlError() => Marshal.PtrToStringUTF8(SDL_GetError());
    }
}
