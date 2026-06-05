// Own xdg_toplevel Wayland window + EGL (desktop GL 2.1) + immediate-mode textured-quad present.
// RetroArch's model: a bare top-level surface, plain eglSwapBuffers (Mesa FIFO @ interval 1) as the clock.
// Built into libwlpresent.so; flat ABI in wl_present.h.
#include "wl_present.h"
#include "xdg-shell-client-protocol.h"
#include "xdg-decoration-unstable-v1-client-protocol.h"
#include "cursor-shape-v1-client-protocol.h"
#include <wayland-client.h>
#include <wayland-egl.h>
#include <EGL/egl.h>
#include <GL/gl.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <math.h>
#include <unistd.h>

// GL 1.2+ tokens not always in <GL/gl.h>
#ifndef GL_BGRA
#define GL_BGRA 0x80E1
#endif
#ifndef GL_RGBA8
#define GL_RGBA8 0x8058
#endif
#ifndef GL_CLAMP_TO_EDGE
#define GL_CLAMP_TO_EDGE 0x812F
#endif
#ifndef EGL_PLATFORM_WAYLAND_KHR
#define EGL_PLATFORM_WAYLAND_KHR 0x31D8
#endif

#define CORNER_R 10   // rounded-window-corner radius (matches the main app's CornerRadius=10)

typedef struct {
    struct wl_display *dpy;
    struct wl_registry *reg;
    struct wl_compositor *comp;
    struct xdg_wm_base *wm;
    struct wl_surface *surf;
    struct xdg_surface *xsurf;
    struct xdg_toplevel *top;
    struct wl_egl_window *eglwin;
    EGLDisplay edpy; EGLContext ectx; EGLSurface esurf; EGLConfig ecfg;
    struct wl_seat *seat;
    struct wl_keyboard *kbd;
    struct wl_pointer *ptr;
    int ptr_x, ptr_y;          // last pointer position (window pixels)
    uint32_t ptr_serial;       // last pointer button serial (for interactive move/resize)
    uint32_t ptr_enter_serial; // last pointer-enter serial (for set_cursor / set_shape)
    struct wp_cursor_shape_manager_v1 *cshape_mgr;
    struct wp_cursor_shape_device_v1 *cshape_dev;
    int cur_shape;             // last applied cursor shape (avoid redundant set_shape)
    struct zxdg_decoration_manager_v1 *deco_mgr;
    struct zxdg_toplevel_decoration_v1 *deco;
    int maximized;             // tracked from xdg_toplevel.configure states
    int ins_top, ins_bottom;   // chrome insets (title bar / status bar) — game is fit BETWEEN them
    double dar;                // display aspect ratio to render at (0 = use the frame's pixel ratio)
    int w, h;
    int configured, closed;
    GLuint tex; int texw, texh;
    GLuint ov_tex; int ov_w, ov_h, ov_on;   // RGBA OSD overlay (FPS + HUD), composited over the game quad
    GLuint gov_tex; int gov_w, gov_h, gov_on;  // RGBA game overlay (Vectrex art) — stretched over the GAME rect
    GLuint bez_tex; int bez_w, bez_h, bez_on;  // RGBA bezel frame (The Bezel Project) — aspect-fit in the content area
    GLuint corner_tex;                       // quarter-circle alpha mask for rounding the 4 window corners
    // input event ring
    struct { int type, a, b; } evq[256];
    int evhead, evtail;
} wlp;

static void evq_push(wlp *s, int type, int a, int b) {
    int next = (s->evhead + 1) & 255;
    if (next == s->evtail) return;   // full → drop oldest-newest boundary (rare)
    s->evq[s->evhead].type = type; s->evq[s->evhead].a = a; s->evq[s->evhead].b = b;
    s->evhead = next;
}

static void set_opaque(wlp *s);   // defined below (used by top_configure)

// ---- listeners ----
static void reg_global(void *data, struct wl_registry *r, uint32_t name,
                       const char *iface, uint32_t ver) {
    wlp *s = data;
    if (!strcmp(iface, "wl_compositor"))
        s->comp = wl_registry_bind(r, name, &wl_compositor_interface, ver < 4 ? ver : 4);
    else if (!strcmp(iface, "xdg_wm_base"))
        s->wm = wl_registry_bind(r, name, &xdg_wm_base_interface, ver < 2 ? ver : 2);
    else if (!strcmp(iface, "wl_seat"))
        s->seat = wl_registry_bind(r, name, &wl_seat_interface, ver < 5 ? ver : 5);
    else if (!strcmp(iface, "zxdg_decoration_manager_v1"))
        s->deco_mgr = wl_registry_bind(r, name, &zxdg_decoration_manager_v1_interface, 1);
    else if (!strcmp(iface, "wp_cursor_shape_manager_v1"))
        s->cshape_mgr = wl_registry_bind(r, name, &wp_cursor_shape_manager_v1_interface, 1);
}
static void reg_global_remove(void *d, struct wl_registry *r, uint32_t n) {}
static const struct wl_registry_listener reg_listener = { reg_global, reg_global_remove };

static void wm_ping(void *d, struct xdg_wm_base *wm, uint32_t serial) { xdg_wm_base_pong(wm, serial); }
static const struct xdg_wm_base_listener wm_listener = { wm_ping };

static void xsurf_configure(void *data, struct xdg_surface *xs, uint32_t serial) {
    wlp *s = data;
    xdg_surface_ack_configure(xs, serial);
    s->configured = 1;
}
static const struct xdg_surface_listener xsurf_listener = { xsurf_configure };

static void top_configure(void *data, struct xdg_toplevel *t, int32_t w, int32_t h,
                          struct wl_array *states) {
    wlp *s = data;
    int max = 0;
    uint32_t *st;
    for (st = states->data; (const char*)st < (const char*)states->data + states->size; st++)
        if (*st == XDG_TOPLEVEL_STATE_MAXIMIZED) max = 1;
    s->maximized = max;
    if (w > 0 && h > 0 && (w != s->w || h != s->h)) {
        s->w = w; s->h = h;
        if (s->eglwin) wl_egl_window_resize(s->eglwin, w, h, 0, 0);
    }
    set_opaque(s);   // corners (un)rounded depending on maximized; region tracks the new size
}
static void top_close(void *data, struct xdg_toplevel *t) { wlp *s = data; s->closed = 1; evq_push(s, WLP_EV_CLOSE, 0, 0); }
static void top_configure_bounds(void *d, struct xdg_toplevel *t, int32_t w, int32_t h) {}
static void top_wm_capabilities(void *d, struct xdg_toplevel *t, struct wl_array *c) {}
static const struct xdg_toplevel_listener top_listener = {
    top_configure, top_close, top_configure_bounds, top_wm_capabilities
};

// ---- keyboard ----
static void kb_keymap(void *d, struct wl_keyboard *k, uint32_t fmt, int32_t fd, uint32_t sz) { if (fd >= 0) close(fd); }
static void kb_enter(void *d, struct wl_keyboard *k, uint32_t s, struct wl_surface *sf, struct wl_array *keys) {}
static void kb_leave(void *d, struct wl_keyboard *k, uint32_t s, struct wl_surface *sf) {}
static void kb_key(void *data, struct wl_keyboard *k, uint32_t serial, uint32_t time, uint32_t key, uint32_t state) {
    evq_push((wlp*)data, WLP_EV_KEY, (int)key, state == WL_KEYBOARD_KEY_STATE_PRESSED ? 1 : 0);
}
static void kb_mods(void *d, struct wl_keyboard *k, uint32_t s, uint32_t dep, uint32_t lat, uint32_t lck, uint32_t grp) {}
static void kb_repeat(void *d, struct wl_keyboard *k, int32_t rate, int32_t delay) {}
static const struct wl_keyboard_listener kb_listener = { kb_keymap, kb_enter, kb_leave, kb_key, kb_mods, kb_repeat };

// ---- pointer (HUD hover + click) ----
#ifndef BTN_LEFT
#define BTN_LEFT 0x110
#define BTN_RIGHT 0x111
#define BTN_MIDDLE 0x112
#endif
static void ptr_set(wlp *s, wl_fixed_t sx, wl_fixed_t sy) {
    s->ptr_x = wl_fixed_to_int(sx); s->ptr_y = wl_fixed_to_int(sy);
    evq_push(s, WLP_EV_MOUSE_MOVE, s->ptr_x, s->ptr_y);
}
static void ptr_enter(void *d, struct wl_pointer *p, uint32_t serial, struct wl_surface *sf, wl_fixed_t sx, wl_fixed_t sy) {
    wlp *s = d; s->ptr_enter_serial = serial; s->cur_shape = -1;   // re-apply shape after (re)entering
    ptr_set(s, sx, sy);
}
static void ptr_leave(void *d, struct wl_pointer *p, uint32_t serial, struct wl_surface *sf) { evq_push((wlp*)d, WLP_EV_MOUSE_LEAVE, 0, 0); }
static void ptr_motion(void *d, struct wl_pointer *p, uint32_t time, wl_fixed_t sx, wl_fixed_t sy) { ptr_set((wlp*)d, sx, sy); }
static void ptr_button(void *d, struct wl_pointer *p, uint32_t serial, uint32_t time, uint32_t button, uint32_t state) {
    wlp *s = d;
    s->ptr_serial = serial;   // remember for interactive move (xdg_toplevel.move needs an input serial)
    int b = button == BTN_RIGHT ? 1 : button == BTN_MIDDLE ? 2 : 0;
    evq_push(s, WLP_EV_MOUSE_MOVE, s->ptr_x, s->ptr_y);   // ensure C# has current coords at click time
    evq_push(s, WLP_EV_MOUSE_BTN, b, state ? 1 : 0);
}
static void ptr_axis(void *d, struct wl_pointer *p, uint32_t time, uint32_t axis, wl_fixed_t value) {}
static void ptr_frame(void *d, struct wl_pointer *p) {}
static void ptr_axis_source(void *d, struct wl_pointer *p, uint32_t src) {}
static void ptr_axis_stop(void *d, struct wl_pointer *p, uint32_t time, uint32_t axis) {}
static void ptr_axis_discrete(void *d, struct wl_pointer *p, uint32_t axis, int32_t discrete) {}
static const struct wl_pointer_listener ptr_listener = {
    .enter = ptr_enter, .leave = ptr_leave, .motion = ptr_motion, .button = ptr_button,
    .axis = ptr_axis, .frame = ptr_frame, .axis_source = ptr_axis_source,
    .axis_stop = ptr_axis_stop, .axis_discrete = ptr_axis_discrete,
};

static void seat_caps(void *data, struct wl_seat *seat, uint32_t caps) {
    wlp *s = data;
    if ((caps & WL_SEAT_CAPABILITY_KEYBOARD) && !s->kbd) {
        s->kbd = wl_seat_get_keyboard(seat);
        wl_keyboard_add_listener(s->kbd, &kb_listener, s);
    }
    if ((caps & WL_SEAT_CAPABILITY_POINTER) && !s->ptr) {
        s->ptr = wl_seat_get_pointer(seat);
        wl_pointer_add_listener(s->ptr, &ptr_listener, s);
        if (s->cshape_mgr) s->cshape_dev = wp_cursor_shape_manager_v1_get_pointer(s->cshape_mgr, s->ptr);
    }
}
static void seat_name(void *d, struct wl_seat *seat, const char *name) {}
static const struct wl_seat_listener seat_listener = { seat_caps, seat_name };

// Opaque region = everything EXCEPT the 4 rounded-corner squares, so KWin blends the (transparent)
// corners while still fast-pathing the opaque body. Maximized → fully opaque (square corners).
static void set_opaque(wlp *s) {
    if (!s->comp || !s->surf) return;
    struct wl_region *r = wl_compositor_create_region(s->comp);
    int R = CORNER_R, w = s->w, h = s->h;
    if (s->maximized || w <= 2 * R || h <= 2 * R) {
        wl_region_add(r, 0, 0, w, h);
    } else {
        wl_region_add(r, 0, R, w, h - 2 * R);     // middle band, full width
        wl_region_add(r, R, 0, w - 2 * R, R);     // top edge between the corners
        wl_region_add(r, R, h - R, w - 2 * R, R); // bottom edge between the corners
    }
    wl_surface_set_opaque_region(s->surf, r);
    wl_region_destroy(r);
}

// Build the quarter-circle alpha mask: alpha=1 OUTSIDE the corner arc (to be erased), 0 inside, AA'd.
static void make_corner_tex(wlp *s) {
    const int N = 64;
    unsigned char *m = malloc(N * N * 4);
    for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) {
        double dx = (i + 0.5) - N, dy = (j + 0.5) - N;      // distance from the inner corner (N,N)
        double dist = sqrt(dx * dx + dy * dy);
        double cov = dist - N + 0.5; if (cov < 0) cov = 0; if (cov > 1) cov = 1;  // 1 = erase
        unsigned char *p = &m[(j * N + i) * 4];
        p[0] = p[1] = p[2] = 0; p[3] = (unsigned char)(cov * 255.0 + 0.5);
    }
    glGenTextures(1, &s->corner_tex);
    glBindTexture(GL_TEXTURE_2D, s->corner_tex);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, N, N, 0, GL_RGBA, GL_UNSIGNED_BYTE, m);
    free(m);
}

// Erase the 4 window corners to transparent (dst *= 1-mask.alpha) so the window appears rounded.
static void draw_corners(wlp *s) {
    if (s->maximized || !s->corner_tex) return;
    int R = CORNER_R, w = s->w, h = s->h;
    glBindTexture(GL_TEXTURE_2D, s->corner_tex);
    glEnable(GL_BLEND);
    glBlendFunc(GL_ZERO, GL_ONE_MINUS_SRC_ALPHA);
    struct { int vx, vy; float t[8]; } cn[4] = {
        { 0,     h - R, {0,0, 1,0, 1,1, 0,1} },  // top-left
        { w - R, h - R, {1,0, 0,0, 0,1, 1,1} },  // top-right
        { 0,     0,     {0,1, 1,1, 1,0, 0,0} },  // bottom-left
        { w - R, 0,     {1,1, 0,1, 0,0, 1,0} },  // bottom-right
    };
    for (int k = 0; k < 4; k++) {
        glViewport(cn[k].vx, cn[k].vy, R, R);
        float *t = cn[k].t;
        glBegin(GL_QUADS);
            glTexCoord2f(t[0], t[1]); glVertex2f(-1,  1);
            glTexCoord2f(t[2], t[3]); glVertex2f( 1,  1);
            glTexCoord2f(t[4], t[5]); glVertex2f( 1, -1);
            glTexCoord2f(t[6], t[7]); glVertex2f(-1, -1);
        glEnd();
    }
    glDisable(GL_BLEND);
}

void* wlp_create(int w, int h, const char *title) {
    wlp *s = calloc(1, sizeof(wlp));
    if (!s) return NULL;
    s->w = w; s->h = h;
    s->dpy = wl_display_connect(NULL);
    if (!s->dpy) { fprintf(stderr, "[wlp] wl_display_connect failed (not Wayland?)\n"); free(s); return NULL; }
    s->reg = wl_display_get_registry(s->dpy);
    wl_registry_add_listener(s->reg, &reg_listener, s);
    wl_display_roundtrip(s->dpy);
    if (!s->comp || !s->wm) { fprintf(stderr, "[wlp] missing wl_compositor/xdg_wm_base\n"); wlp_destroy(s); return NULL; }
    xdg_wm_base_add_listener(s->wm, &wm_listener, s);
    if (s->seat) { wl_seat_add_listener(s->seat, &seat_listener, s); wl_display_roundtrip(s->dpy); }

    s->surf  = wl_compositor_create_surface(s->comp);
    s->xsurf = xdg_wm_base_get_xdg_surface(s->wm, s->surf);
    xdg_surface_add_listener(s->xsurf, &xsurf_listener, s);
    s->top   = xdg_surface_get_toplevel(s->xsurf);
    xdg_toplevel_add_listener(s->top, &top_listener, s);
    xdg_toplevel_set_title(s->top, title ? title : "Emutastic");
    xdg_toplevel_set_app_id(s->top, "Emutastic");

    // We draw our OWN themed title bar → ask for client-side decorations so the compositor (KWin)
    // doesn't also stack a server-side titlebar on top. If the manager is absent, we just get whatever
    // the compositor defaults to.
    if (s->deco_mgr) {
        s->deco = zxdg_decoration_manager_v1_get_toplevel_decoration(s->deco_mgr, s->top);
        zxdg_toplevel_decoration_v1_set_mode(s->deco, ZXDG_TOPLEVEL_DECORATION_V1_MODE_CLIENT_SIDE);
    }

    set_opaque(s);   // opaque body, transparent rounded corners (KWin fast-paths the opaque part)

    wl_surface_commit(s->surf);                 // initial commit, no buffer
    while (!s->configured) wl_display_dispatch(s->dpy);   // wait for first configure (acked in listener)

    // ---- EGL (desktop GL 2.1) ----
    s->edpy = eglGetDisplay((EGLNativeDisplayType)s->dpy);
    if (s->edpy == EGL_NO_DISPLAY || !eglInitialize(s->edpy, NULL, NULL)) {
        fprintf(stderr, "[wlp] eglInitialize failed\n"); wlp_destroy(s); return NULL;
    }
    if (!eglBindAPI(EGL_OPENGL_API)) { fprintf(stderr, "[wlp] eglBindAPI(GL) failed\n"); wlp_destroy(s); return NULL; }
    EGLint cfgattr[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_NONE
    };
    EGLint ncfg = 0;
    if (!eglChooseConfig(s->edpy, cfgattr, &s->ecfg, 1, &ncfg) || ncfg < 1) {
        fprintf(stderr, "[wlp] eglChooseConfig failed\n"); wlp_destroy(s); return NULL;
    }
    s->ectx = eglCreateContext(s->edpy, s->ecfg, EGL_NO_CONTEXT, NULL);
    if (s->ectx == EGL_NO_CONTEXT) { fprintf(stderr, "[wlp] eglCreateContext failed\n"); wlp_destroy(s); return NULL; }
    s->eglwin = wl_egl_window_create(s->surf, w, h);
    s->esurf  = eglCreateWindowSurface(s->edpy, s->ecfg, (EGLNativeWindowType)s->eglwin, NULL);
    if (s->esurf == EGL_NO_SURFACE) { fprintf(stderr, "[wlp] eglCreateWindowSurface failed\n"); wlp_destroy(s); return NULL; }
    eglMakeCurrent(s->edpy, s->esurf, s->esurf, s->ectx);
    eglSwapInterval(s->edpy, 1);

    glGenTextures(1, &s->tex);
    glBindTexture(GL_TEXTURE_2D, s->tex);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glEnable(GL_TEXTURE_2D);
    glClearColor(0, 0, 0, 1);
    make_corner_tex(s);
    fprintf(stderr, "[wlp] created own xdg_toplevel %dx%d, EGL GL ctx, vsync=1\n", w, h);
    return s;
}

int wlp_present(void *h, const void *bgra, int fw, int fh) {
    wlp *s = h;
    if (!s || s->closed || fw <= 0 || fh <= 0) return -1;
    wl_display_dispatch_pending(s->dpy);

    glBindTexture(GL_TEXTURE_2D, s->tex);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    if (fw != s->texw || fh != s->texh) {
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, fw, fh, 0, GL_BGRA, GL_UNSIGNED_BYTE, bgra);
        s->texw = fw; s->texh = fh;
    } else {
        glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, fw, fh, GL_BGRA, GL_UNSIGNED_BYTE, bgra);
    }

    glViewport(0, 0, s->w, s->h);
    glClear(GL_COLOR_BUFFER_BIT);
    // Fit the game into the area BETWEEN the chrome insets (title bar on top, status bar on bottom), so
    // the game is framed by the bars instead of being covered by them — matching the Windows layout.
    int availH = s->h - s->ins_top - s->ins_bottom; if (availH < 1) availH = s->h;
    // Render at the DISPLAY aspect ratio (stretch the framebuffer's pixels to it), not the raw pixel
    // ratio — consoles like CD-i output a frame whose pixel ratio differs from its 4:3 display aspect.
    // Fall back to the frame's pixel ratio when no DAR was set.
    double dar = s->dar > 0.0 ? s->dar : (double)fw / fh;
    int qw, qh;
    if ((double)s->w / availH > dar) { qh = availH; qw = (int)(availH * dar + 0.5); }   // area wider than DAR → bound by height
    else { qw = s->w; qh = (int)(s->w / dar + 0.5); }                                   // area taller → bound by width
    int qx = (s->w - qw) / 2;
    int qy = s->ins_bottom + (availH - qh) / 2;   // GL y is bottom-up: ins_bottom is the low edge
    glViewport(qx, qy, qw, qh);
    glBegin(GL_QUADS);
        glTexCoord2f(0, 0); glVertex2f(-1,  1);
        glTexCoord2f(1, 0); glVertex2f( 1,  1);
        glTexCoord2f(1, 1); glVertex2f( 1, -1);
        glTexCoord2f(0, 1); glVertex2f(-1, -1);
    glEnd();

    // Game overlay (Vectrex translucent art): stretched over the GAME rect exactly (upstream's
    // Stretch=Fill over GameLayer), alpha-blended. Static texture — uploaded once per game.
    if (s->gov_on && s->gov_tex) {
        glBindTexture(GL_TEXTURE_2D, s->gov_tex);
        glEnable(GL_BLEND);
        glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        glBegin(GL_QUADS);
            glTexCoord2f(0, 0); glVertex2f(-1,  1);
            glTexCoord2f(1, 0); glVertex2f( 1,  1);
            glTexCoord2f(1, 1); glVertex2f( 1, -1);
            glTexCoord2f(0, 1); glVertex2f(-1, -1);
        glEnd();
        glDisable(GL_BLEND);
    }

    // Bezel frame (The Bezel Project): aspect-fit at the BEZEL's own ratio in the content area
    // between the chrome insets, alpha-blended over the game (its transparent center is the game
    // window). C# forces the render DAR to the bezel AR while active, so the game lands in the
    // cutout. Static texture — uploaded once per game.
    if (s->bez_on && s->bez_tex && s->bez_w > 0 && s->bez_h > 0) {
        double bar = (double)s->bez_w / s->bez_h;
        int bw, bh;
        if ((double)s->w / availH > bar) { bh = availH; bw = (int)(availH * bar + 0.5); }
        else { bw = s->w; bh = (int)(s->w / bar + 0.5); }
        glViewport((s->w - bw) / 2, s->ins_bottom + (availH - bh) / 2, bw, bh);
        glBindTexture(GL_TEXTURE_2D, s->bez_tex);
        glEnable(GL_BLEND);
        glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        glBegin(GL_QUADS);
            glTexCoord2f(0, 0); glVertex2f(-1,  1);
            glTexCoord2f(1, 0); glVertex2f( 1,  1);
            glTexCoord2f(1, 1); glVertex2f( 1, -1);
            glTexCoord2f(0, 1); glVertex2f(-1, -1);
        glEnd();
        glDisable(GL_BLEND);
    }

    // OSD overlay (FPS + HUD): full-window RGBA quad, alpha-blended over the game.
    if (s->ov_on && s->ov_tex) {
        glViewport(0, 0, s->w, s->h);
        glBindTexture(GL_TEXTURE_2D, s->ov_tex);
        glEnable(GL_BLEND);
        glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        glBegin(GL_QUADS);
            glTexCoord2f(0, 0); glVertex2f(-1,  1);
            glTexCoord2f(1, 0); glVertex2f( 1,  1);
            glTexCoord2f(1, 1); glVertex2f( 1, -1);
            glTexCoord2f(0, 1); glVertex2f(-1, -1);
        glEnd();
        glDisable(GL_BLEND);
    }

    draw_corners(s);   // erase the 4 corners to transparent → rounded window (skipped when maximized)

    eglSwapBuffers(s->edpy, s->esurf);   // Mesa FIFO @ interval 1 = the clock
    wl_display_flush(s->dpy);
    return 0;
}

// Upload (or clear) the OSD overlay. rgba = window-sized straight-alpha RGBA8 (row 0 = top), or NULL/0 to
// hide. MUST be called on the present (GL) thread — the context is current there. Cheap when unchanged size.
void wlp_set_overlay(void *h, const void *rgba, int w, int hh) {
    wlp *s = h;
    if (!s) return;
    if (!rgba || w <= 0 || hh <= 0) { s->ov_on = 0; return; }
    if (!s->ov_tex) {
        glGenTextures(1, &s->ov_tex);
        glBindTexture(GL_TEXTURE_2D, s->ov_tex);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    }
    glBindTexture(GL_TEXTURE_2D, s->ov_tex);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    if (w != s->ov_w || hh != s->ov_h) {
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, hh, 0, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
        s->ov_w = w; s->ov_h = hh;
    } else {
        glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, hh, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
    }
    s->ov_on = 1;
}

// Shared upload for the two static decoration layers (game overlay / bezel). LINEAR filtering —
// these are photographic art scaled to the window, unlike the game's NEAREST pixels.
static void deco_upload(GLuint *tex, int *tw, int *th, int *on, const void *rgba, int w, int hh) {
    if (!rgba || w <= 0 || hh <= 0) { *on = 0; return; }
    if (!*tex) {
        glGenTextures(1, tex);
        glBindTexture(GL_TEXTURE_2D, *tex);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    }
    glBindTexture(GL_TEXTURE_2D, *tex);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    if (w != *tw || hh != *th) {
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, hh, 0, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
        *tw = w; *th = hh;
    } else {
        glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, hh, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
    }
    *on = 1;
}

// Upload (or clear with NULL) the Vectrex game overlay: straight-alpha RGBA8, stretched over the
// game rect each present. MUST be called on the present (GL) thread. Static — upload once per game.
void wlp_set_gameoverlay(void *h, const void *rgba, int w, int hh) {
    wlp *s = h; if (!s) return;
    deco_upload(&s->gov_tex, &s->gov_w, &s->gov_h, &s->gov_on, rgba, w, hh);
}

// Show/hide the already-uploaded game overlay without touching the texture (cog toggle).
void wlp_show_gameoverlay(void *h, int on) {
    wlp *s = h; if (s) s->gov_on = on && s->gov_tex ? 1 : 0;
}

// Upload (or clear with NULL) the bezel frame: straight-alpha RGBA8, aspect-fit in the content
// area each present. MUST be called on the present (GL) thread. Static — upload once per game.
void wlp_set_bezel(void *h, const void *rgba, int w, int hh) {
    wlp *s = h; if (!s) return;
    deco_upload(&s->bez_tex, &s->bez_w, &s->bez_h, &s->bez_on, rgba, w, hh);
}

// Show/hide the already-uploaded bezel without touching the texture (cog toggle).
void wlp_show_bezel(void *h, int on) {
    wlp *s = h; if (s) s->bez_on = on && s->bez_tex ? 1 : 0;
}

// Reserve chrome space (title bar / status bar heights, window pixels). The game is fit between them.
void wlp_set_insets(void *h, int top, int bottom) {
    wlp *s = h; if (!s) return;
    s->ins_top = top < 0 ? 0 : top; s->ins_bottom = bottom < 0 ? 0 : bottom;
}

// Set the display aspect ratio to render at (e.g. 4:3 = 1.3333). 0 = use the frame's pixel ratio.
void wlp_set_aspect(void *h, double dar) {
    wlp *s = h; if (s) s->dar = dar > 0.0 ? dar : 0.0;
}

void wlp_minimize(void *h) { wlp *s = h; if (s && s->top) xdg_toplevel_set_minimized(s->top); }

void wlp_toggle_maximize(void *h) {
    wlp *s = h; if (!s || !s->top) return;
    if (s->maximized) xdg_toplevel_unset_maximized(s->top);
    else xdg_toplevel_set_maximized(s->top);
}

int wlp_is_maximized(void *h) { wlp *s = h; return s ? s->maximized : 0; }

// Start an interactive move (drag the title bar). Uses the last pointer button serial.
void wlp_move(void *h) {
    wlp *s = h; if (s && s->top && s->seat) xdg_toplevel_move(s->top, s->seat, s->ptr_serial);
}

// Start an interactive resize from an edge/corner. `edge` = xdg_toplevel resize-edge bits
// (top=1, bottom=2, left=4, right=8; corners are the OR, e.g. top-left=5).
void wlp_resize(void *h, int edge) {
    wlp *s = h;
    if (s && s->top && s->seat && !s->maximized && edge) xdg_toplevel_resize(s->top, s->seat, s->ptr_serial, (uint32_t)edge);
}

// Set the pointer cursor to a wp_cursor_shape_device_v1 shape (e.g. resize arrows). No-op if the
// compositor lacks cursor-shape-v1, or the shape is unchanged. shape 0 = leave as-is.
void wlp_set_cursor_shape(void *h, int shape) {
    wlp *s = h;
    if (!s || !s->cshape_dev || !shape || shape == s->cur_shape || !s->ptr_enter_serial) return;
    s->cur_shape = shape;
    wp_cursor_shape_device_v1_set_shape(s->cshape_dev, s->ptr_enter_serial, (uint32_t)shape);
}

int wlp_poll(void *h) {
    wlp *s = h;
    if (!s) return -1;
    wl_display_dispatch_pending(s->dpy);
    return s->closed ? 1 : 0;
}

int wlp_poll_event(void *h, int *type, int *a, int *b) {
    wlp *s = h;
    if (!s || s->evtail == s->evhead) { if (type) *type = WLP_EV_NONE; return 0; }
    if (type) *type = s->evq[s->evtail].type;
    if (a) *a = s->evq[s->evtail].a;
    if (b) *b = s->evq[s->evtail].b;
    s->evtail = (s->evtail + 1) & 255;
    return 1;
}

void wlp_size(void *h, int *w, int *hh) {
    wlp *s = h;
    if (s) { if (w) *w = s->w; if (hh) *hh = s->h; }
}

void wlp_destroy(void *h) {
    wlp *s = h;
    if (!s) return;
    if (s->edpy != EGL_NO_DISPLAY) {
        eglMakeCurrent(s->edpy, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (s->tex) glDeleteTextures(1, &s->tex);
        if (s->ov_tex) glDeleteTextures(1, &s->ov_tex);
        if (s->corner_tex) glDeleteTextures(1, &s->corner_tex);
        if (s->esurf != EGL_NO_SURFACE) eglDestroySurface(s->edpy, s->esurf);
        if (s->ectx != EGL_NO_CONTEXT) eglDestroyContext(s->edpy, s->ectx);
        eglTerminate(s->edpy);
    }
    if (s->cshape_dev) wp_cursor_shape_device_v1_destroy(s->cshape_dev);
    if (s->eglwin) wl_egl_window_destroy(s->eglwin);
    if (s->deco) zxdg_toplevel_decoration_v1_destroy(s->deco);
    if (s->top) xdg_toplevel_destroy(s->top);
    if (s->xsurf) xdg_surface_destroy(s->xsurf);
    if (s->surf) wl_surface_destroy(s->surf);
    if (s->dpy) { wl_display_flush(s->dpy); wl_display_disconnect(s->dpy); }
    free(s);
}
