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
        // Buffer-age diagnostic (Phase 0.3): EGL_BUFFER_AGE_EXT tells how many frames ago the back buffer was
        // last the front — age cycling 1↔2 ≈ double-buffered; steady ≥3 ≈ triple+. Requires EGL_EXT_buffer_age.
        public const int EGL_EXTENSIONS = 0x3055;
        public const int EGL_BUFFER_AGE_EXT = 0x313D;
        [DllImport(EGL)] public static extern IntPtr eglQueryString(IntPtr dpy, int name);
        [DllImport(EGL)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool eglQuerySurface(IntPtr dpy, IntPtr surface, int attribute, out int value);

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
            GL_UNPACK_ALIGNMENT = 0x0CF5, GL_UNPACK_ROW_LENGTH = 0x0CF2,
            GL_BLEND = 0x0BE2, GL_SRC_ALPHA = 0x0302, GL_ONE_MINUS_SRC_ALPHA = 0x0303;

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
        [DllImport(GL)] public static extern void glBlendFunc(uint sfactor, uint dfactor);
        [DllImport(GL)] public static extern void glColor4f(float r, float g, float b, float a);

        public static string? SdlError() => Marshal.PtrToStringUTF8(SDL_GetError());

        // ---- GL 2.0+ (shader+VBO+PBO, RetroArch-lean draw). libGL.so.1 only exports ≤1.2 per the Linux GL
        // ABI, so these are loaded at runtime via SDL_GL_GetProcAddress (context must be current first).
        [DllImport(SDL)] public static extern IntPtr SDL_GL_GetProcAddress([MarshalAs(UnmanagedType.LPUTF8Str)] string proc);
        [DllImport(GL)] public static extern void glDisable(uint cap);
        [DllImport(GL)] public static extern void glScissor(int x, int y, int w, int h);   // 1.0 but unused before

        public const uint GL_SCISSOR_TEST = 0x0C11, GL_FRAGMENT_SHADER = 0x8B30, GL_VERTEX_SHADER = 0x8B31,
            GL_COMPILE_STATUS = 0x8B81, GL_LINK_STATUS = 0x8B82, GL_ARRAY_BUFFER = 0x8892,
            GL_PIXEL_UNPACK_BUFFER = 0x88EC, GL_STATIC_DRAW = 0x88E4, GL_STREAM_DRAW = 0x88E0,
            GL_FLOAT = 0x1406, GL_TRIANGLE_STRIP = 0x0005, GL_TEXTURE0 = 0x84C0, GL_WRITE_ONLY = 0x88B9;

        // Delegate signatures for the GL2 entry points we use.
        public delegate uint  D_glCreateShader(uint type);
        public delegate void  D_glShaderSource(uint shader, int count, string[] str, int[]? len);
        public delegate void  D_glCompileShader(uint shader);
        public delegate void  D_glGetShaderiv(uint shader, uint pname, out int p);
        public delegate void  D_glGetShaderInfoLog(uint shader, int max, out int len, byte[] log);
        public delegate uint  D_glCreateProgram();
        public delegate void  D_glAttachShader(uint prog, uint shader);
        public delegate void  D_glBindAttribLocation(uint prog, uint index, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        public delegate void  D_glLinkProgram(uint prog);
        public delegate void  D_glGetProgramiv(uint prog, uint pname, out int p);
        public delegate void  D_glUseProgram(uint prog);
        public delegate void  D_glGenBuffers(int n, out uint b);
        public delegate void  D_glBindBuffer(uint target, uint b);
        public delegate void  D_glBufferData(uint target, IntPtr size, IntPtr data, uint usage);
        public delegate IntPtr D_glMapBuffer(uint target, uint access);
        public delegate byte  D_glUnmapBuffer(uint target);
        public delegate void  D_glVertexAttribPointer(uint index, int size, uint type, byte norm, int stride, IntPtr offset);
        public delegate void  D_glEnableVertexAttribArray(uint index);
        public delegate int   D_glGetUniformLocation(uint prog, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        public delegate void  D_glUniform1i(int loc, int v);
        public delegate void  D_glActiveTexture(uint tex);
        public delegate void  D_glDrawArrays(uint mode, int first, int count);

        public static D_glCreateShader glCreateShader = null!;
        public static D_glShaderSource glShaderSource = null!;
        public static D_glCompileShader glCompileShader = null!;
        public static D_glGetShaderiv glGetShaderiv = null!;
        public static D_glGetShaderInfoLog glGetShaderInfoLog = null!;
        public static D_glCreateProgram glCreateProgram = null!;
        public static D_glAttachShader glAttachShader = null!;
        public static D_glBindAttribLocation glBindAttribLocation = null!;
        public static D_glLinkProgram glLinkProgram = null!;
        public static D_glGetProgramiv glGetProgramiv = null!;
        public static D_glUseProgram glUseProgram = null!;
        public static D_glGenBuffers glGenBuffers = null!;
        public static D_glBindBuffer glBindBuffer = null!;
        public static D_glBufferData glBufferData = null!;
        public static D_glMapBuffer glMapBuffer = null!;
        public static D_glUnmapBuffer glUnmapBuffer = null!;
        public static D_glVertexAttribPointer glVertexAttribPointer = null!;
        public static D_glEnableVertexAttribArray glEnableVertexAttribArray = null!;
        public static D_glGetUniformLocation glGetUniformLocation = null!;
        public static D_glUniform1i glUniform1i = null!;
        public static D_glActiveTexture glActiveTexture = null!;
        public static D_glDrawArrays glDrawArrays = null!;

        private static bool _gl2Loaded;
        /// <summary>Load the GL2 entry points via SDL_GL_GetProcAddress (context must be current). Returns
        /// false if any are missing — caller falls back to the fixed-function immediate-mode path.</summary>
        public static bool LoadGl2()
        {
            if (_gl2Loaded) return true;
            try
            {
                T Get<T>(string n) where T : Delegate
                {
                    IntPtr p = SDL_GL_GetProcAddress(n);
                    if (p == IntPtr.Zero) throw new EntryPointNotFoundException(n);
                    return Marshal.GetDelegateForFunctionPointer<T>(p);
                }
                glCreateShader = Get<D_glCreateShader>("glCreateShader");
                glShaderSource = Get<D_glShaderSource>("glShaderSource");
                glCompileShader = Get<D_glCompileShader>("glCompileShader");
                glGetShaderiv = Get<D_glGetShaderiv>("glGetShaderiv");
                glGetShaderInfoLog = Get<D_glGetShaderInfoLog>("glGetShaderInfoLog");
                glCreateProgram = Get<D_glCreateProgram>("glCreateProgram");
                glAttachShader = Get<D_glAttachShader>("glAttachShader");
                glBindAttribLocation = Get<D_glBindAttribLocation>("glBindAttribLocation");
                glLinkProgram = Get<D_glLinkProgram>("glLinkProgram");
                glGetProgramiv = Get<D_glGetProgramiv>("glGetProgramiv");
                glUseProgram = Get<D_glUseProgram>("glUseProgram");
                glGenBuffers = Get<D_glGenBuffers>("glGenBuffers");
                glBindBuffer = Get<D_glBindBuffer>("glBindBuffer");
                glBufferData = Get<D_glBufferData>("glBufferData");
                glMapBuffer = Get<D_glMapBuffer>("glMapBuffer");
                glUnmapBuffer = Get<D_glUnmapBuffer>("glUnmapBuffer");
                glVertexAttribPointer = Get<D_glVertexAttribPointer>("glVertexAttribPointer");
                glEnableVertexAttribArray = Get<D_glEnableVertexAttribArray>("glEnableVertexAttribArray");
                glGetUniformLocation = Get<D_glGetUniformLocation>("glGetUniformLocation");
                glUniform1i = Get<D_glUniform1i>("glUniform1i");
                glActiveTexture = Get<D_glActiveTexture>("glActiveTexture");
                glDrawArrays = Get<D_glDrawArrays>("glDrawArrays");
                _gl2Loaded = true;
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Gl] GL2 load failed: {ex.Message}"); return false; }
        }
    }
}
