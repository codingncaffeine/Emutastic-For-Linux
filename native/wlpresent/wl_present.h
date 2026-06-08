// Flat C ABI for an own Wayland xdg_toplevel + EGL + GL presenter (RetroArch's model).
// C# (or the test harness) P/Invokes these; all Wayland/xdg/EGL/GL lives in C.
#ifndef WL_PRESENT_H
#define WL_PRESENT_H
#ifdef __cplusplus
extern "C" {
#endif

// Create an own top-level Wayland window (w x h), EGL GL2.1 context, vsync (interval 1).
// Returns an opaque handle, or NULL on failure (not Wayland, missing globals, EGL fail).
void* wlp_create(int w, int h, const char* title);

// Upload a BGRA8888 frame (frameW x frameH) and present it aspect-fit + vsync swap.
// Returns 0 on success, <0 on error/closed.
int wlp_present(void* h, const void* bgra, int frameW, int frameH);

// Pump pending Wayland events (configure/ping/close/resize). Returns 1 if close requested, else 0.
int wlp_poll(void* h);

// Input event types (dequeued via wlp_poll_event).
#define WLP_EV_NONE        0
#define WLP_EV_KEY         1   // a = evdev keycode, b = 1 down / 0 up
#define WLP_EV_MOUSE_MOVE  2   // a = x, b = y (window pixels)
#define WLP_EV_MOUSE_BTN   3   // a = button (0=left,1=right,2=mid), b = 1 down / 0 up
#define WLP_EV_MOUSE_LEAVE 4
#define WLP_EV_CLOSE       5

// Dequeue one input event into *type/*a/*b. Returns 1 if an event was returned, 0 if the queue is empty.
int wlp_poll_event(void* h, int* type, int* a, int* b);

// Set (or clear) the OSD overlay drawn on top of the game: a window-sized straight-alpha RGBA8 buffer
// (row 0 = top). Pass NULL / w<=0 to hide. MUST be called on the same thread that calls wlp_present
// (the GL context lives there). Composited each present until changed.
void wlp_set_overlay(void* h, const void* rgba, int w, int hh);

// Static decoration layers (uploaded once per game; drawn every present until hidden).
// Both take straight-alpha RGBA8 (row 0 = top) and MUST be called on the present (GL) thread.
// Game overlay (Vectrex art): stretched over the game rect, alpha-blended. NULL clears.
void wlp_set_gameoverlay(void* h, const void* rgba, int w, int hh);
void wlp_set_fxoverlay(void* h, const void* rgba, int w, int hh);   // pause-effect layer (capped, GPU-stretched)
void wlp_show_gameoverlay(void* h, int on);   // toggle visibility without re-uploading
// Bezel frame (transparent-center art): aspect-fit at its own ratio in the content area. NULL clears.
void wlp_set_bezel(void* h, const void* rgba, int w, int hh);
void wlp_show_bezel(void* h, int on);         // toggle visibility without re-uploading

// Built-in shader preset on the game quad (cog "Shader: …"): 0=None, 1=CRT Scanlines, 2=GB DMG,
// 3=GB DMG LCD, 4=GB Pocket, 5=LCD Grid, 6=Smooth (linear filtering). Present-thread only.
// Picking a built-in clears any downloaded .glslp chain.
void wlp_set_shader(void* h, int preset);

// Downloaded .glslp preset (libretro shaders_glsl pack): compiles + activates the multi-pass
// chain (overrides the built-in preset). NULL clears. Returns 1 ok / 0 failed (falls back to the
// plain quad). Present-thread only.
int wlp_set_glslp(void* h, const char* presetPath);

// One-shot displayed-frame capture (screenshots): arm, then the NEXT wlp_present reads the game/
// bezel rect back (BGRA top-down, pre-OSD = no HUD). Collect with wlp_take_capture — returns 1 and
// the dims once ready (0 if pending or out too small), clearing the slot. Present-thread only.
void wlp_request_capture(void* h);
int  wlp_take_capture(void* h, void* out, int max_bytes, int* w, int* hh);

// Reserve chrome space (title-bar height on top, status-bar height on bottom, window pixels). The game
// is aspect-fit BETWEEN them instead of being covered by the OSD bars. Call once after create / on resize.
void wlp_set_insets(void* h, int top, int bottom);

// Display aspect ratio to render the game at (4:3 = 1.3333…). 0 = use the frame's raw pixel ratio.
void wlp_set_aspect(void* h, double dar);

// Window management (driven by the themed title-bar buttons).
void wlp_minimize(void* h);
void wlp_toggle_maximize(void* h);
int  wlp_is_maximized(void* h);
void wlp_move(void* h);              // start an interactive move (title-bar drag)
void wlp_resize(void* h, int edge);  // start an interactive resize (edge bits: T=1,B=2,L=4,R=8; corners OR'd)
void wlp_set_cursor_shape(void* h, int shape);  // wp_cursor_shape_device_v1 shape enum (resize arrows etc.)

// ── GL hardware-render (3D cores) — offscreen, emu-thread context; see wl_hwgl.c ──
// ctx_type: 1=OPENGL 2=GLES2 3=OPENGL_CORE 4=GLES3 (6=VULKAN not handled here). Returns 1 ok / 0 fail.
int  wlp_hw_init(int ctx_type, int major, int minor, int want_depth, int want_stencil, int maxw, int maxh, int use_glx);
void wlp_hw_make_current(void);              // make the HW context current on the calling (emu) thread
unsigned int wlp_hw_fbo(void);               // FBO id the core renders into (for get_current_framebuffer)
void* wlp_hw_proc(const char* sym);          // resolve a GL symbol (for get_proc_address)
// FBO → BGRA top-down into out (async via PBOs: returns the PREVIOUS frame; *out_w/*out_h = its dims).
// out must hold up to the FBO's max size. Returns 1 if out was filled, 0 if not (first frame).
int  wlp_hw_readback(void* out, int cur_w, int cur_h, int bottom_left, int* out_w, int* out_h);
void wlp_hw_destroy(void);
// Diagnostics: GL_RENDERER/vendor/version + asyncReadback flag (valid after wlp_hw_init), and
// the readback EMA split — issue (glReadPixels enqueue) vs map (MapBuffer wait + copy).
const char* wlp_hw_info(void);
void wlp_hw_readback_times(double* issue_ms, double* map_ms);
void wlp_hw_readback_times2(double* mapcall_ms, double* copy_ms);  // map_ms split: GPU sync vs pixel copy
void wlp_hw_set_present_target(int w, int h);  // downscale-before-readback hint (0,0 = full res)

// Current window size (pixels).
void wlp_size(void* h, int* w, int* hh);

void wlp_destroy(void* h);

#ifdef __cplusplus
}
#endif
#endif
