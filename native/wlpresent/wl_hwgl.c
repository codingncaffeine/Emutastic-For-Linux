// Offscreen GL hardware-render context for 3D libretro cores (GameCube/Dolphin, PSP/PPSSPP,
// Dreamcast/Flycast, N64 via mupen64plus_next+GlideN64). Lives on the EMU thread, a SEPARATE,
// surfaceless EGL context from the display context in wl_present.c (different thread → no conflict).
// The core renders into our FBO; wlp_hw_readback glReadPixels it back to a BGRA frame (vertically
// flipped to top-down) that flows through the normal present path. Phase 1 = GL only (Vulkan = phase 2).
#include "wl_present.h"
#include <EGL/egl.h>
#include <GL/gl.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

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

typedef void (*fb_gen_t)(GLsizei, GLuint*);
typedef void (*fb_bind_t)(GLenum, GLuint);
typedef void (*fb_tex2d_t)(GLenum, GLenum, GLenum, GLuint, GLint);
typedef void (*rb_gen_t)(GLsizei, GLuint*);
typedef void (*rb_bind_t)(GLenum, GLuint);
typedef void (*rb_storage_t)(GLenum, GLenum, GLsizei, GLsizei);
typedef void (*fb_rb_t)(GLenum, GLenum, GLenum, GLuint);
typedef EGLDisplay (*get_plat_dpy_t)(EGLenum, void*, const EGLint*);

static struct {
    EGLDisplay dpy; EGLContext ctx; EGLConfig cfg;
    GLuint fbo, color, ds;
    int w, h, gles;
    unsigned char *rb; int rbcap;
    fb_gen_t GenFramebuffers; fb_bind_t BindFramebuffer; fb_tex2d_t FramebufferTexture2D;
    rb_gen_t GenRenderbuffers; rb_bind_t BindRenderbuffer; rb_storage_t RenderbufferStorage; fb_rb_t FramebufferRenderbuffer;
} H;

// ctx_type: 1=OPENGL(compat) 2=GLES2 3=OPENGL_CORE 4=GLES3 (6=VULKAN → not handled here). Returns 1 ok.
int wlp_hw_init(int ctx_type, int major, int minor, int want_depth, int want_stencil, int maxw, int maxh) {
    if (ctx_type == 6) return 0;
    H.gles = (ctx_type == 2 || ctx_type == 4);
    if (maxw < 1) maxw = 640; if (maxh < 1) maxh = 480;

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

    H.GenFramebuffers       = (fb_gen_t)    eglGetProcAddress("glGenFramebuffers");
    H.BindFramebuffer       = (fb_bind_t)   eglGetProcAddress("glBindFramebuffer");
    H.FramebufferTexture2D  = (fb_tex2d_t)  eglGetProcAddress("glFramebufferTexture2D");
    H.GenRenderbuffers      = (rb_gen_t)    eglGetProcAddress("glGenRenderbuffers");
    H.BindRenderbuffer      = (rb_bind_t)   eglGetProcAddress("glBindRenderbuffer");
    H.RenderbufferStorage   = (rb_storage_t)eglGetProcAddress("glRenderbufferStorage");
    H.FramebufferRenderbuffer = (fb_rb_t)   eglGetProcAddress("glFramebufferRenderbuffer");
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
    fprintf(stderr, "[wlp.hw] GL HW-render ctx type=%d %dx%d depth=%d fbo=%u\n", ctx_type, maxw, maxh, want_depth, H.fbo);
    return 1;
}

void wlp_hw_make_current(void) { if (H.ctx) eglMakeCurrent(H.dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, H.ctx); }
unsigned int wlp_hw_fbo(void) { return H.fbo; }
void* wlp_hw_proc(const char* sym) { return (void*)eglGetProcAddress(sym); }

// Read the bottom-left w*h of the FBO as BGRA. If bottom_left (core origin is GL bottom-left, the common
// case), flip rows so the output is top-down like the software present path expects.
int wlp_hw_readback(void* out, int w, int h, int bottom_left) {
    if (!H.ctx || !out || w <= 0 || h <= 0) return -1;
    if (w > H.w) w = H.w; if (h > H.h) h = H.h;
    if (H.BindFramebuffer) H.BindFramebuffer(GL_READ_FRAMEBUFFER, H.fbo);
    glPixelStorei(GL_PACK_ALIGNMENT, 4);
    int stride = w * 4, need = stride * h;
    unsigned char *o = (unsigned char*)out;
    if (bottom_left) {
        if (need > H.rbcap) { free(H.rb); H.rb = malloc(need); H.rbcap = need; }
        glReadPixels(0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, H.rb);
        for (int y = 0; y < h; y++) memcpy(o + y * stride, H.rb + (h - 1 - y) * stride, stride);
    } else {
        glReadPixels(0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, out);
    }
    // Force opaque alpha. The core's FBO alpha is undefined, and the window surface has an alpha channel
    // (for rounded corners) — non-255 alpha makes the game composite transparent / wash to white. (The
    // software present path likewise hard-sets alpha=255.)
    for (int i = 3; i < need; i += 4) o[i] = 0xFF;
    return 0;
}

void wlp_hw_destroy(void) {
    if (H.dpy) {
        eglMakeCurrent(H.dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (H.ctx) eglDestroyContext(H.dpy, H.ctx);
        eglTerminate(H.dpy);
    }
    free(H.rb);
    memset(&H, 0, sizeof(H));
}
