using System;
using System.Runtime.InteropServices;
using static Emutastic.Platform.Gl;

namespace Emutastic.Platform
{
    /// <summary>
    /// RetroArch-model GL present: a single SDL3-owned window + GL context, BGRA frame uploaded to a
    /// texture and drawn as an aspect-preserving fullscreen quad, then <c>SDL_GL_SwapWindow</c> with
    /// swap-interval 1 — the BLOCKING vsync swap is the sole frame clock. SDL picks native-Wayland EGL or
    /// X11 GLX automatically (on a Wayland session = native Wayland, like RetroArch's wayland_ctx). One
    /// uncontended window, nothing composited over it — the thing that made RetroArch smooth here.
    /// Call <see cref="Present"/> from the emu thread; it blocks to vsync, pacing the loop.
    /// </summary>
    public sealed unsafe class GlPresenter : IGamePresenter
    {
        // X11/SDL fallback presenter: native WM decorations (no OSD chrome). Deco layers (bezel +
        // Vectrex screen art) mirror the shim's geometry; the shader chain and displayed-res
        // capture remain Wayland-shim-only.
        public bool HasWindowChrome => false;
        public bool HasDecoLayers => true;
        public bool HasShaderChain => false;
        public bool HasCapture => false;

        private IntPtr _window, _ctx;
        // Rotating upload textures. Uploading into the SAME texture the previous frame is still being
        // drawn from forces the driver to stall the glTexSubImage until that draw completes (CPU↔GPU
        // serialize), which intermittently blows the vsync deadline → dropped frames (the "swap blocks
        // but we still only hit ~50fps" symptom vs RetroArch's steady 60). Cycle through N so each upload
        // targets a texture no longer in flight — RetroArch's PBO / max_swapchain_images=3 idea.
        const int TexCount = 3;
        private readonly uint[] _texes = new uint[TexCount];
        private readonly int[] _texW = new int[TexCount];
        private readonly int[] _texH = new int[TexCount];
        private int _texIndex;
        // Direct-EGL present (set when SelfPaced): bypasses SDL_GL_SwapWindow's per-frame frame-callback
        // throttle so Mesa's FIFO can pipeline. Zero => fall back to SDL_GL_SwapWindow.
        private IntPtr _eglDpy, _eglSurf;
        private readonly byte[] _evBuf = new byte[256];   // SDL_Event union drain buffer
        public string? LastError { get; private set; }
        public IntPtr Window => _window;

        // SDL3 event ids (SDL_events.h). We service the GL window's own events on the emu thread inside
        // Present() — the window is FOCUSED (the whole point: an inactive surface gets throttled), so its
        // keyboard + close events are how the player drives the game now that it lives outside Avalonia.
        // SDL3 event ids (SDL_events.h). NOTE: window events start at SDL_EVENT_WINDOW_SHOWN=0x202;
        // CLOSE_REQUESTED is 0x210 (0x202 + 14). Using 0x202 here read every window's SHOWN event as a
        // close → the loop exited one frame after present #1. Keep these exact.
        const uint SDL_EVENT_QUIT = 0x100, SDL_EVENT_WINDOW_MOUSE_LEAVE = 0x20D,
                   SDL_EVENT_WINDOW_CLOSE_REQUESTED = 0x210,
                   SDL_EVENT_KEY_DOWN = 0x300, SDL_EVENT_KEY_UP = 0x301,
                   SDL_EVENT_MOUSE_MOTION = 0x400,
                   SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401, SDL_EVENT_MOUSE_BUTTON_UP = 0x402;
        const ulong SDL_WINDOW_MAXIMIZED = 0x0000000000000080UL;

        /// <summary>Mouse moved inside the game window (hover-reveal an overlay). Emu thread.</summary>
        public event Action? MouseMoved;
        /// <summary>Mouse left the game window (hide the overlay). Emu thread.</summary>
        public event Action? MouseLeft;

        /// <summary>(0=left/1=right/2=mid, isDown) — drives the OSD HUD/cog hit-testing.</summary>
        public event Action<int, bool>? PointerButton;
        // Latest pointer position in WINDOW PIXELS (hit-tests run against the pixel-sized OSD)
        // + whether the pointer is over the window. SDL3 reports window coords (logical points);
        // we scale by pixel/logical ratio at event time so HiDPI X11 still hit-tests correctly.
        public int MouseX { get; private set; }
        public int MouseY { get; private set; }
        public bool MouseInside { get; private set; }

        /// <summary>Set once the window/app asked to close (window X or Ctrl-Q). The emu loop polls this.</summary>
        public bool CloseRequested { get; private set; }

        /// <summary>Raised from <see cref="Present"/> (emu thread) for each non-repeat key transition:
        /// (SDL scancode, isDown). The session maps scancodes → libretro ids / shortcuts.</summary>
        public event Action<int, bool>? KeyEvent;

        /// <summary>Wall-clock ms the last <see cref="Present"/> spent BLOCKED in the vsync swap. The emu
        /// loop watches this: if it stays near zero the swap isn't actually pacing us (vsync off / sw
        /// raster) and the loop must re-engage its Stopwatch limiter instead of free-running (hazard #2).</summary>
        public double LastSwapMs { get; private set; }

        /// <summary>True when we run Mesa-FIFO pacing (SDL interval 0 + eglSwapInterval 1): FIFO backpressure
        /// caps us at refresh rate, so the loop must NOT add its stopwatch limiter (that fights FIFO and
        /// adds jitter). The loop treats GL present as paced when this is set, regardless of swap-block ms.</summary>
        public bool SelfPaced { get; private set; }

        private bool _bufAgeSupported;
        /// <summary>EGL_BUFFER_AGE_EXT of the buffer being rendered this frame (Phase 0.3 diagnostic). 0 if
        /// unsupported. Age cycling 1↔2 ≈ double-buffered; steady ≥3 ≈ triple+ — distinguishes the present
        /// root-cause hypotheses.</summary>
        public int LastBufferAge { get; private set; }

        // Phase 1.0 cost-attribution toggles: skip ONE stage of the per-frame work INSIDE the real present
        // path (same gate/sleep/focus as normal) to see which stage — if any — recovers a clean 60fps.
        // Diagnostic only; whichever closes the ~55→60 gap is the real cost (none ⇒ it's phase, not work).
        private readonly bool _noUpload = Environment.GetEnvironmentVariable("EMUTASTIC_GL_NOUPLOAD") == "1";
        private readonly bool _noClear  = Environment.GetEnvironmentVariable("EMUTASTIC_GL_NOCLEAR")  == "1";
        private readonly bool _noDraw   = Environment.GetEnvironmentVariable("EMUTASTIC_GL_NODRAW")   == "1";

        // RetroArch-lean draw path (Phase 1): shader+VBO quad + scissor-clear instead of immediate-mode +
        // full glClear, to cut the per-frame GPU work that misses KWin's composite latch (~55→60 windowed).
        // Default on; EMUTASTIC_GL_SHADER=0 reverts to immediate-mode. Falls back automatically if GL2 /
        // shader compile fails. Upload stays synchronous (rotating textures) — RetroArch uploads sync too.
        // Default OFF: the shader+VBO path is correct but did NOT help (~53fps, same as immediate-mode) —
        // the ~55 cap is structural (lock-step/phase), not draw-leanness. Kept behind EMUTASTIC_GL_SHADER=1
        // for A/B, not shipped as default.
        private readonly bool _shaderWanted = Environment.GetEnvironmentVariable("EMUTASTIC_GL_SHADER") == "1";
        private bool _shaderReady;
        private uint _program, _vbo;
        private int _aPos, _aUv;   // attribute locations (fixed via glBindAttribLocation: pos=0, uv=1)

        /// <summary>True when the game window currently holds keyboard focus. An unfocused window gets
        /// throttled by KWin (~47fps) — so if smoothness drops, this tells us whether focus is the cause
        /// (e.g. an overlay stealing it) vs something else.</summary>
        public bool IsFocused => _window != IntPtr.Zero && (SDL_GetWindowFlags(_window) & SDL_WINDOW_INPUT_FOCUS) != 0;

        /// <summary>Current screen position + size of the game window (logical units), so an overlay window
        /// can be positioned over it. False if the window isn't up yet.</summary>
        public bool TryGetWindowRect(out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            if (_window == IntPtr.Zero) return false;
            return SDL_GetWindowPosition(_window, out x, out y) && SDL_GetWindowSize(_window, out w, out h);
        }

        public static GlPresenter? TryCreate(int width, int height, bool fullscreen, out string? error)
        {
            error = null;
            try { return new GlPresenter(width, height, fullscreen); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private GlPresenter(int width, int height, bool fullscreen)
        {
            if (!SDL_InitSubSystem(SDL_INIT_VIDEO)) throw new InvalidOperationException($"SDL_InitSubSystem(VIDEO): {SdlError()}");
            SDL_GL_SetAttribute(SDL_GL_DOUBLEBUFFER, 1);
            SDL_GL_SetAttribute(SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_COMPATIBILITY); // fixed-function quad
            SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 2);
            SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 1);

            ulong flags = SDL_WINDOW_OPENGL | SDL_WINDOW_RESIZABLE;
            _window = SDL_CreateWindow("Emutastic", Math.Max(1, width), Math.Max(1, height), flags);
            if (_window == IntPtr.Zero) throw new InvalidOperationException($"SDL_CreateWindow: {SdlError()}");
            if (fullscreen) SDL_SetWindowFullscreen(_window, true);

            SDL_ShowWindow(_window);
            SDL_RaiseWindow(_window);   // take focus so KWin doesn't throttle us as an inactive surface
            _ctx = SDL_GL_CreateContext(_window);
            if (_ctx == IntPtr.Zero) throw new InvalidOperationException($"SDL_GL_CreateContext: {SdlError()}");
            SDL_GL_MakeCurrent(_window, _ctx);
            // Vsync (interval 1) is the clock. EMUTASTIC_GL_VSYNC=0 disables the blocking swap — a
            // diagnostic for the "swap never returns in-process behind Avalonia" hang: if frames flow
            // with it off, the block was waiting on a vsync/frame-callback that isn't being delivered.
            int want = Environment.GetEnvironmentVariable("EMUTASTIC_GL_VSYNC") == "0" ? 0 : 1;
            // MESA-FIFO mode (default on Wayland): DON'T let SDL throttle the swap. SDL3's Wayland backend,
            // with swap interval > 0, registers a wl_surface frame callback and BLOCKS on it every frame
            // inside SDL_GL_SwapWindow — serializing to ONE frame in flight, so each swap eats a whole
            // vblank and the CPU work after it overruns the next refresh (~50fps). RetroArch instead leaves
            // the swap to Mesa's Wayland WSI FIFO (pipelined: returns as soon as a buffer is free, work
            // overlaps scanout, compositor still presents each frame at exactly one vblank). So: tell SDL
            // interval 0 (no SDL block) but force eglSwapInterval(1) so Mesa keeps doing FIFO. FIFO
            // backpressure (swap blocks once the buffer pool is full) caps us at refresh rate — so we also
            // mark the presenter self-paced and skip the stopwatch, which would only add jitter.
            // Prefer native-Wayland EGL FIFO (eglSwapInterval(1)) and then tell SDL NOT to also throttle the
            // swap (its per-frame frame-callback block serializes us). CRITICAL: on x11/GLX (Xwayland — e.g.
            // when the game-host is launched under the Avalonia app) eglGetCurrentDisplay is null, so we
            // CANNOT set EGL FIFO — in that case we MUST keep SDL's vsync (interval=want), or the swap runs
            // UNSYNCED (tearing + the present thread spinning at hundreds of fps = "less smooth in the app").
            bool mesaFifoReq = want != 0 && Environment.GetEnvironmentVariable("EMUTASTIC_GL_MESA_FIFO") != "0";
            IntPtr edpy = IntPtr.Zero;
            try { edpy = eglGetCurrentDisplay(); } catch { }
            bool mesaFifo = mesaFifoReq && edpy != IntPtr.Zero;   // native-Wayland EGL available
            // Set SDL's interval FIRST — its SetSwapInterval also pushes that value onto the EGL surface.
            // 0 when we'll drive FIFO via EGL ourselves; else `want` (SDL's own vsync — the x11/GLX fallback).
            SDL_GL_SetSwapInterval(mesaFifo ? 0 : want);
            SDL_GL_GetSwapInterval(out int iv);
            // Now, AFTER SDL, force eglSwapInterval(1) so the Mesa surface is FIFO. This MUST come last —
            // doing it before SDL_GL_SetSwapInterval(0) lets SDL clobber it back to 0 (→ swap never blocks →
            // present thread spins at thousands of fps). On x11/GLX edpy is null → mesaFifo=false → we kept
            // SDL's vsync above instead.
            bool eglOk = false; string eglMsg;
            if (mesaFifo)
            {
                try { eglOk = eglSwapInterval(edpy, 1); } catch { }
                eglMsg = $"eglSwapInterval(1)={eglOk} dpy=0x{edpy.ToInt64():X}";
            }
            else eglMsg = edpy == IntPtr.Zero ? "eglGetCurrentDisplay=null (x11/GLX → SDL vsync)" : "mesaFifo off";
            mesaFifo = mesaFifo && eglOk;
            SelfPaced = mesaFifo;
            // Cache the EGL display+surface so Present() can call eglSwapBuffers directly (skip SDL's
            // throttling SwapWindow). Only when self-paced and both handles are valid.
            if (SelfPaced)
            {
                try { _eglDpy = edpy; _eglSurf = eglGetCurrentSurface(EGL_DRAW); } catch { }
                if (_eglDpy == IntPtr.Zero || _eglSurf == IntPtr.Zero) { _eglDpy = IntPtr.Zero; _eglSurf = IntPtr.Zero; }
                // Phase 0.3: only query EGL_BUFFER_AGE_EXT if the display advertises EGL_EXT_buffer_age,
                // else eglQuerySurface returns false and the value is meaningless.
                try { var ext = Marshal.PtrToStringAnsi(eglQueryString(_eglDpy, EGL_EXTENSIONS)) ?? ""; _bufAgeSupported = _eglSurf != IntPtr.Zero && ext.Contains("EGL_EXT_buffer_age"); } catch { }
            }
            string vdrv = "?";
            try { var p = SDL_GetCurrentVideoDriver(); if (p != IntPtr.Zero) vdrv = Marshal.PtrToStringUTF8(p) ?? "?"; } catch { }
            System.Diagnostics.Trace.WriteLine($"[Gl] window {width}x{height} fullscreen={fullscreen} videoDriver={vdrv} mesaFifo={mesaFifo} selfPaced={SelfPaced} swapInterval={iv} (wanted {want}) [{eglMsg}]");

            for (int i = 0; i < TexCount; i++)
            {
                glGenTextures(1, out _texes[i]);
                glBindTexture(GL_TEXTURE_2D, _texes[i]);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);   // crisp pixels
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
            }
            glEnable(GL_TEXTURE_2D);
            glClearColor(0, 0, 0, 1);

            if (_shaderWanted) SetupShaderPath();
            System.Diagnostics.Trace.WriteLine($"[Gl] shader path: wanted={_shaderWanted} ready={_shaderReady}");
        }

        // Compile a GLSL 1.20 program + a static quad VBO (NDC full-quad in the fit-rect viewport; UV with
        // the vertical flip baked in, matching the immediate-mode texcoords). On any failure, _shaderReady
        // stays false and Present falls back to immediate-mode.
        private void SetupShaderPath()
        {
            if (!LoadGl2()) return;
            const string vs = "#version 120\nattribute vec2 aPos;\nattribute vec2 aUv;\nvarying vec2 vUv;\nvoid main(){ vUv = aUv; gl_Position = vec4(aPos, 0.0, 1.0); }\n";
            const string fs = "#version 120\nvarying vec2 vUv;\nuniform sampler2D uTex;\nvoid main(){ gl_FragColor = texture2D(uTex, vUv); }\n";
            uint v = CompileShader(GL_VERTEX_SHADER, vs), f = CompileShader(GL_FRAGMENT_SHADER, fs);
            if (v == 0 || f == 0) return;
            _program = glCreateProgram();
            glAttachShader(_program, v); glAttachShader(_program, f);
            glBindAttribLocation(_program, 0, "aPos"); glBindAttribLocation(_program, 1, "aUv");
            glLinkProgram(_program);
            glGetProgramiv(_program, GL_LINK_STATUS, out int linked);
            if (linked == 0) { System.Diagnostics.Trace.WriteLine("[Gl] program link FAILED — fixed-function fallback"); return; }
            _aPos = 0; _aUv = 1;

            // Quad as a TRIANGLE_STRIP: TL, BL, TR, BR. pos in NDC [-1,1] (viewport = fit-rect), uv flipped
            // (t=0 → top) so the top-down BGRA frame is upright — same mapping as the old immediate-mode quad.
            float[] quad = { -1f, 1f, 0f, 0f,   -1f, -1f, 0f, 1f,   1f, 1f, 1f, 0f,   1f, -1f, 1f, 1f };
            glGenBuffers(1, out _vbo);
            glBindBuffer(GL_ARRAY_BUFFER, _vbo);
            fixed (float* q = quad) glBufferData(GL_ARRAY_BUFFER, (IntPtr)(quad.Length * sizeof(float)), (IntPtr)q, GL_STATIC_DRAW);
            glBindBuffer(GL_ARRAY_BUFFER, 0);

            glUseProgram(_program);
            int loc = glGetUniformLocation(_program, "uTex");
            if (loc >= 0) glUniform1i(loc, 0);   // sampler → texture unit 0
            glUseProgram(0);
            _shaderReady = true;
        }

        private uint CompileShader(uint type, string src)
        {
            uint s = glCreateShader(type);
            glShaderSource(s, 1, new[] { src }, null);
            glCompileShader(s);
            glGetShaderiv(s, GL_COMPILE_STATUS, out int ok);
            if (ok == 0)
            {
                var log = new byte[1024]; glGetShaderInfoLog(s, log.Length, out int n, log);
                System.Diagnostics.Trace.WriteLine($"[Gl] shader compile FAILED: {System.Text.Encoding.ASCII.GetString(log, 0, Math.Max(0, Math.Min(n, log.Length)))}");
                return 0;
            }
            return s;
        }

        /// <summary>Drain + dispatch the window's events (keeps the compositor serviced AND delivers input).
        /// Must be called on the thread that owns the window. Present() calls this; the decoupled present
        /// thread also calls it directly when there's no frame yet to draw.</summary>
        public void PumpEvents()
        {
            if (_window == IntPtr.Zero) return;
            while (SDL_PollEvent(_evBuf))
            {
                uint type = BitConverter.ToUInt32(_evBuf, 0);
                switch (type)
                {
                    case SDL_EVENT_QUIT:
                    case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                        CloseRequested = true;
                        break;
                    case SDL_EVENT_KEY_DOWN:
                    case SDL_EVENT_KEY_UP:
                        // SDL_KeyboardEvent layout: type(u32)@0, reserved(u32)@4, timestamp(u64)@8,
                        // windowID(u32)@16, which(u32)@20, scancode(u32)@24, key(u32)@28, mod(u16)@32,
                        // raw(u16)@34, down(bool)@36, repeat(bool)@37.
                        if (_evBuf[37] == 0)   // ignore auto-repeat; we only want real transitions
                            KeyEvent?.Invoke((int)BitConverter.ToUInt32(_evBuf, 24), type == SDL_EVENT_KEY_DOWN);
                        break;
                    case SDL_EVENT_MOUSE_MOTION:
                        // SDL_MouseMotionEvent: x float@28, y float@32 (window coords).
                        UpdateMouse(BitConverter.ToSingle(_evBuf, 28), BitConverter.ToSingle(_evBuf, 32));
                        MouseMoved?.Invoke();
                        break;
                    case SDL_EVENT_MOUSE_BUTTON_DOWN:
                    case SDL_EVENT_MOUSE_BUTTON_UP:
                        // SDL_MouseButtonEvent: button u8@24 (1=L 2=M 3=R), x float@28, y float@32.
                        UpdateMouse(BitConverter.ToSingle(_evBuf, 28), BitConverter.ToSingle(_evBuf, 32));
                        int btn = _evBuf[24] switch { 1 => 0, 3 => 1, 2 => 2, _ => -1 };
                        if (btn >= 0) PointerButton?.Invoke(btn, type == SDL_EVENT_MOUSE_BUTTON_DOWN);
                        break;
                    case SDL_EVENT_WINDOW_MOUSE_LEAVE: MouseInside = false; MouseLeft?.Invoke(); break;
                }
            }
        }

        /// <summary>DIAGNOSTIC: pump events + swap ONLY (no upload/draw/clear). Used to test whether the
        /// ~55fps cap is the per-frame GPU/draw work (then this hits a clean 60) or the FIFO/compositor
        /// itself (then this still sits at ~55). EMUTASTIC_GL_SWAPONLY=1.</summary>
        public bool SwapOnly()
        {
            if (_window == IntPtr.Zero) return false;
            PumpEvents();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ok = (_eglDpy != IntPtr.Zero && _eglSurf != IntPtr.Zero)
                ? eglSwapBuffers(_eglDpy, _eglSurf)
                : SDL_GL_SwapWindow(_window);
            LastSwapMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return ok;
        }

        /// <summary>Upload one BGRA frame, draw it aspect-fit, and swap (blocks to vsync → paces the loop).</summary>
        public bool Present(byte[] bgra, int frameW, int frameH)
        {
            if (_window == IntPtr.Zero || frameW <= 0 || frameH <= 0) return false;
            PumpEvents();
            // Phase 0.3 diagnostic: age of the buffer we're about to render into.
            if (_bufAgeSupported && eglQuerySurface(_eglDpy, _eglSurf, EGL_BUFFER_AGE_EXT, out int age)) LastBufferAge = age;

            // (context is already current on this emu thread since construction; the per-frame
            // SDL_GL_MakeCurrent here was redundant and eglMakeCurrent can force a driver sync —
            // dropped to stop serializing ~a few ms of work on top of every vsync swap.)
            // Advance the rotation so this upload targets a texture the GPU isn't still reading for the
            // previous present (avoids the upload→draw serialization stall).
            _texIndex = (_texIndex + 1) % TexCount;
            glBindTexture(GL_TEXTURE_2D, _texes[_texIndex]);
            glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
            if (!_noUpload)   // attribution toggle: skip the synchronous texture upload
            fixed (byte* p = bgra)
            {
                if (frameW != _texW[_texIndex] || frameH != _texH[_texIndex])
                {
                    glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, frameW, frameH, 0, GL_BGRA, GL_UNSIGNED_BYTE, (IntPtr)p);
                    _texW[_texIndex] = frameW; _texH[_texIndex] = frameH;
                }
                else
                {
                    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, frameW, frameH, GL_BGRA, GL_UNSIGNED_BYTE, (IntPtr)p);
                }
            }

            SDL_GetWindowSizeInPixels(_window, out int winW, out int winH);
            if (winW <= 0 || winH <= 0) { winW = frameW; winH = frameH; }
            // Game fit geometry — mirrors wl_present.c: fit in the content area (window minus the
            // chrome insets); when a bezel is up, its aspect-fit rect becomes the game's fit
            // CONTAINER so the game lands in the art's transparent cutout whatever the window
            // shape; the game keeps the DISPLAY aspect (DAR), not the frame pixel ratio.
            int contentH = Math.Max(1, winH - _insetTop - _insetBottom);
            double dar = _dar > 0.0 ? _dar : (double)frameW / frameH;
            bool bezOn = _bezOn && _bezTex != 0 && _bezW > 0 && _bezH > 0;
            int bx = 0, by = _insetBottom, bw = winW, bh = contentH;
            if (bezOn)
            {
                double bar = (double)_bezW / _bezH;
                if ((double)winW / contentH > bar) { bh = contentH; bw = (int)(contentH * bar + 0.5); }
                else { bw = winW; bh = (int)(winW / bar + 0.5); }
                bx = (winW - bw) / 2; by = _insetBottom + (contentH - bh) / 2;
            }
            int gcw = bezOn ? bw : winW, gch = bezOn ? bh : contentH;   // game fit container
            int fw, fh;
            if ((double)gcw / gch > dar) { fh = gch; fw = (int)(gch * dar + 0.5); }
            else { fw = gcw; fh = (int)(gcw / dar + 0.5); }
            int x0 = bx + (gcw - fw) / 2, y0 = by + (gch - fh) / 2;
            bool useShader = _shaderReady && _shaderWanted;

            // Clear: shader path scissor-clears ONLY the letterbox bars (cheap); fallback does a full clear.
            if (!_noClear)
            {
                if (useShader)
                {
                    glEnable(GL_SCISSOR_TEST);
                    if (x0 > 0)            { glScissor(0, 0, x0, winH);              glClear(GL_COLOR_BUFFER_BIT); }       // left
                    if (x0 + fw < winW)    { glScissor(x0 + fw, 0, winW - x0 - fw, winH); glClear(GL_COLOR_BUFFER_BIT); }  // right
                    if (y0 > 0)            { glScissor(x0, 0, fw, y0);               glClear(GL_COLOR_BUFFER_BIT); }       // bottom
                    if (y0 + fh < winH)    { glScissor(x0, y0 + fh, fw, winH - y0 - fh); glClear(GL_COLOR_BUFFER_BIT); }   // top
                    glDisable(GL_SCISSOR_TEST);
                }
                else { glViewport(0, 0, winW, winH); glClear(GL_COLOR_BUFFER_BIT); }
            }

            glViewport(x0, y0, fw, fh);

            if (!_noDraw)
            {
                if (useShader)
                {
                    glUseProgram(_program);
                    glActiveTexture(GL_TEXTURE0);
                    glBindTexture(GL_TEXTURE_2D, _texes[_texIndex]);
                    glBindBuffer(GL_ARRAY_BUFFER, _vbo);
                    glVertexAttribPointer((uint)_aPos, 2, GL_FLOAT, 0, 4 * sizeof(float), (IntPtr)0);
                    glVertexAttribPointer((uint)_aUv, 2, GL_FLOAT, 0, 4 * sizeof(float), (IntPtr)(2 * sizeof(float)));
                    glEnableVertexAttribArray((uint)_aPos);
                    glEnableVertexAttribArray((uint)_aUv);
                    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
                    glBindBuffer(GL_ARRAY_BUFFER, 0);
                    glUseProgram(0);
                }
                else
                {
                    // Fixed-function fallback: textured quad, texcoord t=0 → top so the frame shows upright.
                    glBegin(GL_QUADS);
                    glTexCoord2f(0, 0); glVertex2f(-1, 1);
                    glTexCoord2f(1, 0); glVertex2f(1, 1);
                    glTexCoord2f(1, 1); glVertex2f(1, -1);
                    glTexCoord2f(0, 1); glVertex2f(-1, -1);
                    glEnd();
                }
            }

            // Deco layers (shim draw order): Vectrex screen art stretched over the GAME rect,
            // then the bezel frame at its own rect — its transparent center frames the game.
            if (_govOn && _govTex != 0)
            {
                glViewport(x0, y0, fw, fh);
                DrawBlendedQuad(_govTex);
            }
            if (bezOn)
            {
                glViewport(bx, by, bw, bh);
                DrawBlendedQuad(_bezTex);
            }

            // OSD overlay last: window-sized straight-alpha quad over everything (status line, HUD
            // pill, cog menu, toasts — same buffer the Wayland shim composites). Fixed-function on
            // purpose: the 2.1 compatibility context always has it, independent of the shader path.
            if (_osdVisible && _osdTex != 0)
            {
                glViewport(0, 0, winW, winH);
                DrawBlendedQuad(_osdTex);
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // Direct EGL present (Mesa FIFO, pipelined) when self-paced; else SDL's blocking swap.
            bool ok = (_eglDpy != IntPtr.Zero && _eglSurf != IntPtr.Zero)
                ? eglSwapBuffers(_eglDpy, _eglSurf)
                : SDL_GL_SwapWindow(_window);
            LastSwapMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return ok;
        }

        // Window coords (points) -> pixels for OSD hit-testing.
        private void UpdateMouse(float wx, float wy)
        {
            double sx = 1, sy = 1;
            if (SDL_GetWindowSize(_window, out int lw, out int lh)
                && SDL_GetWindowSizeInPixels(_window, out int pw, out int ph) && lw > 0 && lh > 0)
            { sx = (double)pw / lw; sy = (double)ph / lh; }
            MouseX = (int)Math.Round(wx * sx);
            MouseY = (int)Math.Round(wy * sy);
            MouseInside = true;
        }

        public void GetSize(out int w, out int h)
        {
            w = h = 0;
            if (_window != IntPtr.Zero) SDL_GetWindowSizeInPixels(_window, out w, out h);
        }

        public bool IsMaximized => _window != IntPtr.Zero && (SDL_GetWindowFlags(_window) & SDL_WINDOW_MAXIMIZED) != 0;

        // ── OSD overlay quad (window-sized straight-alpha RGBA8, row 0 = top) ───────────────────
        private uint _osdTex;
        private int _osdW, _osdH;
        private bool _osdVisible;

        public void SetOverlay(IntPtr rgba, int w, int h)
        {
            if (_window == IntPtr.Zero) return;
            if (rgba == IntPtr.Zero || w <= 0 || h <= 0) { _osdVisible = false; return; }
            if (_osdTex == 0)
            {
                glGenTextures(1, out _osdTex);
                glBindTexture(GL_TEXTURE_2D, _osdTex);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);   // 1:1 window-sized
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
            }
            else glBindTexture(GL_TEXTURE_2D, _osdTex);
            glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
            if (w != _osdW || h != _osdH)
            {
                glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
                _osdW = w; _osdH = h;
            }
            else glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
            _osdVisible = true;
        }

        // ── chrome insets: reserve top/bottom strips (status bar) out of the game fit-rect ─────
        private int _insetTop, _insetBottom;
        public void SetInsets(int top, int bottom) { _insetTop = Math.Max(0, top); _insetBottom = Math.Max(0, bottom); }

        // Native WM owns the chrome on this path — window-management calls are no-ops, and the
        // shim-only extras report unsupported (see the capability flags up top).
        // Display aspect ratio for the game fit-rect (0 → frame pixel ratio), like the shim.
        private double _dar;
        public void SetAspect(double dar) { _dar = dar; }

        public void Minimize() { }
        public void ToggleMaximize() { }
        public void StartMove() { }
        public void StartResize(int edge) { }
        public void SetCursorShape(int shape) { }

        // ── Static deco layers (mirrors wl_present.c): bezel frame aspect-fit in the content
        //    area becomes the game's fit CONTAINER (its transparent cutout frames the game);
        //    the Vectrex screen art stretches over the game rect, alpha-blended. LINEAR filtering
        //    — these are photographic art scaled to arbitrary window sizes. ─────────────────────
        private uint _bezTex, _govTex;
        private int _bezW, _bezH;
        private bool _bezOn, _govOn;

        private uint UploadDeco(uint tex, byte[] rgba, int w, int h)
        {
            if (tex == 0)
            {
                glGenTextures(1, out tex);
                glBindTexture(GL_TEXTURE_2D, tex);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
            }
            else glBindTexture(GL_TEXTURE_2D, tex);
            glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
            fixed (byte* pput = rgba)
                glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, (IntPtr)pput);
            return tex;
        }

        public void SetBezel(byte[] rgba, int w, int h)
        {
            if (_window == IntPtr.Zero || rgba == null || w <= 0 || h <= 0) return;
            _bezTex = UploadDeco(_bezTex, rgba, w, h); _bezW = w; _bezH = h;
        }
        public void ShowBezel(bool on) => _bezOn = on;

        public void SetGameOverlay(byte[] rgba, int w, int h)
        {
            if (_window == IntPtr.Zero || rgba == null || w <= 0 || h <= 0) return;
            _govTex = UploadDeco(_govTex, rgba, w, h);
        }
        public void ShowGameOverlay(bool on) => _govOn = on;

        // Blended fullscreen quad into the CURRENT viewport (deco layers + OSD share this).
        private static void DrawBlendedQuad(uint tex)
        {
            glBindTexture(GL_TEXTURE_2D, tex);
            glEnable(GL_BLEND);
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
            glColor4f(1, 1, 1, 1);
            glBegin(GL_QUADS);
            glTexCoord2f(0, 0); glVertex2f(-1, 1);
            glTexCoord2f(1, 0); glVertex2f(1, 1);
            glTexCoord2f(1, 1); glVertex2f(1, -1);
            glTexCoord2f(0, 1); glVertex2f(-1, -1);
            glEnd();
            glDisable(GL_BLEND);
        }
        public void SetShader(int preset) { }
        public bool SetGlslp(string? presetPath) => false;
        public void RequestCapture() { }
        public bool TryTakeCapture(byte[] buf, out int w, out int h) { w = h = 0; return false; }

        public void SetFullscreen(bool fullscreen)
        {
            if (_window != IntPtr.Zero) SDL_SetWindowFullscreen(_window, fullscreen);
        }

        public void Dispose()
        {
            if (_ctx != IntPtr.Zero)
            {
                SDL_GL_MakeCurrent(_window, _ctx);
                for (int i = 0; i < TexCount; i++)
                    if (_texes[i] != 0) { glDeleteTextures(1, ref _texes[i]); _texes[i] = 0; }
                if (_osdTex != 0) { glDeleteTextures(1, ref _osdTex); _osdTex = 0; }
                if (_bezTex != 0) { glDeleteTextures(1, ref _bezTex); _bezTex = 0; }
                if (_govTex != 0) { glDeleteTextures(1, ref _govTex); _govTex = 0; }
            }
            if (_ctx != IntPtr.Zero) { SDL_GL_DestroyContext(_ctx); _ctx = IntPtr.Zero; }
            if (_window != IntPtr.Zero) { SDL_DestroyWindow(_window); _window = IntPtr.Zero; }
            SDL_QuitSubSystem(SDL_INIT_VIDEO);
        }
    }
}
