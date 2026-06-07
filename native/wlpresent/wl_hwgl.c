// Offscreen GL hardware-render context for 3D libretro cores (GameCube/Dolphin, PSP/PPSSPP,
// Dreamcast/Flycast, N64 via mupen64plus_next+GlideN64). Lives on the EMU thread, a SEPARATE,
// surfaceless EGL context from the display context in wl_present.c (different thread → no conflict).
// The core renders into our FBO; wlp_hw_readback glReadPixels it back to a BGRA frame (vertically
// flipped to top-down) that flows through the normal present path. Phase 1 = GL only (Vulkan = phase 2).
#include "wl_present.h"
#include <EGL/egl.h>
#include <GL/gl.h>
#include <GL/glx.h>     // GLX backend for GLEW-bootstrapped cores (PPSSPP) — via XWayland
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

#ifndef GL_BGRA
#define GL_BGRA 0x80E1
#endif
#ifndef GL_RGBA8
#define GL_RGBA8 0x8058
#endif
#ifndef EGL_PLATFORM_SURFACELESS_MESA
#define EGL_PLATFORM_SURFACELESS_MESA 0x31DD
#endif
#ifndef EGL_CONTEXT_MAJOR_VERSION
#define EGL_CONTEXT_MAJOR_VERSION 0x3098
#endif
#ifndef EGL_CONTEXT_MINOR_VERSION
#define EGL_CONTEXT_MINOR_VERSION 0x30FB
#endif
#ifndef EGL_CONTEXT_OPENGL_PROFILE_MASK
#define EGL_CONTEXT_OPENGL_PROFILE_MASK 0x30FD
#endif
#ifndef EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT
#define EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT 0x00000001
#endif
#ifndef EGL_OPENGL_ES3_BIT
#define EGL_OPENGL_ES3_BIT 0x00000040
#endif
// FBO tokens (GL 3.0 / ARB_framebuffer_object) — not in <GL/gl.h>
#define GL_FRAMEBUFFER 0x8D40
#define GL_READ_FRAMEBUFFER 0x8CA8
#define GL_COLOR_ATTACHMENT0 0x8CE0
#define GL_DEPTH_STENCIL_ATTACHMENT 0x821A
#define GL_DEPTH24_STENCIL8 0x88F0
#define GL_RENDERBUFFER 0x8D41
#define GL_PIXEL_PACK_BUFFER 0x88EB
#define GL_STREAM_READ 0x88E1
#define GL_READ_ONLY 0x88B8
// Sync objects (GL 3.2 / ARB_sync) — for the non-blocking readback ring
#define GL_SYNC_GPU_COMMANDS_COMPLETE 0x9117
#define GL_ALREADY_SIGNALED 0x911A
#define GL_TIMEOUT_EXPIRED 0x911B
#define GL_CONDITION_SATISFIED 0x911C

typedef void (*fb_gen_t)(GLsizei, GLuint*);
typedef void (*fb_bind_t)(GLenum, GLuint);
typedef void (*fb_tex2d_t)(GLenum, GLenum, GLenum, GLuint, GLint);
typedef void (*rb_gen_t)(GLsizei, GLuint*);
typedef void (*rb_bind_t)(GLenum, GLuint);
typedef void (*rb_storage_t)(GLenum, GLenum, GLsizei, GLsizei);
typedef void (*fb_rb_t)(GLenum, GLenum, GLenum, GLuint);
typedef EGLDisplay (*get_plat_dpy_t)(EGLenum, void*, const EGLint*);
typedef void (*gen_bufs_t)(GLsizei, GLuint*);
typedef void (*bind_buf_t)(GLenum, GLuint);
typedef void (*buf_data_t)(GLenum, long, const void*, GLenum);
typedef void* (*map_buf_t)(GLenum, GLenum);
typedef unsigned char (*unmap_buf_t)(GLenum);
typedef void* (*fence_sync_t)(GLenum, GLbitfield);
typedef GLenum (*client_wait_t)(void*, GLbitfield, unsigned long long);
typedef void (*del_sync_t)(void*);
typedef void (*blit_fb_t)(GLint, GLint, GLint, GLint, GLint, GLint, GLint, GLint, GLbitfield, GLenum);

#ifndef GL_DRAW_FRAMEBUFFER
#define GL_DRAW_FRAMEBUFFER 0x8CA9
#endif

static struct {
    EGLDisplay dpy; EGLContext ctx; EGLConfig cfg;
    GLuint fbo, color, ds;
    int w, h, gles;
    unsigned char *rb; int rbcap;
    fb_gen_t GenFramebuffers; fb_bind_t BindFramebuffer; fb_tex2d_t FramebufferTexture2D;
    rb_gen_t GenRenderbuffers; rb_bind_t BindRenderbuffer; rb_storage_t RenderbufferStorage; fb_rb_t FramebufferRenderbuffer;
    // Non-blocking readback ring (4 pixel-pack buffers + fence syncs): issue glReadPixels+fence
    // each frame; map only buffers whose fence has ALREADY signaled. The emu thread NEVER waits on
    // the GPU — critical on low GPU clocks (light N64 load keeps an AMD mobile chip at its minimum
    // DPM state, where one frame's render+readback latency exceeds 16.7ms; blocking there cost
    // 8-15ms/frame and capped the emu thread at ~43fps).
    gen_bufs_t GenBuffers; bind_buf_t BindBuffer; buf_data_t BufferData; map_buf_t MapBuffer; unmap_buf_t UnmapBuffer;
    fence_sync_t FenceSync; client_wait_t ClientWaitSync; del_sync_t DeleteSync;
#define WLP_PBO_RING 4
    GLuint pbo[WLP_PBO_RING]; int pbo_w[WLP_PBO_RING], pbo_h[WLP_PBO_RING];
    void *pbo_sync[WLP_PBO_RING];                 // NULL = slot free
    unsigned long long pbo_seq[WLP_PBO_RING], seq; // issue order, to pick the newest completed
    int pbo_ok;
    // GPU downscale-before-readback: when the core's FBO is much larger than the presented
    // window (3DS 8x internal res ⇒ ~43MB/frame), blit it into a window-bounded scale FBO
    // first and read THAT back — the mapped-PBO memcpy cost becomes window-sized regardless
    // of internal resolution. tgt_* is written from the present thread (plain int store; the
    // emu thread reads it on the next readback — staleness is harmless).
    blit_fb_t BlitFramebuffer;
    GLuint sfbo, stex; int sfbo_w, sfbo_h;
    volatile int tgt_w, tgt_h;
    // diagnostics: which device/driver the surfaceless context actually landed on (llvmpipe vs
    // real GPU flips readback cost ~1ms ↔ ~11ms), and where readback time goes (issue vs map).
    char info[256];
    double issue_ms, map_ms;   // EMA, same alpha as the C# hwReadback EMA
    double mapcall_ms, copy_ms; // map_ms split: MapBuffer call (GPU sync) vs pixel copy (memory speed)
} H;

static double now_ms(void) {
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

// ── GLX backend ──────────────────────────────────────────────────────────────
// PPSSPP bootstraps GL through GLEW, whose X11 build initializes its GLX function
// table inside glewInit() — on our surfaceless EGL context there is no GLX display
// and glewInit() fails outright ("[G3D] glewInit() failed."), regardless of GL
// version/profile. Windows never sees this (WGL legacy contexts are what GLEW
// expects); RetroArch's "gl" driver is GLX-backed for the same reason. So cores
// that ask for it (use_glx=1) get a legacy GLX context on a 64x64 pbuffer via
// XWayland instead — legacy GLX == compatibility profile with every extension
// visible, exactly the environment GLEW was designed for. The FBO/readback code
// below is identical for both backends.
static struct {
    Display *xdpy; GLXContext ctx; GLXPbuffer pbuf; int active;
} GX;

static int glx_create(int want_depth, int want_stencil) {
    GX.xdpy = XOpenDisplay(NULL);
    if (!GX.xdpy) { fprintf(stderr, "[wlp.hw] GLX: XOpenDisplay failed (no X/XWayland?)\n"); return 0; }
    int fbattr[] = {
        GLX_DRAWABLE_TYPE, GLX_PBUFFER_BIT,
        GLX_RENDER_TYPE, GLX_RGBA_BIT,
        GLX_RED_SIZE, 8, GLX_GREEN_SIZE, 8, GLX_BLUE_SIZE, 8, GLX_ALPHA_SIZE, 8,
        GLX_DEPTH_SIZE, want_depth ? 24 : 0, GLX_STENCIL_SIZE, want_stencil ? 8 : 0,
        None
    };
    int n = 0;
    GLXFBConfig *cfgs = glXChooseFBConfig(GX.xdpy, DefaultScreen(GX.xdpy), fbattr, &n);
    if (!cfgs || n < 1) { fprintf(stderr, "[wlp.hw] GLX: glXChooseFBConfig failed\n"); goto fail; }
    GLXFBConfig cfg = cfgs[0];
    XFree(cfgs);
    // Legacy create → compatibility profile, all extensions enumerable (what GLEW wants).
    GX.ctx = glXCreateNewContext(GX.xdpy, cfg, GLX_RGBA_TYPE, NULL, True);
    if (!GX.ctx) { fprintf(stderr, "[wlp.hw] GLX: glXCreateNewContext failed\n"); goto fail; }
    int pbattr[] = { GLX_PBUFFER_WIDTH, 64, GLX_PBUFFER_HEIGHT, 64, None };
    GX.pbuf = glXCreatePbuffer(GX.xdpy, cfg, pbattr);
    if (!GX.pbuf) { fprintf(stderr, "[wlp.hw] GLX: glXCreatePbuffer failed\n"); goto fail; }
    if (!glXMakeContextCurrent(GX.xdpy, GX.pbuf, GX.pbuf, GX.ctx)) {
        fprintf(stderr, "[wlp.hw] GLX: glXMakeContextCurrent failed\n"); goto fail;
    }
    GX.active = 1;
    return 1;
fail:
    if (GX.pbuf) { glXDestroyPbuffer(GX.xdpy, GX.pbuf); GX.pbuf = 0; }
    if (GX.ctx)  { glXDestroyContext(GX.xdpy, GX.ctx);  GX.ctx = NULL; }
    if (GX.xdpy) { XCloseDisplay(GX.xdpy); GX.xdpy = NULL; }
    return 0;
}

// Symbol lookup for whichever backend owns the context.
static void* hw_gpa(const char *sym) {
    if (GX.active) return (void*)glXGetProcAddressARB((const GLubyte*)sym);
    return (void*)eglGetProcAddress(sym);
}

// ctx_type: 1=OPENGL(compat) 2=GLES2 3=OPENGL_CORE 4=GLES3 (6=VULKAN → not handled here). Returns 1 ok.
int wlp_hw_init(int ctx_type, int major, int minor, int want_depth, int want_stencil, int maxw, int maxh, int use_glx) {
    if (ctx_type == 6) return 0;
    H.gles = (ctx_type == 2 || ctx_type == 4);
    if (maxw < 1) maxw = 640; if (maxh < 1) maxh = 480;

    // GLEW-bootstrapped cores need a GLX context (see glx_create). Falls back to EGL
    // (with a loud log line) when there's no X server to talk to.
    if (use_glx && !H.gles && glx_create(want_depth, want_stencil)) {
        fprintf(stderr, "[wlp.hw] using GLX backend (legacy/compat context via XWayland)\n");
    } else {
    if (use_glx) fprintf(stderr, "[wlp.hw] GLX requested but unavailable — falling back to EGL\n");
    get_plat_dpy_t getplat = (get_plat_dpy_t)eglGetProcAddress("eglGetPlatformDisplayEXT");
    if (getplat) H.dpy = getplat(EGL_PLATFORM_SURFACELESS_MESA, (void*)EGL_DEFAULT_DISPLAY, NULL);
    if (!H.dpy || H.dpy == EGL_NO_DISPLAY) H.dpy = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    if (H.dpy == EGL_NO_DISPLAY || !eglInitialize(H.dpy, NULL, NULL)) { fprintf(stderr, "[wlp.hw] eglInitialize failed\n"); return 0; }
    if (!eglBindAPI(H.gles ? EGL_OPENGL_ES_API : EGL_OPENGL_API)) { fprintf(stderr, "[wlp.hw] eglBindAPI failed\n"); return 0; }

    EGLint cfgattr[] = {
        EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
        EGL_RENDERABLE_TYPE, H.gles ? (ctx_type == 4 ? EGL_OPENGL_ES3_BIT : EGL_OPENGL_ES2_BIT) : EGL_OPENGL_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_DEPTH_SIZE, want_depth ? 24 : 0, EGL_STENCIL_SIZE, want_stencil ? 8 : 0,
        EGL_NONE
    };
    EGLint n = 0;
    if (!eglChooseConfig(H.dpy, cfgattr, &H.cfg, 1, &n) || n < 1) { fprintf(stderr, "[wlp.hw] eglChooseConfig failed\n"); return 0; }

    EGLint ca[16]; int ci = 0;
    if (major > 0) { ca[ci++] = EGL_CONTEXT_MAJOR_VERSION; ca[ci++] = major; ca[ci++] = EGL_CONTEXT_MINOR_VERSION; ca[ci++] = minor > 0 ? minor : 0; }
    if (ctx_type == 3) { ca[ci++] = EGL_CONTEXT_OPENGL_PROFILE_MASK; ca[ci++] = EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT; }
    ca[ci++] = EGL_NONE;
    H.ctx = eglCreateContext(H.dpy, H.cfg, EGL_NO_CONTEXT, ca);
    if (H.ctx == EGL_NO_CONTEXT) H.ctx = eglCreateContext(H.dpy, H.cfg, EGL_NO_CONTEXT, NULL); // let the driver pick
    if (H.ctx == EGL_NO_CONTEXT) { fprintf(stderr, "[wlp.hw] eglCreateContext failed\n"); return 0; }
    if (!eglMakeCurrent(H.dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, H.ctx)) { fprintf(stderr, "[wlp.hw] eglMakeCurrent(surfaceless) failed\n"); return 0; }
    }

    H.GenFramebuffers       = (fb_gen_t)    hw_gpa("glGenFramebuffers");
    H.BindFramebuffer       = (fb_bind_t)   hw_gpa("glBindFramebuffer");
    H.FramebufferTexture2D  = (fb_tex2d_t)  hw_gpa("glFramebufferTexture2D");
    H.GenRenderbuffers      = (rb_gen_t)    hw_gpa("glGenRenderbuffers");
    H.BindRenderbuffer      = (rb_bind_t)   hw_gpa("glBindRenderbuffer");
    H.RenderbufferStorage   = (rb_storage_t)hw_gpa("glRenderbufferStorage");
    H.FramebufferRenderbuffer = (fb_rb_t)   hw_gpa("glFramebufferRenderbuffer");
    if (!H.GenFramebuffers || !H.BindFramebuffer || !H.FramebufferTexture2D) { fprintf(stderr, "[wlp.hw] FBO entry points missing\n"); return 0; }

    H.w = maxw; H.h = maxh;
    H.GenFramebuffers(1, &H.fbo); H.BindFramebuffer(GL_FRAMEBUFFER, H.fbo);
    glGenTextures(1, &H.color); glBindTexture(GL_TEXTURE_2D, H.color);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexImage2D(GL_TEXTURE_2D, 0, H.gles ? GL_RGBA : GL_RGBA8, maxw, maxh, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    H.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, H.color, 0);
    if (want_depth || want_stencil) {
        H.GenRenderbuffers(1, &H.ds); H.BindRenderbuffer(GL_RENDERBUFFER, H.ds);
        H.RenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH24_STENCIL8, maxw, maxh);
        H.FramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_RENDERBUFFER, H.ds);
    }
    H.BindFramebuffer(GL_FRAMEBUFFER, H.fbo);
    glViewport(0, 0, maxw, maxh);

    // Async readback PBO ring. If any entry point is missing we fall back to synchronous glReadPixels.
    H.GenBuffers     = (gen_bufs_t)   hw_gpa("glGenBuffers");
    H.BindBuffer     = (bind_buf_t)   hw_gpa("glBindBuffer");
    H.BufferData     = (buf_data_t)   hw_gpa("glBufferData");
    H.MapBuffer      = (map_buf_t)    hw_gpa("glMapBuffer");
    H.UnmapBuffer    = (unmap_buf_t)  hw_gpa("glUnmapBuffer");
    H.FenceSync      = (fence_sync_t) hw_gpa("glFenceSync");
    H.ClientWaitSync = (client_wait_t)hw_gpa("glClientWaitSync");
    H.DeleteSync     = (del_sync_t)   hw_gpa("glDeleteSync");
    H.pbo_ok = H.GenBuffers && H.BindBuffer && H.BufferData && H.MapBuffer && H.UnmapBuffer
            && H.FenceSync && H.ClientWaitSync && H.DeleteSync;
    H.BlitFramebuffer = (blit_fb_t)hw_gpa("glBlitFramebuffer");   // missing → full-res readback
    if (H.pbo_ok) {
        H.GenBuffers(WLP_PBO_RING, H.pbo);
        for (int i = 0; i < WLP_PBO_RING; i++) {
            H.BindBuffer(GL_PIXEL_PACK_BUFFER, H.pbo[i]);
            H.BufferData(GL_PIXEL_PACK_BUFFER, (long)maxw * maxh * 4, NULL, GL_STREAM_READ);
            H.pbo_sync[i] = NULL;
        }
        H.BindBuffer(GL_PIXEL_PACK_BUFFER, 0);
    }
    // Capture the actual device/driver for the managed log (stderr is dropped when launched
    // from the app — this string is what tells fast-1ms (llvmpipe RAM copy) from slow-11ms
    // (real-GPU PCIe readback) sessions apart).
    const char *ren = (const char*)glGetString(GL_RENDERER), *ven = (const char*)glGetString(GL_VENDOR), *ver = (const char*)glGetString(GL_VERSION);
    snprintf(H.info, sizeof H.info, "renderer=%s vendor=%s version=%s asyncReadback=%d",
             ren ? ren : "?", ven ? ven : "?", ver ? ver : "?", H.pbo_ok);
    fprintf(stderr, "[wlp.hw] GL HW-render ctx type=%d %dx%d depth=%d fbo=%u %s\n", ctx_type, maxw, maxh, want_depth, H.fbo, H.info);
    return 1;
}

const char* wlp_hw_info(void) { return H.info; }
void wlp_hw_readback_times(double* issue, double* map) { if (issue) *issue = H.issue_ms; if (map) *map = H.map_ms; }
void wlp_hw_readback_times2(double* mapcall, double* copy) { if (mapcall) *mapcall = H.mapcall_ms; if (copy) *copy = H.copy_ms; }

// Present-target hint for downscale-before-readback (0,0 disables). Called from the present
// thread whenever the window size changes; plain stores, read by the emu thread next readback.
void wlp_hw_set_present_target(int w, int h) { H.tgt_w = w; H.tgt_h = h; }

void wlp_hw_make_current(void) {
    if (GX.active) { glXMakeContextCurrent(GX.xdpy, GX.pbuf, GX.pbuf, GX.ctx); return; }
    if (H.ctx) eglMakeCurrent(H.dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, H.ctx);
}
unsigned int wlp_hw_fbo(void) { return H.fbo; }
void* wlp_hw_proc(const char* sym) { return hw_gpa(sym); }

// Copy a mapped/raw BGRA source (bottom-up GL order) into out as top-down, forcing opaque alpha. The
// core's FBO alpha is undefined and the window surface has an alpha channel (rounded corners) → non-255
// alpha composites transparent / washes to white (the software present path likewise hard-sets 255).
static void copy_flip_opaque(unsigned char *o, const unsigned char *src, int w, int h, int bottom_left) {
    int stride = w * 4;
    if (bottom_left) for (int y = 0; y < h; y++) memcpy(o + y * stride, src + (h - 1 - y) * stride, stride);
    else            memcpy(o, src, stride * h);
    for (int i = 3, n = stride * h; i < n; i += 4) o[i] = 0xFF;
}

// Read the core's FBO back to BGRA (top-down). With async PBOs this ISSUES the read of the CURRENT frame
// (cur_w*cur_h, non-blocking) and returns the PREVIOUS frame's pixels (1-frame latency, no GPU stall) —
// matching RetroArch/Windows. out must hold up to the FBO's max size; *out_w/*out_h get the returned
// frame's dims (may differ from cur_* on an N64 mode switch). Returns 1 if out was filled, else 0.
int wlp_hw_readback(void* out, int cur_w, int cur_h, int bottom_left, int* out_w, int* out_h) {
    if ((!H.ctx && !GX.active) || !out || cur_w <= 0 || cur_h <= 0) return 0;   // a context exists in either backend
    if (cur_w > H.w) cur_w = H.w; if (cur_h > H.h) cur_h = H.h;
    if (H.BindFramebuffer) H.BindFramebuffer(GL_READ_FRAMEBUFFER, H.fbo);
    glPixelStorei(GL_PACK_ALIGNMENT, 4);
    unsigned char *o = (unsigned char*)out;

    if (!H.pbo_ok) {   // synchronous fallback (stalls — old behavior)
        int need = cur_w * cur_h * 4;
        if (need > H.rbcap) { free(H.rb); H.rb = malloc(need); H.rbcap = need; }
        glReadPixels(0, 0, cur_w, cur_h, GL_BGRA, GL_UNSIGNED_BYTE, H.rb);
        copy_flip_opaque(o, H.rb, cur_w, cur_h, bottom_left);
        if (out_w) *out_w = cur_w; if (out_h) *out_h = cur_h;
        return 1;
    }

    // NEVER-BLOCK ring: map only buffers whose fence has ALREADY signaled (zero-timeout check).
    // If the GPU is running behind (minimum DPM clocks make even a 640x480 frame's latency exceed
    // 16.7ms), we return the newest COMPLETED frame — or nothing — instead of waiting. The emu
    // thread keeps its 60Hz cadence no matter how late the GPU is; video lags 1-3 frames.
    double t0 = now_ms();
    int newest = -1;
    for (int i = 0; i < WLP_PBO_RING; i++) {
        if (!H.pbo_sync[i]) continue;
        GLenum st = H.ClientWaitSync(H.pbo_sync[i], 0, 0);
        if (st == GL_ALREADY_SIGNALED || st == GL_CONDITION_SATISFIED) {
            if (newest < 0 || H.pbo_seq[i] > H.pbo_seq[newest]) newest = i;
        }
    }
    int produced = 0;
    double t1 = t0;
    if (newest >= 0) {
        // Free every completed slot OLDER than the one we're returning (superseded frames).
        for (int i = 0; i < WLP_PBO_RING; i++) {
            if (i == newest || !H.pbo_sync[i] || H.pbo_seq[i] >= H.pbo_seq[newest]) continue;
            GLenum st = H.ClientWaitSync(H.pbo_sync[i], 0, 0);
            if (st == GL_ALREADY_SIGNALED || st == GL_CONDITION_SATISFIED) { H.DeleteSync(H.pbo_sync[i]); H.pbo_sync[i] = NULL; }
        }
        int tw = H.pbo_w[newest], th = H.pbo_h[newest];
        H.BindBuffer(GL_PIXEL_PACK_BUFFER, H.pbo[newest]);
        double tm0 = now_ms();
        unsigned char *src = (unsigned char*)H.MapBuffer(GL_PIXEL_PACK_BUFFER, GL_READ_ONLY);
        double tm1 = now_ms();
        if (src) {
            copy_flip_opaque(o, src, tw, th, bottom_left);
            double tm2 = now_ms();
            H.copy_ms += 0.05 * ((tm2 - tm1) - H.copy_ms);
            H.UnmapBuffer(GL_PIXEL_PACK_BUFFER);
            if (out_w) *out_w = tw; if (out_h) *out_h = th;
            produced = 1;
        }
        H.mapcall_ms += 0.05 * ((tm1 - tm0) - H.mapcall_ms);
        H.DeleteSync(H.pbo_sync[newest]); H.pbo_sync[newest] = NULL;
        t1 = now_ms();
    }

    // Issue the CURRENT frame's read into a free slot + fence + FLUSH (so the GPU starts now —
    // unflushed batches were the original 1ms↔11ms session lottery). All slots pending = GPU is
    // >3 frames behind: drop this frame's readback rather than stall.
    int free_slot = -1;
    for (int i = 0; i < WLP_PBO_RING; i++) if (!H.pbo_sync[i]) { free_slot = i; break; }
    if (free_slot >= 0) {
        // Downscale-before-readback: at high internal res the FBO dwarfs the window (3DS 8x =
        // ~43MB/frame → ~8ms of mapped-PBO memcpy on the emu thread). Blit (GPU, ~free) into a
        // window-bounded scale FBO and read THAT. Uniform scale preserves the frame's aspect;
        // the present path is dimension-agnostic (it gets rw/rh with the pixels). Only engages
        // when meaningfully smaller (s < 0.85) so ~1:1 cases keep the direct read.
        int rw = cur_w, rh = cur_h;
        if (H.BlitFramebuffer && H.tgt_w > 0 && H.tgt_h > 0) {
            double s = (double)H.tgt_w / cur_w;
            double sy = (double)H.tgt_h / cur_h;
            if (sy < s) s = sy;
            if (s < 0.85) {
                if (s < 0.05) s = 0.05;
                rw = (int)(cur_w * s + 0.5); rh = (int)(cur_h * s + 0.5);
                if (rw < 16) rw = 16; if (rh < 16) rh = 16;
                if (!H.sfbo) { H.GenFramebuffers(1, &H.sfbo); glGenTextures(1, &H.stex); }
                if (rw != H.sfbo_w || rh != H.sfbo_h) {
                    glBindTexture(GL_TEXTURE_2D, H.stex);
                    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, rw, rh, 0, GL_BGRA, GL_UNSIGNED_BYTE, NULL);
                    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                    glBindTexture(GL_TEXTURE_2D, 0);
                    H.BindFramebuffer(GL_DRAW_FRAMEBUFFER, H.sfbo);
                    H.FramebufferTexture2D(GL_DRAW_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, H.stex, 0);
                    H.sfbo_w = rw; H.sfbo_h = rh;
                } else {
                    H.BindFramebuffer(GL_DRAW_FRAMEBUFFER, H.sfbo);
                }
                H.BlitFramebuffer(0, 0, cur_w, cur_h, 0, 0, rw, rh, GL_COLOR_BUFFER_BIT, GL_LINEAR);
                H.BindFramebuffer(GL_READ_FRAMEBUFFER, H.sfbo);
                H.BindFramebuffer(GL_DRAW_FRAMEBUFFER, H.fbo);   // core expects its FBO bound for draw
            } else { rw = cur_w; rh = cur_h; }
        }
        H.BindBuffer(GL_PIXEL_PACK_BUFFER, H.pbo[free_slot]);
        glReadPixels(0, 0, rw, rh, GL_BGRA, GL_UNSIGNED_BYTE, 0);
        H.pbo_sync[free_slot] = H.FenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        glFlush();
        H.pbo_w[free_slot] = rw; H.pbo_h[free_slot] = rh; H.pbo_seq[free_slot] = ++H.seq;
        if (rw != cur_w) H.BindFramebuffer(GL_READ_FRAMEBUFFER, H.fbo);   // restore for the core
    }
    H.BindBuffer(GL_PIXEL_PACK_BUFFER, 0);
    // "map" = fence scan + map + copy of the completed frame; "issue" = readPixels + fence + flush.
    // EMA alpha matches the C# hwReadback EMA so the numbers line up.
    double t2 = now_ms();
    H.map_ms   += 0.05 * ((t1 - t0) - H.map_ms);
    H.issue_ms += 0.05 * ((t2 - t1) - H.issue_ms);
    return produced;
}

void wlp_hw_destroy(void) {
    if (GX.active) {
        glXMakeContextCurrent(GX.xdpy, None, None, NULL);
        if (GX.ctx)  glXDestroyContext(GX.xdpy, GX.ctx);
        if (GX.pbuf) glXDestroyPbuffer(GX.xdpy, GX.pbuf);
        XCloseDisplay(GX.xdpy);
        memset(&GX, 0, sizeof(GX));
    }
    else if (H.dpy) {
        eglMakeCurrent(H.dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (H.ctx) eglDestroyContext(H.dpy, H.ctx);
        eglTerminate(H.dpy);
    }
    free(H.rb);
    memset(&H, 0, sizeof(H));
}
