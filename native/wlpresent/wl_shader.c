// RetroArch GLSL shader-chain runtime (.glslp presets from libretro's shaders_glsl pack).
// Implements the classic GL preset format: an INI-style .glslp lists N passes; each pass is one
// .glsl file compiled twice (with VERTEX / FRAGMENT defined) and run through FBOs, the last pass
// rendering into the caller's viewport on the default framebuffer. Standard interface (the same
// contract RetroArch's GL driver gives these shaders):
//   attributes  VertexCoord (vec4 clip-space quad), TexCoord (vec4, xy = 0..1)
//   uniforms    MVPMatrix (identity), Texture (the pass input), InputSize, TextureSize,
//               OutputSize (vec2), FrameCount (int, wrapped by frame_count_modN)
// v1 limits (presets needing these fail cleanly → caller falls back to the plain quad):
//   #pragma parameters use their compiled-in defaults (PARAMETER_UNIFORM left undefined),
//   LUT textures ("textures = …"), PassPrev/feedback, float/srgb framebuffers are unsupported.
#include "wl_shader.h"
#include <EGL/egl.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#define MAX_PASSES 16

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
} sc_pass;

struct sc_chain {
    int npasses;
    sc_pass pass[MAX_PASSES];
    unsigned frame;
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
    PFNGLUNIFORMMATRIX4FVPROC UniformMatrix4fv;
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
    F(UniformMatrix4fv, "glUniformMatrix4fv")
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

// Compile one stage of a .glsl file: inject the stage define AFTER a leading #version line
// (the spec requires #version first). RetroArch compiles legacy no-version files as GLSL 1.20.
static GLuint compile_stage(const char *src, int is_vertex, const char *path) {
    const char *define = is_vertex ? "#define VERTEX\n" : "#define FRAGMENT\n";
    const char *parts[3]; int nparts = 0;
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

    // LUT textures are not supported in v1 — fail the whole preset cleanly.
    char *luts = ini_get(ini, "textures");
    if (luts) { fprintf(stderr, "[wlsc] %s uses LUT textures (unsupported)\n", presetPath); free(luts); free(ini); return NULL; }

    int n = ini_get_int(ini, "shaders", 0);
    if (n < 1 || n > MAX_PASSES) { free(ini); return NULL; }

    sc_chain *c = calloc(1, sizeof *c);
    if (!c) { free(ini); return NULL; }
    c->npasses = n;

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

        p->a_vertex     = G.GetAttribLocation(p->prog, "VertexCoord");
        p->a_tex        = G.GetAttribLocation(p->prog, "TexCoord");
        p->u_mvp        = G.GetUniformLocation(p->prog, "MVPMatrix");
        p->u_tex        = G.GetUniformLocation(p->prog, "Texture");
        p->u_insize     = G.GetUniformLocation(p->prog, "InputSize");
        p->u_texsize    = G.GetUniformLocation(p->prog, "TextureSize");
        p->u_outsize    = G.GetUniformLocation(p->prog, "OutputSize");
        p->u_framecount = G.GetUniformLocation(p->prog, "FrameCount");
        p->u_framedir   = G.GetUniformLocation(p->prog, "FrameDirection");

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
    free(ini);
    return c;

fail:
    free(ini);
    sc_free(c);
    return NULL;
}

void sc_free(sc_chain *c) {
    if (!c) return;
    for (int i = 0; i < c->npasses; i++) {
        sc_pass *p = &c->pass[i];
        if (p->prog) G.DeleteProgram(p->prog);
        if (p->fbo) G.DeleteFramebuffers(1, &p->fbo);
        if (p->fbo_tex) glDeleteTextures(1, &p->fbo_tex);
    }
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

static const GLfloat IDENTITY[16] = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };

int sc_draw(sc_chain *c, unsigned gameTex, int fw, int fh,
            int vx, int vy, int vw, int vh) {
    if (!c || c->npasses < 1) return 0;

    // Standard full-target quad: clip-space positions + 0..1 texcoords (top of texture = top of
    // frame; the game texture is uploaded top-down, and FBO passes consume their input the same
    // way, so the orientation stays consistent through the chain).
    static const GLfloat verts[] = { -1,-1,0,1,  1,-1,0,1,  -1,1,0,1,  1,1,0,1 };
    static const GLfloat texco[] = {  0, 1,0,0,  1, 1,0,0,   0,0,0,0,  1,0,0,0 };

    unsigned inTex = gameTex;
    int inW = fw, inH = fh;          // InputSize of the pass (= its input's content size)

    for (int i = 0; i < c->npasses; i++) {
        sc_pass *p = &c->pass[i];
        int last = (i == c->npasses - 1);

        int outW, outH;
        if (last && p->scale_type_x == 1 && p->scale_x == 1.0f
                 && p->scale_type_y == 1 && p->scale_y == 1.0f) { outW = vw; outH = vh; }
        else {
            outW = p->scale_type_x == 0 ? (int)(inW * p->scale_x + 0.5f)
                 : p->scale_type_x == 1 ? (int)(vw * p->scale_x + 0.5f) : p->abs_x;
            outH = p->scale_type_y == 0 ? (int)(inH * p->scale_y + 0.5f)
                 : p->scale_type_y == 1 ? (int)(vh * p->scale_y + 0.5f) : p->abs_y;
        }
        if (outW < 1) outW = 1;
        if (outH < 1) outH = 1;
        if (outW > 8192) outW = 8192;
        if (outH > 8192) outH = 8192;

        if (last) {
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
            G.VertexAttribPointer((GLuint)p->a_vertex, 4, GL_FLOAT, GL_FALSE, 0, verts);
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
    c->frame++;
    return 1;
}
