// RetroArch GLSL shader-chain runtime (.glslp presets) — see wl_shader.c.
// All calls must happen on the thread owning the GL context (the present thread).
#ifndef WL_SHADER_H
#define WL_SHADER_H
#ifdef __cplusplus
extern "C" {
#endif

typedef struct sc_chain sc_chain;

// Parse + compile a .glslp preset. Returns NULL on any failure (unsupported feature, missing
// file, compile error) — the caller keeps its plain-quad path.
sc_chain *sc_load(const char *presetPath);
void      sc_free(sc_chain *c);

// Intended output aspect ratio (OUT_X/OUT_Y) for console-border presets, or 0 if the chain has no
// fixed aspect. The present path should size the game rect to this so the border isn't distorted.
double    sc_aspect(sc_chain *c);

// Run the chain: gameTex (fw×fh, top-down) through every pass, the last one rendering into the
// default framebuffer at viewport (vx,vy,vw,vh). Returns 1 if it drew, 0 on failure (caller
// should fall back to the plain quad). Leaves program 0 bound.
int sc_draw(sc_chain *c, unsigned gameTex, int fw, int fh, int vx, int vy, int vw, int vh);

#ifdef __cplusplus
}
#endif
#endif
