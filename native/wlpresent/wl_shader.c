// RetroArch GLSL shader-chain runtime (.glslp presets from libretro's shaders_glsl pack).
// Implements the classic GL preset format: an INI-style .glslp lists N passes; each pass is one
// .glsl file compiled twice (with VERTEX / FRAGMENT defined) and run through FBOs, the last pass
// rendering into the caller's viewport on the default framebuffer. Standard interface (the same
// contract RetroArch's GL driver gives these shaders):
//   attributes  VertexCoord (vec4 clip-space quad), TexCoord (vec4, xy = 0..1)
//   uniforms    MVPMatrix (identity), Texture (the pass input), InputSize, TextureSize,
//               OutputSize (vec2), FrameCount (int, wrapped by frame_count_modN)
// v1 limits (presets needing these fail cleanly → caller falls back to the plain quad):
//   PassPrev/feedback and float/srgb framebuffers are unsupported.
// #pragma parameters ARE supported at their default values: we compile with PARAMETER_UNIFORM
// defined (so the shaders' `#ifdef PARAMETER_UNIFORM uniform float X;` blocks declare the real
// uniforms) and set each one to the default from its `#pragma parameter NAME "…" DEFAULT …` line.
// This is what the Game Boy presets (gameboy, gb-dot-matrix-*, gba-dot-matrix-*) need — they
// guard their param uniforms behind PARAMETER_UNIFORM with no #else fallback, so without it the
// names are undeclared. Live tuning isn't exposed; every parameter just takes its default.
// LUT textures ("textures = NAME;…") ARE supported: each named PNG is decoded (libpng simplified
// API) into a GL texture and bound as the same-named sampler on texture units 1.. (unit 0 stays
// the pass input). This is what the libretro handheld presets (gb-palette-*, *-dot-matrix, the
// sameboy/ags001 LCD shaders) need — verified loading the Game Boy palette/dot-matrix presets.
#include "wl_shader.h"
#include <EGL/egl.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <png.h>
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#define MAX_PASSES 16
#define MAX_LUTS   8
#define MAX_PARAMS 32

typedef struct {
    GLuint prog;
    GLint a_vertex, a_tex;
    GLint u_mvp, u_tex, u_insize, u_texsize, u_outsize, u_framecount, u_framedir;
    int filter_linear;          // sampling of THIS pass's input
    int scale_type_x, scale_type_y;   // 0=source 1=viewport 2=absolute
    float scale_x, scale_y;
    int abs_x, abs_y;
    int frame_count_mod;
    int wrap_repeat;            // GL_CLAMP_TO_EDGE unless wrap_mode says otherwise
    GLuint fbo, fbo_tex;        // intermediate target (not used by the last pass)
    int fbo_w, fbo_h;
    GLint lut_loc[MAX_LUTS];    // this pass's sampler location for each chain LUT (-1 = unused)
    int nparams;                // resolved #pragma parameter uniforms (set to their defaults)
    struct { GLint loc; float val; int dyn; } param[MAX_PARAMS];  // dyn: 0=static, 1=OUT_X→vw, 2=OUT_Y→vh
} sc_pass;

// A decoded LUT texture, shared by every pass that samples it by name.
typedef struct {
    char   name[64];
    GLuint tex;
} sc_lut;

struct sc_chain {
    int npasses;
    sc_pass pass[MAX_PASSES];
    int nluts;
    sc_lut lut[MAX_LUTS];
    unsigned frame;
    double overlay_ar;          // intended output aspect (OUT_X/OUT_Y) for console-border presets; 0 = none
    double design_x;            // OUT_X (design output width) — game scale tracks viewport/design_x; 0 = none
    GLuint final_fbo, final_tex; int final_w, final_h;   // design-res target for overlay presets (blitted to screen)
};

// ── GL2/FBO entry points (own resolver; wl_present.c keeps its own private set) ──
static struct {
    int inited, ok;
    PFNGLCREATESHADERPROC CreateShader; PFNGLSHADERSOURCEPROC ShaderSource;
    PFNGLCOMPILESHADERPROC CompileShader; PFNGLGETSHADERIVPROC GetShaderiv;
    PFNGLGETSHADERINFOLOGPROC GetShaderInfoLog; PFNGLCREATEPROGRAMPROC CreateProgram;
    PFNGLATTACHSHADERPROC AttachShader; PFNGLLINKPROGRAMPROC LinkProgram;
    PFNGLGETPROGRAMIVPROC GetProgramiv; PFNGLGETPROGRAMINFOLOGPROC GetProgramInfoLog;
    PFNGLUSEPROGRAMPROC UseProgram; PFNGLDELETESHADERPROC DeleteShader;
    PFNGLDELETEPROGRAMPROC DeleteProgram; PFNGLBINDATTRIBLOCATIONPROC BindAttribLocation;
    PFNGLGETUNIFORMLOCATIONPROC GetUniformLocation; PFNGLGETATTRIBLOCATIONPROC GetAttribLocation;
    PFNGLUNIFORM1IPROC Uniform1i; PFNGLUNIFORM1FPROC Uniform1f; PFNGLUNIFORM2FPROC Uniform2f;
    PFNGLUNIFORMMATRIX4FVPROC UniformMatrix4fv; PFNGLACTIVETEXTUREPROC ActiveTexture;
    PFNGLVERTEXATTRIBPOINTERPROC VertexAttribPointer;
    PFNGLENABLEVERTEXATTRIBARRAYPROC EnableVertexAttribArray;
    PFNGLDISABLEVERTEXATTRIBARRAYPROC DisableVertexAttribArray;
    PFNGLGENFRAMEBUFFERSPROC GenFramebuffers; PFNGLBINDFRAMEBUFFERPROC BindFramebuffer;
    PFNGLFRAMEBUFFERTEXTURE2DPROC FramebufferTexture2D;
    PFNGLCHECKFRAMEBUFFERSTATUSPROC CheckFramebufferStatus;
    PFNGLDELETEFRAMEBUFFERSPROC DeleteFramebuffers;
} G;

static int g_init(void) {
    if (G.inited) return G.ok;
    G.inited = 1;
    #define F(field, name) G.field = (void*)eglGetProcAddress(name); if (!G.field) { \
        fprintf(stderr, "[wlsc] missing %s\n", name); return (G.ok = 0); }
    F(CreateShader, "glCreateShader") F(ShaderSource, "glShaderSource")
    F(CompileShader, "glCompileShader") F(GetShaderiv, "glGetShaderiv")
    F(GetShaderInfoLog, "glGetShaderInfoLog") F(CreateProgram, "glCreateProgram")
    F(AttachShader, "glAttachShader") F(LinkProgram, "glLinkProgram")
    F(GetProgramiv, "glGetProgramiv") F(GetProgramInfoLog, "glGetProgramInfoLog")
    F(UseProgram, "glUseProgram") F(DeleteShader, "glDeleteShader")
    F(DeleteProgram, "glDeleteProgram") F(BindAttribLocation, "glBindAttribLocation")
    F(GetUniformLocation, "glGetUniformLocation") F(GetAttribLocation, "glGetAttribLocation")
    F(Uniform1i, "glUniform1i") F(Uniform1f, "glUniform1f") F(Uniform2f, "glUniform2f")
    F(UniformMatrix4fv, "glUniformMatrix4fv") F(ActiveTexture, "glActiveTexture")
    F(VertexAttribPointer, "glVertexAttribPointer")
    F(EnableVertexAttribArray, "glEnableVertexAttribArray")
    F(DisableVertexAttribArray, "glDisableVertexAttribArray")
    F(GenFramebuffers, "glGenFramebuffers") F(BindFramebuffer, "glBindFramebuffer")
    F(FramebufferTexture2D, "glFramebufferTexture2D")
    F(CheckFramebufferStatus, "glCheckFramebufferStatus")
    F(DeleteFramebuffers, "glDeleteFramebuffers")
    #undef F
    return (G.ok = 1);
}

// ── tiny INI helpers (the .glslp format: key = value, "quotes" optional, # comments) ──
static char *ini_get(const char *ini, const char *key) {
    size_t klen = strlen(key);
    const char *p = ini;
    while (p && *p) {
        const char *line = p;
        const char *nl = strchr(p, '\n');
        p = nl ? nl + 1 : NULL;
        while (*line == ' ' || *line == '\t') line++;
        if (strncmp(line, key, klen) != 0) continue;
        const char *q = line + klen;
        while (*q == ' ' || *q == '\t') q++;
        if (*q != '=') continue;
        q++;
        while (*q == ' ' || *q == '\t' || *q == '"') q++;
        const char *end = q;
        while (*end && *end != '\n' && *end != '\r' && *end != '"' && *end != '#') end++;
        while (end > q && (end[-1] == ' ' || end[-1] == '\t')) end--;
        char *out = malloc((size_t)(end - q) + 1);
        if (!out) return NULL;
        memcpy(out, q, (size_t)(end - q)); out[end - q] = 0;
        return out;
    }
    return NULL;
}
static int ini_get_int(const char *ini, const char *key, int defv) {
    char *v = ini_get(ini, key); if (!v) return defv;
    int r = atoi(v); free(v); return r;
}
static float ini_get_float(const char *ini, const char *key, float defv) {
    char *v = ini_get(ini, key); if (!v) return defv;
    float r = (float)atof(v); free(v); return r;
}
static int ini_get_bool(const char *ini, const char *key, int defv) {
    char *v = ini_get(ini, key); if (!v) return defv;
    int r = (v[0] == 't' || v[0] == 'T' || v[0] == '1'); free(v); return r;
}

static char *read_file(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    if (n < 0 || n > 8 * 1024 * 1024) { fclose(f); return NULL; }
    char *buf = malloc((size_t)n + 1);
    if (!buf) { fclose(f); return NULL; }
    if (fread(buf, 1, (size_t)n, f) != (size_t)n) { free(buf); fclose(f); return NULL; }
    fclose(f); buf[n] = 0;
    return buf;
}

// Resolve "relative" against the directory of "base" (a file path). Returns malloc'd.
static char *rel_path(const char *base, const char *rel) {
    if (rel[0] == '/') return strdup(rel);
    const char *slash = strrchr(base, '/');
    size_t dirlen = slash ? (size_t)(slash - base) + 1 : 0;
    char *out = malloc(dirlen + strlen(rel) + 1);
    if (!out) return NULL;
    memcpy(out, base, dirlen);
    strcpy(out + dirlen, rel);
    return out;
}

// Decode a PNG LUT (libpng simplified API → tight RGBA8, top-down) into a GL texture. PNG row 0
// is the top of the image and our chain samples top-down (texcoord v=0 = top), so the LUT is
// uploaded as-is — no vertical flip, matching how the artwork is authored. linear/wrap come from
// the preset's per-LUT keys. Returns 0 on any failure (caller fails the whole preset cleanly).
static GLuint load_lut_png(const char *path, int linear, int wrap_repeat) {
    png_image img; memset(&img, 0, sizeof img); img.version = PNG_IMAGE_VERSION;
    if (!png_image_begin_read_from_file(&img, path)) {
        fprintf(stderr, "[wlsc] LUT open %s: %s\n", path, img.message); return 0;
    }
    img.format = PNG_FORMAT_RGBA;
    png_bytep buf = malloc(PNG_IMAGE_SIZE(img));
    if (!buf) { png_image_free(&img); return 0; }
    if (!png_image_finish_read(&img, NULL, buf, 0, NULL)) {
        fprintf(stderr, "[wlsc] LUT decode %s: %s\n", path, img.message);
        free(buf); png_image_free(&img); return 0;
    }
    GLuint tex = 0; glGenTextures(1, &tex); glBindTexture(GL_TEXTURE_2D, tex);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, (GLsizei)img.width, (GLsizei)img.height, 0,
                 GL_RGBA, GL_UNSIGNED_BYTE, buf);
    GLint f = linear ? GL_LINEAR : GL_NEAREST;
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, f);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, f);
    GLint w = wrap_repeat ? GL_REPEAT : GL_CLAMP_TO_EDGE;
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, w);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, w);
    glBindTexture(GL_TEXTURE_2D, 0);
    free(buf); png_image_free(&img);
    return tex;
}

// Parse the chain's "textures = A;B" list and load each LUT. Per-LUT keys: "<name>" = PNG path
// (relative to the preset), "<name>_linear" (bool), "<name>_wrap_mode" (repeat vs clamp). Returns
// 1 on success (including no LUTs), 0 if any declared LUT failed to load.
static int load_luts(sc_chain *c, const char *ini, const char *presetPath) {
    char *list = ini_get(ini, "textures");
    if (!list) return 1;
    int ok = 1;
    for (char *tok = strtok(list, ";"); tok && ok; tok = strtok(NULL, ";")) {
        while (*tok == ' ' || *tok == '\t') tok++;
        char *end = tok + strlen(tok);
        while (end > tok && (end[-1] == ' ' || end[-1] == '\t')) *--end = 0;
        if (!*tok) continue;
        if (c->nluts >= MAX_LUTS) { fprintf(stderr, "[wlsc] too many LUTs\n"); ok = 0; break; }

        char *rel = ini_get(ini, tok);
        if (!rel) { fprintf(stderr, "[wlsc] LUT %s has no path\n", tok); ok = 0; break; }
        char *abs = rel_path(presetPath, rel); free(rel);
        if (!abs) { ok = 0; break; }

        char key[80];
        snprintf(key, sizeof key, "%s_linear", tok);
        int linear = ini_get_bool(ini, key, 0);
        snprintf(key, sizeof key, "%s_wrap_mode", tok);
        char *wrap = ini_get(ini, key);
        int repeat = wrap && strstr(wrap, "repeat") != NULL;   // repeat / mirrored_repeat → GL_REPEAT
        free(wrap);

        GLuint tex = load_lut_png(abs, linear, repeat); free(abs);
        if (!tex) { ok = 0; break; }
        sc_lut *L = &c->lut[c->nluts++];
        snprintf(L->name, sizeof L->name, "%s", tok);
        L->tex = tex;
    }
    free(list);
    return ok;
}

// Scan a .glsl source for "#pragma parameter NAME "desc" DEFAULT min max step" lines, recording
// (name, default) for each. Returns the count (capped at maxn). Used to set each parameter uniform
// to its preset default after the program links.
typedef struct { char name[64]; float val; } sc_pp;
static int collect_params(const char *src, sc_pp *out, int maxn) {
    int n = 0;
    const char *p = src;
    while (n < maxn && (p = strstr(p, "#pragma parameter")) != NULL) {
        p += 17;                                  // past "#pragma parameter"
        while (*p == ' ' || *p == '\t') p++;
        const char *ns = p;
        while (isalnum((unsigned char)*p) || *p == '_') p++;
        size_t nlen = (size_t)(p - ns);
        const char *nl = strchr(p, '\n');
        if (nlen == 0 || nlen >= sizeof out[0].name) continue;
        const char *q = strchr(p, '"');           // opening quote of the description
        if (!q || (nl && q > nl)) continue;
        const char *q2 = strchr(q + 1, '"');      // closing quote
        if (!q2 || (nl && q2 > nl)) continue;
        p = q2 + 1;
        while (*p == ' ' || *p == '\t') p++;
        char *endp = NULL;
        float v = strtof(p, &endp);
        if (endp == p) continue;                  // no numeric default → skip
        p = endp;
        memcpy(out[n].name, ns, nlen); out[n].name[nlen] = 0;
        out[n].val = v;
        n++;
    }
    return n;
}

// Compile one stage of a .glsl file: inject the stage define AFTER a leading #version line
// (the spec requires #version first). RetroArch compiles legacy no-version files as GLSL 1.20.
// PARAMETER_UNIFORM is defined so the shaders' guarded `uniform float <param>;` blocks declare
// the real uniforms (we then set them to their defaults after link); see the header note.
static GLuint compile_stage(const char *src, int is_vertex, const char *path) {
    const char *define = is_vertex ? "#define VERTEX\n" : "#define FRAGMENT\n";
    const char *parts[4]; int nparts = 0;
    char verline[128] = "";
    const char *body = src;
    if (strncmp(src, "#version", 8) == 0) {
        const char *nl = strchr(src, '\n');
        size_t vl = nl ? (size_t)(nl - src) + 1 : strlen(src);
        if (vl >= sizeof verline) vl = sizeof verline - 1;
        memcpy(verline, src, vl); verline[vl] = 0;
        body = nl ? nl + 1 : src + strlen(src);
        parts[nparts++] = verline;
    }
    parts[nparts++] = define;
    parts[nparts++] = "#define PARAMETER_UNIFORM\n";
    parts[nparts++] = body;
    GLuint sh = G.CreateShader(is_vertex ? GL_VERTEX_SHADER : GL_FRAGMENT_SHADER);
    if (!sh) return 0;
    G.ShaderSource(sh, nparts, parts, NULL);
    G.CompileShader(sh);
    GLint ok = 0; G.GetShaderiv(sh, GL_COMPILE_STATUS, &ok);
    if (!ok) {
        char log[1024]; G.GetShaderInfoLog(sh, sizeof log, NULL, log);
        fprintf(stderr, "[wlsc] %s %s compile failed: %.900s\n", path, is_vertex ? "VS" : "FS", log);
        G.DeleteShader(sh); return 0;
    }
    return sh;
}

static int parse_scale_type(const char *v) {
    if (!v) return -1;
    if (strcmp(v, "source") == 0) return 0;
    if (strcmp(v, "viewport") == 0) return 1;
    if (strcmp(v, "absolute") == 0) return 2;
    return -1;
}

sc_chain *sc_load(const char *presetPath) {
    if (!g_init()) return NULL;
    char *ini = read_file(presetPath);
    if (!ini) { fprintf(stderr, "[wlsc] cannot read %s\n", presetPath); return NULL; }

    int n = ini_get_int(ini, "shaders", 0);
    if (n < 1 || n > MAX_PASSES) { free(ini); return NULL; }

    sc_chain *c = calloc(1, sizeof *c);
    if (!c) { free(ini); return NULL; }
    c->npasses = n;

    // LUT textures ("textures = NAME;…") — decode each named PNG up front so every pass can bind it.
    if (!load_luts(c, ini, presetPath)) goto fail;

    for (int i = 0; i < n; i++) {
        sc_pass *p = &c->pass[i];
        char key[64];

        snprintf(key, sizeof key, "shader%d", i);
        char *rel = ini_get(ini, key);
        if (!rel) goto fail;
        char *glslPath = rel_path(presetPath, rel);
        free(rel);
        if (!glslPath) goto fail;
        char *src = read_file(glslPath);
        if (!src) { fprintf(stderr, "[wlsc] cannot read %s\n", glslPath); free(glslPath); goto fail; }

        sc_pp pp[MAX_PARAMS];
        int npp = collect_params(src, pp, MAX_PARAMS);   // (name, default) before src is freed
        // The .glslp may override a parameter's value (e.g. "OUT_X = 2400.0", "grey_balance = 3.0");
        // RetroArch uses those over the .glsl #pragma default, so do the same.
        for (int j = 0; j < npp; j++) {
            char *ov = ini_get(ini, pp[j].name);
            if (ov) { pp[j].val = (float)atof(ov); free(ov); }
        }

        GLuint vs = compile_stage(src, 1, glslPath);
        GLuint fs = compile_stage(src, 0, glslPath);
        free(src);
        if (!vs || !fs) {
            if (vs) G.DeleteShader(vs);
            if (fs) G.DeleteShader(fs);
            free(glslPath); goto fail;
        }
        p->prog = G.CreateProgram();
        G.AttachShader(p->prog, vs); G.AttachShader(p->prog, fs);
        // Fixed attribute slots before link (the RetroArch GL contract names).
        G.BindAttribLocation(p->prog, 0, "VertexCoord");
        G.BindAttribLocation(p->prog, 1, "TexCoord");
        G.LinkProgram(p->prog);
        G.DeleteShader(vs); G.DeleteShader(fs);
        GLint ok = 0; G.GetProgramiv(p->prog, GL_LINK_STATUS, &ok);
        if (!ok) {
            char log[512]; G.GetProgramInfoLog(p->prog, sizeof log, NULL, log);
            fprintf(stderr, "[wlsc] %s link failed: %.400s\n", glslPath, log);
            free(glslPath); goto fail;
        }
        free(glslPath);

        // Resolve each #pragma parameter to its uniform location; ones the linker dropped (unused,
        // or behind a #define fallback) report -1 and are simply skipped.
        for (int j = 0; j < npp; j++) {
            GLint loc = G.GetUniformLocation(p->prog, pp[j].name);
            if (loc >= 0 && p->nparams < MAX_PARAMS) {
                // OUT_X/OUT_Y are the shader's assumed output resolution; the border only fills the
                // frame when they equal the real viewport, so drive them from it at draw time rather
                // than the preset's fixed guess (otherwise the overlay is zoomed in / cropped).
                int dyn = strcmp(pp[j].name, "OUT_X") == 0 ? 1
                        : strcmp(pp[j].name, "OUT_Y") == 0 ? 2
                        : strcmp(pp[j].name, "video_scale") == 0 ? 3 : 0;
                p->param[p->nparams].loc = loc;
                p->param[p->nparams].val = pp[j].val;
                p->param[p->nparams].dyn = dyn;
                p->nparams++;
            }
        }

        p->a_vertex     = G.GetAttribLocation(p->prog, "VertexCoord");
        p->a_tex        = G.GetAttribLocation(p->prog, "TexCoord");
        p->u_mvp        = G.GetUniformLocation(p->prog, "MVPMatrix");
        p->u_tex        = G.GetUniformLocation(p->prog, "Texture");
        p->u_insize     = G.GetUniformLocation(p->prog, "InputSize");
        p->u_texsize    = G.GetUniformLocation(p->prog, "TextureSize");
        p->u_outsize    = G.GetUniformLocation(p->prog, "OutputSize");
        p->u_framecount = G.GetUniformLocation(p->prog, "FrameCount");
        p->u_framedir   = G.GetUniformLocation(p->prog, "FrameDirection");
        for (int k = 0; k < c->nluts; k++)
            p->lut_loc[k] = G.GetUniformLocation(p->prog, c->lut[k].name);

        snprintf(key, sizeof key, "filter_linear%d", i);
        p->filter_linear = ini_get_bool(ini, key, 0);
        snprintf(key, sizeof key, "frame_count_mod%d", i);
        p->frame_count_mod = ini_get_int(ini, key, 0);
        snprintf(key, sizeof key, "wrap_mode%d", i);
        char *wrap = ini_get(ini, key);
        p->wrap_repeat = wrap && strcmp(wrap, "repeat") == 0;
        free(wrap);

        // Scale: scale_typeN sets both axes; *_xN / *_yN override per axis. Default for every
        // pass but the last is "source ×1"; the last pass defaults to the viewport.
        snprintf(key, sizeof key, "scale_type%d", i);
        char *st = ini_get(ini, key);
        int both = parse_scale_type(st); free(st);
        snprintf(key, sizeof key, "scale_type_x%d", i);
        st = ini_get(ini, key); int sx = parse_scale_type(st); free(st);
        snprintf(key, sizeof key, "scale_type_y%d", i);
        st = ini_get(ini, key); int sy = parse_scale_type(st); free(st);
        int last = (i == n - 1);
        p->scale_type_x = sx >= 0 ? sx : both >= 0 ? both : (last ? 1 : 0);
        p->scale_type_y = sy >= 0 ? sy : both >= 0 ? both : (last ? 1 : 0);
        snprintf(key, sizeof key, "scale%d", i);
        float sboth = ini_get_float(ini, key, 1.0f);
        snprintf(key, sizeof key, "scale_x%d", i);
        p->scale_x = ini_get_float(ini, key, sboth);
        snprintf(key, sizeof key, "scale_y%d", i);
        p->scale_y = ini_get_float(ini, key, sboth);
        snprintf(key, sizeof key, "absolute_x%d", i);
        p->abs_x = ini_get_int(ini, key, 0);
        snprintf(key, sizeof key, "absolute_y%d", i);
        p->abs_y = ini_get_int(ini, key, 0);
    }

    // Console-border presets (gameboy shells etc.) compose into a fixed output aspect given by the
    // OUT_X/OUT_Y parameters; their final pass scales the border by OutputSize/(OUT_X,OUT_Y), so the
    // border only stays undistorted when the viewport matches that aspect. Surface it (sc_aspect) so
    // the present path can size the game rect to it instead of the raw pixel aspect.
    { char *ox = ini_get(ini, "OUT_X"), *oy = ini_get(ini, "OUT_Y");
      if (ox && oy) { double x = atof(ox), y = atof(oy);
          if (x > 0.0 && y > 0.0) { c->overlay_ar = x / y; c->design_x = x; } }
      free(ox); free(oy); }

    free(ini);
    return c;

fail:
    free(ini);
    sc_free(c);
    return NULL;
}

double sc_aspect(sc_chain *c) { return c ? c->overlay_ar : 0.0; }

void sc_free(sc_chain *c) {
    if (!c) return;
    for (int i = 0; i < c->npasses; i++) {
        sc_pass *p = &c->pass[i];
        if (p->prog) G.DeleteProgram(p->prog);
        if (p->fbo) G.DeleteFramebuffers(1, &p->fbo);
        if (p->fbo_tex) glDeleteTextures(1, &p->fbo_tex);
    }
    for (int k = 0; k < c->nluts; k++)
        if (c->lut[k].tex) glDeleteTextures(1, &c->lut[k].tex);
    if (c->final_fbo) G.DeleteFramebuffers(1, &c->final_fbo);
    if (c->final_tex) glDeleteTextures(1, &c->final_tex);
    free(c);
}

// (Re)allocate a pass's intermediate FBO at w×h (RGBA8).
static int ensure_fbo(sc_pass *p, int w, int h) {
    if (p->fbo && p->fbo_w == w && p->fbo_h == h) return 1;
    if (!p->fbo_tex) glGenTextures(1, &p->fbo_tex);
    glBindTexture(GL_TEXTURE_2D, p->fbo_tex);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    if (!p->fbo) G.GenFramebuffers(1, &p->fbo);
    G.BindFramebuffer(GL_FRAMEBUFFER, p->fbo);
    G.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, p->fbo_tex, 0);
    GLenum st = G.CheckFramebufferStatus(GL_FRAMEBUFFER);
    G.BindFramebuffer(GL_FRAMEBUFFER, 0);
    if (st != GL_FRAMEBUFFER_COMPLETE) { fprintf(stderr, "[wlsc] FBO incomplete (%dx%d)\n", w, h); return 0; }
    p->fbo_w = w; p->fbo_h = h;
    return 1;
}

// (Re)allocate the chain's design-resolution target (overlay presets render the whole chain here at
// OUT_X×OUT_Y, then it's scaled to the screen). Linear sampling — it's downscaled on the blit.
static int ensure_final_fbo(sc_chain *c, int w, int h) {
    if (c->final_fbo && c->final_w == w && c->final_h == h) return 1;
    if (!c->final_tex) glGenTextures(1, &c->final_tex);
    glBindTexture(GL_TEXTURE_2D, c->final_tex);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    if (!c->final_fbo) G.GenFramebuffers(1, &c->final_fbo);
    G.BindFramebuffer(GL_FRAMEBUFFER, c->final_fbo);
    G.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, c->final_tex, 0);
    GLenum st = G.CheckFramebufferStatus(GL_FRAMEBUFFER);
    G.BindFramebuffer(GL_FRAMEBUFFER, 0);
    if (st != GL_FRAMEBUFFER_COMPLETE) { fprintf(stderr, "[wlsc] final FBO incomplete (%dx%d)\n", w, h); return 0; }
    c->final_w = w; c->final_h = h;
    return 1;
}

static const GLfloat IDENTITY[16] = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };

int sc_draw(sc_chain *c, unsigned gameTex, int fw, int fh,
            int vx, int vy, int vw, int vh) {
    if (!c || c->npasses < 1) return 0;

    // Console-border presets are authored for a fixed design resolution (OUT_X×OUT_Y) at which the
    // game's integer video_scale and the shell art align pixel-perfectly. Render the whole chain at
    // that resolution into final_fbo, then scale it to the real game rect — this keeps video_scale
    // integer (crisp) and makes fit-to-window a high-quality linear downscale instead of a fractional
    // in-shader upscale. cvw/cvh is the chain's working viewport: design res here, real rect otherwise.
    int overlay = (c->design_x > 0.0 && c->overlay_ar > 0.0);
    int cvw = vw, cvh = vh;
    if (overlay) {
        cvw = (int)(c->design_x + 0.5);
        cvh = (int)(c->design_x / c->overlay_ar + 0.5);
        if (cvw < 1) cvw = 1; else if (cvw > 4096) cvw = 4096;
        if (cvh < 1) cvh = 1; else if (cvh > 4096) cvh = 4096;
        if (!ensure_final_fbo(c, cvw, cvh)) overlay = 0;   // allocation failed → fall back to direct draw
    }

    // Standard full-target quad: clip-space positions + 0..1 texcoords (texcoord v=0 = top of the
    // image; the game texture is uploaded top-down). Rendering into an FBO inverts Y (clip-top lands
    // at the highest row, so a sampled FBO comes back flipped) — which upends the image once per
    // intermediate pass. To keep every pass's input top-down, the FBO (non-last) passes draw with a
    // Y-flipped quad so their result is stored top-down; the final pass (straight to the viewport)
    // uses the plain quad, matching the single-pass path that already renders upright.
    static const GLfloat verts[]      = { -1,-1,0,1,  1,-1,0,1,  -1, 1,0,1,  1, 1,0,1 };
    static const GLfloat verts_flip[] = { -1, 1,0,1,  1, 1,0,1,  -1,-1,0,1,  1,-1,0,1 };
    static const GLfloat texco[]      = {  0, 1,0,0,  1, 1,0,0,   0, 0,0,0,  1, 0,0,0 };

    unsigned inTex = gameTex;
    int inW = fw, inH = fh;          // InputSize of the pass (= its input's content size)

    for (int i = 0; i < c->npasses; i++) {
        sc_pass *p = &c->pass[i];
        int last = (i == c->npasses - 1);

        int outW, outH;
        if (last && p->scale_type_x == 1 && p->scale_x == 1.0f
                 && p->scale_type_y == 1 && p->scale_y == 1.0f) { outW = cvw; outH = cvh; }
        else {
            outW = p->scale_type_x == 0 ? (int)(inW * p->scale_x + 0.5f)
                 : p->scale_type_x == 1 ? (int)(cvw * p->scale_x + 0.5f) : p->abs_x;
            outH = p->scale_type_y == 0 ? (int)(inH * p->scale_y + 0.5f)
                 : p->scale_type_y == 1 ? (int)(cvh * p->scale_y + 0.5f) : p->abs_y;
        }
        if (outW < 1) outW = 1;
        if (outH < 1) outH = 1;
        if (outW > 8192) outW = 8192;
        if (outH > 8192) outH = 8192;

        if (last && overlay) {
            G.BindFramebuffer(GL_FRAMEBUFFER, c->final_fbo);
            glViewport(0, 0, outW, outH);            // render the final pass at design res
        } else if (last) {
            G.BindFramebuffer(GL_FRAMEBUFFER, 0);
            glViewport(vx, vy, vw, vh);
        } else {
            if (!ensure_fbo(p, outW, outH)) return 0;
            G.BindFramebuffer(GL_FRAMEBUFFER, p->fbo);
            glViewport(0, 0, outW, outH);
        }

        glBindTexture(GL_TEXTURE_2D, inTex);
        GLint filt = p->filter_linear ? GL_LINEAR : GL_NEAREST;
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filt);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filt);
        GLint wrap = p->wrap_repeat ? GL_REPEAT : GL_CLAMP_TO_EDGE;
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, wrap);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, wrap);

        G.UseProgram(p->prog);
        if (p->u_mvp >= 0)     G.UniformMatrix4fv(p->u_mvp, 1, GL_FALSE, IDENTITY);
        if (p->u_tex >= 0)     G.Uniform1i(p->u_tex, 0);
        for (int j = 0; j < p->nparams; j++) {
            float pv = p->param[j].val;
            if      (p->param[j].dyn == 1) pv = (float)cvw;  // OUT_X → working viewport width
            else if (p->param[j].dyn == 2) pv = (float)cvh;  // OUT_Y → working viewport height
            else if (p->param[j].dyn == 3 && c->design_x > 0.0)
                pv = p->param[j].val * (float)(cvw / c->design_x);  // video_scale tracks viewport/design
            G.Uniform1f(p->param[j].loc, pv);
        }
        // Bind the chain's LUTs on units 1.. (the pass input stays on unit 0). Passes that don't
        // declare a given sampler have lut_loc[k] < 0 and are skipped; leave the active unit on 0.
        for (int k = 0; k < c->nluts; k++) {
            if (p->lut_loc[k] < 0) continue;
            G.ActiveTexture((GLenum)(GL_TEXTURE0 + 1 + k));
            glBindTexture(GL_TEXTURE_2D, c->lut[k].tex);
            G.Uniform1i(p->lut_loc[k], 1 + k);
        }
        G.ActiveTexture(GL_TEXTURE0);
        // InputSize == TextureSize here: inputs are tightly-packed textures (no oversized pot
        // padding), both for the game texture and our FBO targets.
        if (p->u_insize >= 0)  G.Uniform2f(p->u_insize, (float)inW, (float)inH);
        if (p->u_texsize >= 0) G.Uniform2f(p->u_texsize, (float)inW, (float)inH);
        if (p->u_outsize >= 0) G.Uniform2f(p->u_outsize, (float)outW, (float)outH);
        unsigned fc = c->frame;
        if (p->frame_count_mod > 0) fc %= (unsigned)p->frame_count_mod;
        if (p->u_framecount >= 0) G.Uniform1i(p->u_framecount, (GLint)fc);
        if (p->u_framedir >= 0)   G.Uniform1i(p->u_framedir, 1);

        if (p->a_vertex >= 0) {
            G.EnableVertexAttribArray((GLuint)p->a_vertex);
            // Render to screen only when this is the final pass AND we're not staging through the
            // design-res FBO; every FBO target (intermediate, or the overlay final_fbo) needs the flip.
            G.VertexAttribPointer((GLuint)p->a_vertex, 4, GL_FLOAT, GL_FALSE, 0,
                                  (last && !overlay) ? verts : verts_flip);
        }
        if (p->a_tex >= 0) {
            G.EnableVertexAttribArray((GLuint)p->a_tex);
            G.VertexAttribPointer((GLuint)p->a_tex, 4, GL_FLOAT, GL_FALSE, 0, texco);
        }
        glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
        if (p->a_vertex >= 0) G.DisableVertexAttribArray((GLuint)p->a_vertex);
        if (p->a_tex >= 0)    G.DisableVertexAttribArray((GLuint)p->a_tex);

        inTex = p->fbo_tex;
        inW = outW; inH = outH;
    }
    G.UseProgram(0);

    if (overlay) {
        // Scale the design-res composite down into the real game rect (fixed-function, linear). The
        // final_fbo is stored top-down (it was rendered with the flipped quad like any FBO target), so
        // clip-top samples v=0 = image top → upright, matching the direct-to-screen path.
        G.BindFramebuffer(GL_FRAMEBUFFER, 0);
        glViewport(vx, vy, vw, vh);
        G.ActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_2D, c->final_tex);
        glEnable(GL_TEXTURE_2D);
        glColor4f(1.0f, 1.0f, 1.0f, 1.0f);
        glBegin(GL_TRIANGLE_STRIP);
            glTexCoord2f(0.0f, 1.0f); glVertex2f(-1.0f, -1.0f);
            glTexCoord2f(1.0f, 1.0f); glVertex2f( 1.0f, -1.0f);
            glTexCoord2f(0.0f, 0.0f); glVertex2f(-1.0f,  1.0f);
            glTexCoord2f(1.0f, 0.0f); glVertex2f( 1.0f,  1.0f);
        glEnd();
    }

    c->frame++;
    return 1;
}
