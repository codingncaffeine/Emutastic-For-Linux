using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Emutastic.Platform;
using Emutastic.Services;
using Emutastic.Services.ConsoleHandlers;

namespace Emutastic.Emulator
{
    /// <summary>
    /// Minimal libretro runtime for the M2 vertical slice: loads a core + ROM, wires the six
    /// libretro callbacks, services the essential environment commands, and drives retro_run on a
    /// dedicated thread paced by the core's fps + SDL audio backpressure. Video frames are
    /// converted to BGRA and exposed via <see cref="TrySnapshot"/> for the UI to blit.
    ///
    /// This is the software-readback path (the upstream production fallback). HW rendering
    /// (GL/Vulkan via a NativeControlHost child surface) is M6 — this class is structured so the
    /// frame sink is swappable. Core options (CoreOptionsService) and the full environment surface
    /// land in M3+.
    /// </summary>
    public sealed class EmulatorSession : IDisposable
    {
        // ---- libretro environment command numbers (libretro.h) ----
        const uint ENV_SET_ROTATION = 1;   // core requests screen rotation (value × 90° CCW)
        const uint ENV_GET_OVERSCAN = 2;
        const uint ENV_GET_CAN_DUPE = 3;
        const uint ENV_SET_PERFORMANCE_LEVEL = 8;
        const uint ENV_GET_SYSTEM_DIRECTORY = 9;
        const uint ENV_SET_PIXEL_FORMAT = 10;
        const uint ENV_GET_VARIABLE = 15;
        const uint ENV_SET_VARIABLES = 16;
        const uint ENV_GET_VARIABLE_UPDATE = 17;
        const uint ENV_GET_CORE_OPTIONS_VERSION = 52;
        const uint ENV_GET_LOG_INTERFACE = 27;
        const uint ENV_GET_CORE_ASSETS_DIRECTORY = 30;
        const uint ENV_GET_SAVE_DIRECTORY = 31;
        const uint ENV_SET_DISK_CONTROL_INTERFACE = 13;
        const uint ENV_SET_DISK_CONTROL_EXT_INTERFACE = 58;
        // libretro OR's these flags into command IDs; mask them off before switching.
        const uint RETRO_ENVIRONMENT_EXPERIMENTAL = 0x10000;
        const uint RETRO_ENVIRONMENT_PRIVATE = 0x20000;

        private readonly string _corePath, _romPath;
        private LibretroCore? _core;
        private SdlAudio? _audio;
        private readonly SdlInput _input;

        // keep delegates alive for the lifetime of the core
        private readonly retro_environment_t _envCb;
        private readonly retro_video_refresh_t _videoCb;
        private readonly retro_audio_sample_t _audioCb;
        private readonly retro_audio_sample_batch_t _audioBatchCb;
        private readonly retro_input_poll_t _inputPollCb;
        private readonly retro_input_state_t _inputStateCb;

        // Persistent ANSI pointers handed to the core for its lifetime (freed in Dispose).
        private IntPtr _systemDirPtr, _saveDirPtr, _coreAssetsDirPtr;
        private readonly retro_log_printf_t _logCb; // kept alive; handed to the core via GET_LOG_INTERFACE
        private int _pixelFormat = 0; // 0=0RGB1555, 1=XRGB8888, 2=RGB565
        private double _fps = 60.0, _sampleRate = 44100;

        // libretro disk-control interface (FDS / multi-disc). The core hands us these callbacks via
        // SET_DISK_CONTROL_INTERFACE; we use them to insert disk 0 after load (FDS boots ejected →
        // the BIOS otherwise sits on "Set the Disk Card").
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetEjectStateFn(bool ejected);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetImageIndexFn(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool GetEjectStateFn();
        private SetEjectStateFn? _setEjectState;
        private SetImageIndexFn? _setImageIndex;
        private GetEjectStateFn? _getEjectState;

        [StructLayout(LayoutKind.Sequential)]
        private struct retro_disk_control_callback   // first 7 fields are shared with the EXT version
        {
            public IntPtr set_eject_state, get_eject_state, get_image_index,
                          set_image_index, get_num_images, replace_image_index, add_image_index;
        }

        private Thread? _thread;
        private volatile bool _running;
        private volatile bool _paused;
        private volatile bool _resetRequested;

        /// <summary>Pause/resume the emulation (frame freezes, audio goes silent). UI-thread safe.</summary>
        public bool IsPaused => _paused;
        public void SetPaused(bool paused) => _paused = paused;
        /// <summary>Request a core reset; applied on the emu thread to avoid racing retro_run.</summary>
        public void RequestReset() => _resetRequested = true;

        // latest converted frame (BGRA8888), guarded by _frameLock. Buffers are REUSED, not allocated
        // per frame: a fresh 245KB/frame alloc at 60fps churned the Large Object Heap (~15MB/s) and
        // triggered ~4 gen2 GC pauses/sec → visible stutter. _convBuf is the emu thread's working buffer;
        // it's swapped with _frame under the lock (zero-copy); TrySnapshot copies _frame → _uiBuf (UI-only)
        // under the lock so the emu can keep reusing buffers without racing the blit.
        private readonly object _frameLock = new();
        private byte[]? _frame;       // front buffer (most recent complete frame)
        private byte[]? _convBuf;     // emu working buffer (filled by Video_cb, then swapped into _frame)
        private byte[]? _uiBuf;       // UI copy target (TrySnapshot writes it, PumpFrame reads it)
        private int _frameW, _frameH;
        private volatile int _rotationDeg;   // 0/90/180/270, set by ENV_SET_ROTATION
        private long _frameSeq;
        private int _frameCountSample;            // frames produced since the last SampleStats (real fps)
        private long _coreRunTicks, _coreRunCalls; // accumulated retro_run time + call count for avg ms

        public string CoreName => _core?.CoreName ?? "?";
        public SdlInput Input => _input;

        /// <summary>Display aspect ratio to render at (handler override, else core/geometry). 0 = use
        /// the frame's pixel ratio. e.g. TG16 forces 4:3 regardless of the core's reported geometry.</summary>
        public double DisplayAspectRatio { get; private set; }

        /// <summary>The emulation loop's target frame rate (core-reported or handler-forced).</summary>
        public double TargetFps => _fps;

        /// <summary>Raised on the emu thread when a new frame has been published. The window presents
        /// on this (push) — paced by the core, a single clock — instead of pulling on a timer.</summary>
        public event Action? FrameReady;

        // ── Vulkan present integration (see docs/frame-pacing-and-vsync.md) ──
        // OPT-IN (EMUTASTIC_VULKAN=1). A dedicated present thread floats a borderless top-level Vulkan
        // window (VkOverlay) over the Avalonia window's video viewport — the upstream WS_POPUP model and
        // the ONLY config that hit clean vsync on KWin/Xwayland (a reparented child = ~28fps). Emulation
        // stays on its own steady Stopwatch thread; the overlay present is vsync-paced and decoupled.
        // The UI thread only feeds a target screen rect + fullscreen flag. Any failure → WriteableBitmap.
        private VkOverlay? _overlay;
        private volatile bool _ovHasTarget;
        private volatile int _ovX, _ovY, _ovW = 1280, _ovH = 720;
        private volatile bool _ovFullscreen;
        private volatile uint _ovGeomGen;     // bumped by the UI thread on any geometry/state change
        private uint _ovGeomApplied;          // last gen applied (emu thread)
        private bool _overlayTried;           // one-shot overlay-create attempt
        private volatile bool _vulkanOk;
        private double _presentMsEma;         // smoothed present-block time (instrumentation + pace gate)
        private double _frameMaxMs;           // worst frame-to-frame time this log interval (jitter peak)
        private int _frameHitches;            // frames >1.5× refresh this interval (periodic-stall detector)
        private double _frameMsEma;           // smoothed frame-to-frame period (cadence readout)

        // Frame-PACING method (EMUTASTIC_PACING) — the lever for in-game smoothness (see
        // focus-on-pacing-method): "stopwatch" (default, high-res timer to 60.0), "audio" (sound-clock:
        // pace by the audio device draining one frame — perfectly steady, true 60.0988 rate, no timer
        // wobble), "spin" (pure busy-spin to the budget, lowest timer wobble). A/B on the real machine.
        private readonly string _pacing = (Environment.GetEnvironmentVariable("EMUTASTIC_PACING") ?? "stopwatch").Trim().ToLowerInvariant();
        // Audio DRC is OPT-IN (EMUTASTIC_DRC=1). It was added this session and regressed 2D smoothness
        // (matches the memory: a prior DRC attempt made jitter WORSE). Default OFF restores the pre-Vulkan
        // behavior: plain Stopwatch + the smooth TIME-BASED audio estimate for the guards.
        private readonly bool _drc = Environment.GetEnvironmentVariable("EMUTASTIC_DRC") == "1";

        // ── GL present (the proven RetroArch model: own SDL3-GL window, vsync swap = the clock) ──
        // EMUTASTIC_PRESENT = writeable (default) | vulkan | gl. "gl" is OPT-IN while we debug an
        // in-process hang: the emu thread owns a focused GlPresenter window and presents each produced
        // frame through it, the BLOCKING vsync swap pacing the loop (none of the Stopwatch/audio/overlay
        // pacing runs). Window creation + present #1 work, but the loop then parks — suspected interaction
        // between SDL's GL context and Avalonia's Mesa renderer in one process. Default stays on the known-
        // good WriteableBitmap path so a normal launch is never affected until the GL path is proven.
        private readonly string _present = (Environment.GetEnvironmentVariable("EMUTASTIC_PRESENT") ?? "writeable").Trim().ToLowerInvariant();
        private GlPresenter? _gl;
        private bool _glFullscreen;
        private long _glPresents;   // bring-up diagnostic: count of GL presents (heartbeat / first-present log)
        private double _glSwapMsEma; // smoothed vsync-swap block time → no-vsync-fallback gate (hazard #2)
        // GL "spike model": exactly one retro_run per present (vsync = the only clock). The audio
        // backpressure skip / low-watermark catch-up runs the core a VARIABLE number of times per present,
        // which jitters the swap rhythm — the spike (run-once-present) was the only smooth config. Default
        // ON for the GL path; EMUTASTIC_GL_SIMPLE=0 reverts to the old variable loop for A/B.
        private readonly bool _glSimpleEnabled = Environment.GetEnvironmentVariable("EMUTASTIC_GL_SIMPLE") != "0";
        // DECOUPLED present (EMUTASTIC_GL_PRESENT_THREAD=1): RetroArch's pacing model. The emu thread runs
        // the core at real-time, paced by AUDIO backpressure (block until the device drains the frame we
        // just produced — RetroArch's audio_sync). A separate PRESENT thread owns the GL window and shows
        // the latest frame at vsync. A missed vblank then just repeats a frame instead of slowing the core,
        // so emulation speed/audio stay correct regardless of present hitches. Relaunch to A/B vs the
        // single-threaded swap-is-the-clock path. DEFAULT ON; EMUTASTIC_GL_PRESENT_THREAD=0 reverts to the
        // old single-threaded path for A/B.
        private readonly bool _presentThreadMode = Environment.GetEnvironmentVariable("EMUTASTIC_GL_PRESENT_THREAD") != "0";
        private byte[]? _presentBuf;   // present-thread-owned copy of the latest frame (decoupled mode)
        // DIAGNOSTIC ONLY (EMUTASTIC_NO_INPUTPOLL=1): skip the per-frame SDL pump/gamepad update to test
        // whether per-frame input polling is what jitters the present. Game won't respond to input.
        private readonly bool _noInputPoll = Environment.GetEnvironmentVariable("EMUTASTIC_NO_INPUTPOLL") == "1";
        // Spike-comparable smoothness stats for the GL path (mean/stddev/min/max over a ~5s window).
        private double _glStatSum, _glStatSumSq, _glStatMin = double.MaxValue, _glStatMax, _glStatWorkMax;
        private int _glStatCount, _glStatGc2Base = -1;

        // Battery save-RAM (.srm) persistence. Loaded from disk after the core boots, autosaved every
        // few seconds while running, and flushed on exit — there was NO save persistence before, and
        // Dispose() can leak a hung core, so periodic autosave (not just flush-on-exit) is the safety net.
        private string? _srmPath;
        private long _srmAutoSaveTick;     // loop counter for the periodic autosave cadence
        private byte[]? _lastSrm;          // last bytes written, to skip unchanged writes

        /// <summary>True while a game runs in a SEPARATE host process (set by the parent's launcher).
        /// The parent's ControllerManager must stop pumping SDL gamepads while this is set, since the
        /// in-process <see cref="AnyActive"/> guard can't see a child process holding the same pads.</summary>
        public static volatile bool ExternalGameActive;

        /// <summary>Mouse moved inside / left the GL game window (raised on the emu thread). An overlay
        /// uses these to hover-reveal then auto-hide, so nothing shows during normal play.</summary>
        public event Action? GameMouseMoved;
        public event Action? GameMouseLeft;
        private void OnGlMouseMoved() => GameMouseMoved?.Invoke();
        private void OnGlMouseLeft() => GameMouseLeft?.Invoke();

        /// <summary>Ask the loop to stop and exit cleanly (flushes SRAM). Used by the host's quit signals
        /// (stdin EOF / SIGTERM) — distinct from Dispose, which also tears down native resources.</summary>
        public void RequestQuit() => _running = false;

        /// <summary>Blocks until the emulation thread has exited (the GL window closed or RequestQuit).
        /// The game host calls this on its main thread to keep the process alive for the session.</summary>
        public void WaitForExit() => _thread?.Join();

        private bool _runInline;
        /// <summary>Like <see cref="Start"/>, but runs the emulation loop + GL window on the CALLING thread
        /// (blocks until the game exits) instead of a background thread — the spike model, which the screen-
        /// sync prefers on Linux. The game host calls this on its main thread.</summary>
        public bool RunInline(out string? error)
        {
            _runInline = true;
            return Start(out error);   // Start() runs RunLoop() inline and returns once the game exits
        }

        /// <summary>Screen rect of the GL game window (for positioning an overlay over it). False if the
        /// GL window isn't up (or not in GL mode).</summary>
        public bool TryGetGameWindowRect(out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            return _gl != null && _gl.TryGetWindowRect(out x, out y, out w, out h);
        }

        // SDL3 scancode → libretro player-1 joypad id (defaults; mirrors EmulatorWindow.KeyMap). Per-console
        // configured keybindings are honored on the gamepad path already; wiring the GL keyboard to the
        // Controls panel is a follow-up — these defaults keep a ROM playable from the keyboard meanwhile.
        private static readonly Dictionary<int, int> _glKeyMap = new()
        {
            { 82, 4 }, { 81, 5 }, { 80, 6 }, { 79, 7 },   // Up / Down / Left / Right
            { 29, 0 }, { 27, 8 }, { 4, 1 }, { 22, 9 },     // Z=B, X=A, A=Y, S=X
            { 40, 3 }, { 229, 2 }, { 20, 10 }, { 26, 11 }, // Enter=START, RShift=SELECT, Q=L, W=R
        };
        const int SC_ESCAPE = 41, SC_F11 = 68, SC_P = 19;

        // Emu-thread handler for the GL window's keyboard. Game buttons feed SdlInput's player-1 fallback;
        // a few non-game scancodes drive the session (quit / fullscreen / pause).
        private void OnGlKey(int scancode, bool down)
        {
            if (_glKeyMap.TryGetValue(scancode, out int id)) { _input.SetKeyboardButton(id, down); return; }
            if (!down) return;
            switch (scancode)
            {
                case SC_ESCAPE: _running = false; break;
                case SC_F11:    _glFullscreen = !_glFullscreen; _gl?.SetFullscreen(_glFullscreen); break;
                case SC_P:      _paused = !_paused; break;
            }
        }

        /// <summary>UI thread feeds the overlay's target: the video viewport's screen position + pixel
        /// size and whether the window is fullscreen. Cheap; the present thread applies changes.</summary>
        public void SetOverlayGeometry(int screenX, int screenY, int pixelW, int pixelH, bool fullscreen)
        {
            _ovX = screenX; _ovY = screenY;
            if (pixelW > 0) _ovW = pixelW;
            if (pixelH > 0) _ovH = pixelH;
            _ovFullscreen = fullscreen;
            _ovHasTarget = true;
            unchecked { _ovGeomGen++; }
        }

        public bool VulkanPresentActive => _vulkanOk;

        /// <summary>Resolved on the present thread: true = Vulkan overlay active (hide the WriteableBitmap
        /// Image), false = using the WriteableBitmap path.</summary>
        public event Action<bool>? PresenterResolved;

        /// <summary>Sample-and-reset the real fps + average retro_run time since the last call
        /// (drives the bottom status bar). Safe to call from the UI thread.</summary>
        public void SampleStats(out int frames, out double avgRunMs)
        {
            frames = System.Threading.Interlocked.Exchange(ref _frameCountSample, 0);
            long ticks = System.Threading.Interlocked.Exchange(ref _coreRunTicks, 0);
            long calls = System.Threading.Interlocked.Exchange(ref _coreRunCalls, 0);
            avgRunMs = calls > 0 ? (double)ticks / calls / Stopwatch.Frequency * 1000.0 : 0;
        }

        private readonly string _console;

        // Per-console handler (core options, controller ports, aspect/fps, dirs) — keeps each console
        // segregated so one console's quirks can't break another. See ConsoleHandlers/.
        private readonly IConsoleHandler _handler;
        // Resolved core options the core reads via GET_VARIABLE. Seeded from the handler, then filled
        // in from each SET_VARIABLES announcement (first valid value when not pre-seeded).
        private readonly Dictionary<string, string> _coreOptions = new();
        // Persistent ANSI value pointers handed to the core via GET_VARIABLE (it keeps the pointer).
        // _coreOptionPtrs is the current ptr per key (for the reuse check); _allocatedOptionPtrs is
        // EVERY ptr ever handed out — we never free one mid-session (a core may still hold an old one
        // → use-after-free), only at session end. Matches upstream's deliberate keep-alive.
        private readonly Dictionary<string, IntPtr> _coreOptionPtrs = new();
        private readonly List<IntPtr> _allocatedOptionPtrs = new();
        private volatile bool _coreOptionsDirty;   // false until SET_VARIABLES announces options (upstream parity)

        [StructLayout(LayoutKind.Sequential)]
        private struct retro_variable { public IntPtr key; public IntPtr value; }

        public EmulatorSession(string corePath, string romPath, string console = "")
        {
            _corePath = corePath;
            _romPath = romPath;
            _console = console;
            _handler = ConsoleHandlerFactory.Create(console);
            foreach (var kv in _handler.GetDefaultCoreOptions())   // pre-seed this console's curated options
                _coreOptions[kv.Key] = kv.Value;
            _input = new SdlInput
            {
                UsesAnalogStick = _handler.UsesAnalogStick,
                PromoteAnalogStickToDpad = _handler.PromoteAnalogStickToDpad,
            };

            _envCb = Environment_cb;
            _videoCb = Video_cb;
            _audioCb = Audio_cb;
            _audioBatchCb = AudioBatch_cb;
            _inputPollCb = InputPoll_cb;
            _inputStateCb = _input.GetInputState;
            _logCb = RetroLog_cb;
        }

        /// <summary>Loads the core+ROM and starts the emulation thread. Returns false on failure.</summary>
        public bool Start(out string? error)
        {
            error = null;
            try
            {
                _input.Initialize();
                _input.LoadConfiguration(_console, App.Configuration);   // honor the Controls-panel bindings
                // Free the previous session's deferred core handle before dlopen'ing a fresh one
                // (prevents the stale-globals 2nd-launch failure for mupen64/dolphin/ppsspp-class cores).
                LibretroCore.FreeStaleDll();
                _core = new LibretroCore(_corePath);
                // System (BIOS) and save dirs follow XDG/portable layout (AppPaths creates them);
                // core-assets default to the core's own folder.
                string coreDir = System.IO.Path.GetDirectoryName(_corePath) ?? "";
                string sysDir = _handler.ResolveSystemDirectory(AppPaths.GetFolder("System"), coreDir);
                string saveDir = AppPaths.GetFolder("Saves");
                _handler.PrepareSaveDirectory(saveDir);   // create any console-specific subdirs (e.g. dc/)
                // Battery save lives next to the ROM's name in the Saves dir (RetroArch's <rom>.srm scheme).
                _srmPath = System.IO.Path.Combine(saveDir, System.IO.Path.GetFileNameWithoutExtension(_romPath) + ".srm");
                _systemDirPtr = Marshal.StringToHGlobalAnsi(sysDir);
                _saveDirPtr = Marshal.StringToHGlobalAnsi(saveDir);
                _coreAssetsDirPtr = Marshal.StringToHGlobalAnsi(coreDir);
                _core.SetCallbacks(_envCb, _videoCb, _audioCb, _audioBatchCb, _inputPollCb, _inputStateCb);
                _core.Init();

                if (!_core.LoadGame(_romPath))
                {
                    error = _core.LastError ?? "retro_load_game failed (the core rejected the ROM).";
                    return false;
                }

                // Per-console controller-port setup (base sets ports 0–3 to JOYPAD; PS1 → DualShock on
                // 0–1; GameCube/Dreamcast 4 ports, which also kicks off VMU/maple attachment).
                _handler.ConfigureControllerPorts(_core);

                // FDS / multi-disc: if the core handed us a disk-control interface and booted with the
                // disk ejected (FDS BIOS "Set the Disk Card"), insert disk 0 so the game boots. Discs
                // that are already inserted (PS1/Saturn) are left alone.
                TryInsertFirstDisk();
                LoadSram();   // restore battery save before the first frame runs

                _fps = _core.AvInfo.timing.fps > 0 ? _core.AvInfo.timing.fps : 60.0;
                double hwFps = _handler.HardwareTargetFps;   // console-forced rate (e.g. Dreamcast 60); -1 = use core
                if (hwFps > 0) _fps = hwFps;

                // Only a deliberate per-console AR override (e.g. TG16 → 4:3) changes the display; 0
                // keeps the current pixel-ratio rendering for everything else (incl. rotated games).
                var geo = _core.AvInfo.geometry;
                DisplayAspectRatio = _handler.GetDisplayAspectRatio(geo.base_width, geo.base_height, geo.aspect_ratio);
                _sampleRate = _core.AvInfo.timing.sample_rate > 0 ? _core.AvInfo.timing.sample_rate : 44100;
                // DIAGNOSTIC ONLY (EMUTASTIC_NO_AUDIO=1): skip opening the sound device to test whether the
                // audio subsystem is what's dragging the present off a clean 60. Never a shipping setting.
                if (Environment.GetEnvironmentVariable("EMUTASTIC_NO_AUDIO") != "1")
                    _audio = new SdlAudio((int)Math.Round(_sampleRate));
                else
                    Trace.WriteLine("[Emu] EMUTASTIC_NO_AUDIO=1 — running WITHOUT sound (diagnostic)");

                _running = true;
                System.Threading.Interlocked.Increment(ref _activeCount);
                if (_runInline)
                {
                    // Run the loop (and the GL window/present) on the CALLING thread — the spike model.
                    // On Linux the screen-sync behaves far better for a window driven by the main thread
                    // than a background one. The host has nothing else for its main thread to do.
                    RunLoop();   // blocks until the game exits
                }
                else
                {
                    _thread = new Thread(RunLoop) { IsBackground = true, Name = "EmuLoop" };
                    _thread.Start();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void RunLoop()
        {
            double targetFrameMs = 1000.0 / _fps;
            // Software-core timing (upstream "Stopwatch-primary" model, see Emulation-Timing wiki):
            // a high-res frame timer paces production; audio thresholds are only guards. Pure
            // Thread.Sleep jitters → chunky 60fps, so we sleep most of the budget then SPIN the last ms.
            const double prefillMs = 150, lowWatermark = 80, backpressureMs = 300;

            // Pre-fill the audio buffer so it doesn't underrun at startup (underrun = crackle + a
            // catch-up stutter as the loop races to refill). Run frames un-paced until the cushion
            // fills, but BOUNDED (≤60 ≈ 1s) and only with a working audio device so a silent intro /
            // no-audio device can't fast-forward seconds of game on boot.
            for (int guard = 0; _running && _audio != null && _audio.IsOpen && _audio.QueuedMs < prefillMs && guard < 60; guard++)
                try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw (prefill): {ex}"); break; }

            // DECOUPLED mode: present runs on its own thread, emu thread is paced by audio. Branch out
            // entirely (it owns the GL window + its own loop + cleanup) and return.
            if (_present == "gl" && _presentThreadMode)
            {
                RunDecoupled(targetFrameMs, prefillMs);
                return;
            }

            // Bring up the GL window on THIS (emu) thread so its GL context + event pump live here.
            // Sized to the display aspect (cosmetic — GlPresenter aspect-fits any frame); 4:3 default.
            if (_present == "gl")
            {
                double ar = DisplayAspectRatio > 0 ? DisplayAspectRatio : 4.0 / 3.0;
                int winH = 720, winW = Math.Max(1, (int)Math.Round(winH * ar));
                _glFullscreen = Environment.GetEnvironmentVariable("EMUTASTIC_GL_FULLSCREEN") == "1";
                _gl = GlPresenter.TryCreate(winW, winH, _glFullscreen, out string? glErr);
                if (_gl == null)
                {
                    Trace.WriteLine($"[Emu] GL present unavailable ({glErr}); falling back to WriteableBitmap path");
                    PresenterResolved?.Invoke(false);
                }
                else
                {
                    _gl.KeyEvent += OnGlKey;
                    _gl.MouseMoved += OnGlMouseMoved;
                    _gl.MouseLeft += OnGlMouseLeft;
                    Trace.WriteLine("[Emu] GL present ACTIVE (SDL3 GL window, vsync swap = the clock)");
                    PresenterResolved?.Invoke(true);   // tells the Avalonia window to hide its WriteableBitmap
                }
            }

            var frameTimer = Stopwatch.StartNew();
            long drcLogTick = 0;
            while (_running)
            {
                // Reset is honored even while paused (so the pill's Reset isn't dead when paused).
                if (_resetRequested) { _resetRequested = false; try { _core!.Reset(); } catch (Exception ex) { Trace.WriteLine($"[Emu] reset threw: {ex}"); } }

                // Paused: stop advancing the core (frame stays frozen) but keep the thread responsive.
                if (_paused) { Thread.Sleep(16); frameTimer.Restart(); continue; }

                if (!_noInputPoll) _input.Poll();

                // Dynamic rate control: fine-tune the resampler ratio each frame to hold the audio queue
                // centered (RetroArch's model). This is the PRIMARY audio-sync mechanism now; the coarse
                // backpressure/low-watermark guards below are only far-extreme backstops. All four servo
                // the same REAL input-queue signal so they don't fight each other.
                // DRC is opt-in (regressed 2D smoothness; off by default). Also off in "audio" pacing
                // (the buffer drain is the clock, resampling would fight it).
                if (_gl != null && _glSimpleEnabled)
                {
                    // SPIKE MODEL (the only config ever measured smooth): exactly ONE retro_run per present;
                    // the blocking vsync swap is the sole clock. Sound STAYS ON — it's kept in sync purely
                    // by the gentle resample nudge (DRC), NOT by skipping/repeating game frames (which is a
                    // video action and was jittering the picture). One run per refresh = steady rhythm.
                    _audio?.ApplyDrc();
                    long runT0 = frameTimer.ElapsedTicks;
                    try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                    System.Threading.Interlocked.Add(ref _coreRunTicks, frameTimer.ElapsedTicks - runT0);
                    System.Threading.Interlocked.Increment(ref _coreRunCalls);
                }
                else
                {
                    if (_drc && _pacing != "audio") _audio?.ApplyDrc();

                    // Backpressure: if audio has run well ahead (core got ahead of real time), SKIP this
                    // frame's run so the buffer drains during the pacing wait — don't spin-then-run, which
                    // adds audio faster than it drains and burns CPU.
                    bool overBuffered = _audio != null && _audio.QueuedMs > backpressureMs;
                    if (!overBuffered)
                    {
                        long runT0 = frameTimer.ElapsedTicks;
                        try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                        System.Threading.Interlocked.Add(ref _coreRunTicks, frameTimer.ElapsedTicks - runT0);
                        System.Threading.Interlocked.Increment(ref _coreRunCalls);

                        // Low-watermark catch-up: buffer dipped below the cushion → run one extra frame so
                        // audio refills instead of underrunning (the latest video frame still wins). Counted
                        // in the stats so the fps readout stays honest.
                        if (_running && _audio != null && _audio.QueuedMs < lowWatermark)   // smooth time-based estimate
                        {
                            _input.Poll();
                            long t2 = frameTimer.ElapsedTicks;
                            try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                            System.Threading.Interlocked.Add(ref _coreRunTicks, frameTimer.ElapsedTicks - t2);
                            System.Threading.Interlocked.Increment(ref _coreRunCalls);
                        }
                    }
                }

                // Present + pace. When the overlay is up, its BLOCKING vsync present is the SINGLE clock
                // (RetroArch's model): one retro_run per refresh → phase-locked, killing the 60.0-vs-panel
                // beat that reads as "60fps but jittery". Strict FIFO → even refresh intervals. The
                // Stopwatch runs only with no overlay (WriteableBitmap), or if the present didn't actually
                // block (no real vsync) so we never free-run.
                bool presentPaced = false;
                if (_gl != null)
                {
                    // GL path: the blocking vsync swap IS the clock. One present per refresh → phase-locked,
                    // even intervals. No Stopwatch/audio/spin pacing runs (presentPaced short-circuits it).
                    if (_gl.CloseRequested) { _running = false; break; }
                    byte[]? buf; int pw, ph;
                    lock (_frameLock) { buf = _frame; pw = _frameW; ph = _frameH; }
                    if (buf != null)
                    {
                        // Bring-up diagnostic: log the first present's enter/return so a hang in the
                        // blocking swap is unmistakable, then a heartbeat every ~60 frames.
                        if (_glPresents == 0) Trace.WriteLine($"[Gl] present #1 ENTER ({pw}x{ph})");
                        _gl.Present(buf, pw, ph);   // blocks to vsync → paces the loop
                        if (_glPresents == 0) Trace.WriteLine("[Gl] present #1 RETURNED (swap is unblocking)");
                        if ((++_glPresents % 60) == 0) Trace.WriteLine($"[Gl] heartbeat: {_glPresents} presents");
                        // Hazard #2: gate on the SMOOTHED swap-block time (like the Vulkan path). If the
                        // swap actually blocks to vsync it paces us; if it returns fast (vsync off / sw
                        // raster) the EMA stays low → presentPaced=false → the Stopwatch limiter below
                        // re-engages so the loop never free-runs uncapped.
                        _glSwapMsEma = _glSwapMsEma <= 0 ? _gl.LastSwapMs : _glSwapMsEma + 0.05 * (_gl.LastSwapMs - _glSwapMsEma);
                        // Mesa-FIFO present is self-paced (FIFO backpressure caps the rate); trust it and
                        // skip the stopwatch. Otherwise gate on the swap-block time as before.
                        presentPaced = _gl.SelfPaced || _glSwapMsEma > targetFrameMs * 0.5;
                    }
                    else { Thread.Sleep(1); presentPaced = true; }   // no frame yet (boot) → don't busy-spin
                }
                else if (EnsureOverlay() && _overlay != null)
                {
                    uint gen = _ovGeomGen;
                    if (gen != _ovGeomApplied)
                    {
                        _ovGeomApplied = gen;
                        try { if (!_overlay.Update(_ovX, _ovY, _ovW, _ovH, _ovFullscreen, out _)) FailOverlay(); }
                        catch (Exception ex) { Trace.WriteLine($"[Emu] overlay update threw: {ex.Message}"); }
                    }
                    if (_overlay != null)
                    {
                        byte[]? buf; int pw, ph;
                        lock (_frameLock) { buf = _frame; pw = _frameW; ph = _frameH; }
                        if (buf != null)
                        {
                            long pt0 = frameTimer.ElapsedTicks;
                            // Present, then BLOCK until it's actually on screen (present_wait) → the next
                            // retro_run is locked to the real display cadence (CVDisplayLink model). Falls
                            // back to acquire-pacing if present_wait is unavailable.
                            try { _overlay.Present(buf, pw, ph); _overlay.WaitForLastPresent(); }
                            catch (Exception ex) { Trace.WriteLine($"[Emu] overlay present threw: {ex.Message}"); FailOverlay(); }
                            double pm = (frameTimer.ElapsedTicks - pt0) * 1000.0 / Stopwatch.Frequency;
                            _presentMsEma = _presentMsEma <= 0 ? pm : _presentMsEma + 0.05 * (pm - _presentMsEma);
                        }
                    }
                    // Smoothed gate (not per-frame) so a single fast present can't flip us into Stopwatch.
                    presentPaced = _vulkanOk && _presentMsEma > targetFrameMs * 0.5;
                }

                if (!presentPaced)
                {
                    if (_pacing == "audio" && _audio != null && _audio.IsOpen)
                    {
                        // SOUND CLOCK: retro_run just added ~1 frame of audio; wait for the device to drain
                        // back to the cushion. The device consumes at exactly sample_rate → this paces the
                        // loop to real time, steadily, at the core's true rate — no Stopwatch wobble. Capped
                        // at 4× the budget so a silent scene can't stall.
                        int guard = 0;
                        while (_running && _audio.QueuedMsReal > prefillMs
                               && frameTimer.Elapsed.TotalMilliseconds < targetFrameMs * 4 && guard++ < 8000)
                        {
                            if (_audio.QueuedMsReal - prefillMs > 4) Thread.Sleep(1); else Thread.SpinWait(60);
                        }
                    }
                    else if (_pacing == "spin")
                    {
                        // Pure busy-spin to the budget: no Thread.Sleep at all → lowest timer wobble (burns a core).
                        while (_running && frameTimer.Elapsed.TotalMilliseconds < targetFrameMs) Thread.SpinWait(40);
                    }
                    else
                    {
                        // STOPWATCH (default): sleep most of the budget, spin the last ~1ms for sub-ms accuracy.
                        double remaining = targetFrameMs - frameTimer.Elapsed.TotalMilliseconds;
                        if (remaining > 1.5) Thread.Sleep((int)(remaining - 1.0));
                        while (_running && frameTimer.Elapsed.TotalMilliseconds < targetFrameMs) Thread.SpinWait(10);
                    }
                }
                // Universal frame-cadence instrumentation: the full frame-to-frame period (work + pacing),
                // for ANY pacing method / present path — this is the actual smoothness signal.
                double frameMs = frameTimer.Elapsed.TotalMilliseconds;
                frameTimer.Restart();
                if (frameMs > _frameMaxMs) _frameMaxMs = frameMs;
                if (frameMs > targetFrameMs * 1.5) _frameHitches++;
                _frameMsEma = _frameMsEma <= 0 ? frameMs : _frameMsEma + 0.05 * (frameMs - _frameMsEma);

                // GL smoothness readout, directly comparable to the spike (mean/stddev/min/max + focus),
                // every ~300 frames (~5s). The KEY line to grep: tells us if the GL path is smooth and
                // whether the window is focused (an unfocused window is throttled → not the code's fault).
                if (_gl != null)
                {
                    if (_glStatGc2Base < 0) _glStatGc2Base = GC.CollectionCount(2);
                    double workMs = frameMs - _gl.LastSwapMs;   // CPU work outside the blocking swap
                    if (workMs > _glStatWorkMax) _glStatWorkMax = workMs;
                    _glStatSum += frameMs; _glStatSumSq += frameMs * frameMs; _glStatCount++;
                    if (frameMs < _glStatMin) _glStatMin = frameMs;
                    if (frameMs > _glStatMax) _glStatMax = frameMs;
                    if (_glStatCount >= 300)
                    {
                        double mean = _glStatSum / _glStatCount;
                        double variance = Math.Max(0, _glStatSumSq / _glStatCount - mean * mean);
                        // gen2gc>0 in a spiky window => GC pause is the stutter. workMax high (vs swap) =>
                        // the stall is in our CPU work, not the present.
                        int gc2now = GC.CollectionCount(2); int gen2gc = gc2now - _glStatGc2Base; _glStatGc2Base = gc2now;
                        string statLine = $"[GlStats] {_glStatCount}f mean={mean:F2}ms ({1000.0 / mean:F1}fps) stddev={Math.Sqrt(variance):F2}ms min={_glStatMin:F2} max={_glStatMax:F2} workMax={_glStatWorkMax:F1}ms gen2gc={gen2gc} focus={_gl.IsFocused} swapEma={_glSwapMsEma:F2}ms";
                        Trace.WriteLine(statLine);
                        // Bulletproof readout: also append straight to /tmp, independent of any Trace/log setup.
                        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emutastic-glstats.log"), statLine + "\n"); } catch { }
                        _glStatSum = _glStatSumSq = _glStatMax = _glStatWorkMax = 0; _glStatMin = double.MaxValue; _glStatCount = 0;
                    }
                }

                // Pacing/cadence instrumentation (~once / 10s).
                if (_audio != null && (++drcLogTick % 600) == 0)
                {
                    _audio.SampleDrc(out double qms, out double ratio, out long underruns);
                    double fps = _frameMsEma > 0 ? 1000.0 / _frameMsEma : 0;
                    Trace.WriteLine($"[Emu] pacing={_pacing} frame={_frameMsEma:F2}ms(~{fps:F1}fps) max={_frameMaxMs:F1}ms hitches={_frameHitches} vk={_vulkanOk} pwait={_overlay?.PresentWaitAvailable ?? false}  DRC q={qms:F0}ms ratio={ratio:F5}");
                    _frameMaxMs = 0; _frameHitches = 0;
                }

                // Periodic SRAM autosave (~every 10s). Cheap (skips unchanged) and the only thing that
                // survives a hung-core leak / crash / SIGKILL, since flush-on-exit may never run.
                if (!_paused && (++_srmAutoSaveTick % 600) == 0) SaveSram();
            }

            SaveSram();   // flush battery save on clean exit (emu thread, core still loaded)
            try { _overlay?.Dispose(); } catch { }   // overlay created + used on this thread → tear down here
            _overlay = null; _vulkanOk = false;
            if (_gl != null) { _gl.KeyEvent -= OnGlKey; _gl.MouseMoved -= OnGlMouseMoved; _gl.MouseLeft -= OnGlMouseLeft; try { _gl.Dispose(); } catch { } _gl = null; }
        }

        // RetroArch-style decoupled pacing (EMUTASTIC_GL_PRESENT_THREAD=1). Emu thread = core paced by
        // audio backpressure (real-time clock); present thread = GL window showing the latest frame at
        // vsync. A missed vblank repeats a frame instead of slowing the core, so emulation speed + audio
        // stay correct regardless of present hitches.
        private void RunDecoupled(double targetFrameMs, double cushionMs)
        {
            using var ready = new System.Threading.ManualResetEventSlim(false);
            var presentThread = new Thread(() => PresentThreadProc(ready)) { IsBackground = true, Name = "GlPresent" };
            presentThread.Start();
            ready.Wait();
            if (_gl == null) { Trace.WriteLine("[Emu] decoupled: GL present failed to start; stopping"); _running = false; }

            var frameTimer = Stopwatch.StartNew();
            long drcLogTick = 0;
            while (_running)
            {
                if (_resetRequested) { _resetRequested = false; try { _core!.Reset(); } catch (Exception ex) { Trace.WriteLine($"[Emu] reset threw: {ex}"); } }
                if (_paused) { Thread.Sleep(16); frameTimer.Restart(); continue; }
                if (!_noInputPoll) _input.Poll();
                _audio?.ApplyDrc();

                long runT0 = frameTimer.ElapsedTicks;
                try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                System.Threading.Interlocked.Add(ref _coreRunTicks, frameTimer.ElapsedTicks - runT0);
                System.Threading.Interlocked.Increment(ref _coreRunCalls);

                // AUDIO IS THE CLOCK (RetroArch audio_sync): block until the device drains the ~1 frame of
                // audio we just produced back to the cushion. The device consumes at sample_rate → this
                // paces the loop to real-time, steadily, independent of the present thread's vsync.
                if (_audio != null && _audio.IsOpen)
                {
                    int guard = 0;
                    while (_running && _audio.QueuedMsReal > cushionMs && guard++ < 8000)
                    {
                        if (_audio.QueuedMsReal - cushionMs > 4) Thread.Sleep(1); else Thread.SpinWait(60);
                    }
                }
                else
                {
                    while (_running && frameTimer.Elapsed.TotalMilliseconds < targetFrameMs) Thread.SpinWait(40);
                }
                double frameMs = frameTimer.Elapsed.TotalMilliseconds; frameTimer.Restart();
                _frameMsEma = _frameMsEma <= 0 ? frameMs : _frameMsEma + 0.05 * (frameMs - _frameMsEma);

                var glRef = _gl;
                if (glRef != null && glRef.CloseRequested) { _running = false; break; }

                if (_audio != null && (++drcLogTick % 600) == 0)
                {
                    _audio.SampleDrc(out double qms, out double ratio, out long underruns);
                    double fps = _frameMsEma > 0 ? 1000.0 / _frameMsEma : 0;
                    Trace.WriteLine($"[Emu] DECOUPLED emu={_frameMsEma:F2}ms(~{fps:F1}fps) DRC q={qms:F0}ms ratio={ratio:F5} underruns={underruns}");
                }
                if (!_paused && (++_srmAutoSaveTick % 600) == 0) SaveSram();
            }

            _running = false;
            try { presentThread.Join(1500); } catch { }
            SaveSram();   // flush battery save on clean exit (emu thread, core still loaded)
        }

        // Present thread: owns the GL window + context + event pump. Shows the latest produced frame at
        // vsync; never runs the core. GlStats logged here = the TRUE display cadence.
        private void PresentThreadProc(System.Threading.ManualResetEventSlim ready)
        {
            double ar = DisplayAspectRatio > 0 ? DisplayAspectRatio : 4.0 / 3.0;
            int winH = 720, winW = Math.Max(1, (int)Math.Round(winH * ar));
            _glFullscreen = Environment.GetEnvironmentVariable("EMUTASTIC_GL_FULLSCREEN") == "1";
            _gl = GlPresenter.TryCreate(winW, winH, _glFullscreen, out string? glErr);
            if (_gl == null)
            {
                Trace.WriteLine($"[Emu] decoupled GL present unavailable ({glErr})");
                PresenterResolved?.Invoke(false);
                ready.Set();
                return;
            }
            _gl.KeyEvent += OnGlKey; _gl.MouseMoved += OnGlMouseMoved; _gl.MouseLeft += OnGlMouseLeft;
            Trace.WriteLine("[Emu] GL present ACTIVE (DECOUPLED: present thread + audio-clock emu thread)");
            PresenterResolved?.Invoke(true);
            ready.Set();

            var pt = Stopwatch.StartNew();
            while (_running && !_gl.CloseRequested)
            {
                // Copy the latest frame UNDER the lock into a present-owned buffer (the emu thread
                // ping-pongs its two buffers, so the front buffer must not be read off-lock).
                byte[]? toPresent = null; int pw = 0, ph = 0;
                lock (_frameLock)
                {
                    if (_frame != null)
                    {
                        pw = _frameW; ph = _frameH; int need = pw * ph * 4;
                        if (_presentBuf == null || _presentBuf.Length != need) _presentBuf = new byte[need];
                        System.Buffer.BlockCopy(_frame, 0, _presentBuf, 0, need);
                        toPresent = _presentBuf;
                    }
                }
                if (toPresent != null) _gl.Present(toPresent, pw, ph);   // pumps events + vsync swap
                else { _gl.PumpEvents(); Thread.Sleep(2); }

                double frameMs = pt.Elapsed.TotalMilliseconds; pt.Restart();
                _glSwapMsEma = _glSwapMsEma <= 0 ? _gl.LastSwapMs : _glSwapMsEma + 0.05 * (_gl.LastSwapMs - _glSwapMsEma);
                if (_glStatGc2Base < 0) _glStatGc2Base = GC.CollectionCount(2);
                double workMs = frameMs - _gl.LastSwapMs; if (workMs > _glStatWorkMax) _glStatWorkMax = workMs;
                _glStatSum += frameMs; _glStatSumSq += frameMs * frameMs; _glStatCount++;
                if (frameMs < _glStatMin) _glStatMin = frameMs;
                if (frameMs > _glStatMax) _glStatMax = frameMs;
                if (_glStatCount >= 300)
                {
                    double mean = _glStatSum / _glStatCount;
                    double variance = Math.Max(0, _glStatSumSq / _glStatCount - mean * mean);
                    int gc2now = GC.CollectionCount(2); int gen2gc = gc2now - _glStatGc2Base; _glStatGc2Base = gc2now;
                    Trace.WriteLine($"[GlStats] DECOUPLED {_glStatCount}f mean={mean:F2}ms ({1000.0 / mean:F1}fps) stddev={Math.Sqrt(variance):F2}ms min={_glStatMin:F2} max={_glStatMax:F2} workMax={_glStatWorkMax:F1}ms gen2gc={gen2gc} focus={_gl.IsFocused} swapEma={_glSwapMsEma:F2}ms");
                    _glStatSum = _glStatSumSq = _glStatMax = _glStatWorkMax = 0; _glStatMin = double.MaxValue; _glStatCount = 0;
                }
            }

            _running = false;   // window closed → stop the emu thread
            var gl = _gl; _gl = null;
            if (gl != null)
            {
                gl.KeyEvent -= OnGlKey; gl.MouseMoved -= OnGlMouseMoved; gl.MouseLeft -= OnGlMouseLeft;
                try { gl.Dispose(); } catch { }
            }
        }

        // Build the Vulkan overlay window on the EMU thread once the UI gives us a target rect (opt-in
        // EMUTASTIC_VULKAN=1). One-shot. Success → the RunLoop couples emulation to its vsync present
        // (one retro_run per refresh → phase-locked, no beat). Failure → WriteableBitmap path.
        private bool EnsureOverlay()
        {
            if (_overlay != null) return true;
            if (_overlayTried) return false;
            if (!_ovHasTarget) return false;            // wait for first geometry (don't set _overlayTried yet)
            _overlayTried = true;
            if (Environment.GetEnvironmentVariable("EMUTASTIC_VULKAN") != "1")
            {
                Trace.WriteLine("[Emu] Vulkan present opt-in (set EMUTASTIC_VULKAN=1); using WriteableBitmap path");
                PresenterResolved?.Invoke(false);
                return false;
            }
            var ov = new VkOverlay();
            if (!ov.Create(_ovX, _ovY, _ovW, _ovH, _ovFullscreen))
            {
                Trace.WriteLine($"[Emu] Vulkan overlay unavailable ({ov.LastError}); WriteableBitmap fallback");
                ov.Dispose();
                PresenterResolved?.Invoke(false);
                return false;
            }
            _overlay = ov; _ovGeomApplied = _ovGeomGen; _vulkanOk = true;
            Trace.WriteLine($"[Emu] Vulkan present ACTIVE (overlay; present_wait={ov.PresentWaitAvailable})");
            PresenterResolved?.Invoke(true);
            return true;
        }

        private void FailOverlay()
        {
            _vulkanOk = false;
            try { _overlay?.Dispose(); } catch { /* best-effort */ }
            _overlay = null;
            PresenterResolved?.Invoke(false);
        }

        // ---- libretro callbacks ----
        private bool Environment_cb(uint cmd, IntPtr data)
        {
            // Cores OR RETRO_ENVIRONMENT_EXPERIMENTAL/PRIVATE into the command id — strip before switching.
            uint baseCmd = cmd & ~(RETRO_ENVIRONMENT_EXPERIMENTAL | RETRO_ENVIRONMENT_PRIVATE);
            switch (baseCmd)
            {
                case ENV_GET_CAN_DUPE:
                    if (data != IntPtr.Zero) Marshal.WriteByte(data, 1);
                    return true;
                case ENV_SET_PIXEL_FORMAT:
                    if (data != IntPtr.Zero) _pixelFormat = Marshal.ReadInt32(data);
                    return true;
                case ENV_SET_ROTATION:
                    // value 0..3 → 0/90/180/270° counter-clockwise (vertical arcade games etc.)
                    if (data != IntPtr.Zero) _rotationDeg = (Marshal.ReadInt32(data) & 3) * 90;
                    return true;
                case ENV_GET_SYSTEM_DIRECTORY:
                    if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _systemDirPtr);
                    return true;
                case ENV_GET_SAVE_DIRECTORY:
                    if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _saveDirPtr);
                    return true;
                case ENV_GET_CORE_ASSETS_DIRECTORY:
                    if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _coreAssetsDirPtr);
                    return true;
                case ENV_GET_LOG_INTERFACE:
                    // retro_log_callback is a single function-pointer field; hand the core our logger.
                    if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, Marshal.GetFunctionPointerForDelegate(_logCb));
                    return true;
                case ENV_SET_PERFORMANCE_LEVEL:
                    return true;
                case ENV_GET_VARIABLE_UPDATE:
                    if (data != IntPtr.Zero) Marshal.WriteByte(data, (byte)(_coreOptionsDirty ? 1 : 0));
                    return true;
                case ENV_GET_CORE_OPTIONS_VERSION:
                    // Report v0 so cores use the simple SET_VARIABLES path (v2-capable cores downgrade
                    // cleanly); v2's display/category metadata isn't needed to apply the options.
                    if (data != IntPtr.Zero) Marshal.WriteInt32(data, 0);
                    return true;
                case ENV_SET_VARIABLES:
                    ParseSetVariables(data);
                    return true;
                case ENV_SET_DISK_CONTROL_INTERFACE:
                case ENV_SET_DISK_CONTROL_EXT_INTERFACE:
                    // Capture the core's disk callbacks so we can insert disk 0 after load (FDS).
                    if (data != IntPtr.Zero)
                    {
                        var dc = Marshal.PtrToStructure<retro_disk_control_callback>(data);
                        if (dc.set_eject_state != IntPtr.Zero) _setEjectState = Marshal.GetDelegateForFunctionPointer<SetEjectStateFn>(dc.set_eject_state);
                        if (dc.set_image_index != IntPtr.Zero) _setImageIndex = Marshal.GetDelegateForFunctionPointer<SetImageIndexFn>(dc.set_image_index);
                        if (dc.get_eject_state != IntPtr.Zero) _getEjectState = Marshal.GetDelegateForFunctionPointer<GetEjectStateFn>(dc.get_eject_state);
                    }
                    return true;
                case ENV_GET_VARIABLE:
                    return HandleGetVariable(data);
                case ENV_GET_OVERSCAN:
                default:
                    return false; // unsupported / use core defaults — cores cope (incl. SET_HW_RENDER → SW)
            }
        }

        // Parse a libretro SET_VARIABLES announcement: a NULL-terminated array of retro_variable
        // {key, "human description; opt1|opt2|…"}. We let the console handler filter/inject values,
        // then default any unseeded key to the core's first valid option.
        private void ParseSetVariables(IntPtr data)
        {
            if (data == IntPtr.Zero) return;
            try
            {
                int stride = Marshal.SizeOf<retro_variable>();
                IntPtr p = data;
                for (int n = 0; n < 4096; n++, p = IntPtr.Add(p, stride))   // cap: a malformed/non-terminated array can't run off into unmapped memory
                {
                    var v = Marshal.PtrToStructure<retro_variable>(p);
                    if (v.key == IntPtr.Zero) break;   // {NULL, NULL} terminator

                    string? key = Marshal.PtrToStringAnsi(v.key);
                    string? desc = Marshal.PtrToStringAnsi(v.value);
                    if (string.IsNullOrEmpty(key) || desc == null) continue;

                    int semi = desc.IndexOf(';');
                    string opts = semi >= 0 ? desc[(semi + 1)..] : desc;
                    string[] vals = opts.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    vals = _handler.FilterCoreOptionValues(key!, vals) ?? vals;
                    _handler.OnVariableAnnounced(key!, vals, _coreOptions);

                    if (vals.Length == 0) continue;
                    // Default an unseeded key to the first valid value; also repair a seeded value the
                    // core wouldn't accept (not in its valid set) so we never feed it a bad option.
                    if (!_coreOptions.TryGetValue(key!, out var cur) || Array.IndexOf(vals, cur) < 0)
                        _coreOptions[key!] = vals[0];
                }
                _coreOptionsDirty = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Emu] SET_VARIABLES parse failed: {ex.Message}");
            }
        }

        // GET_VARIABLE: the core passes a retro_variable with key set; we write back a persistent
        // char* for the resolved value (or leave it NULL + return false so the core uses its default).
        private bool HandleGetVariable(IntPtr data)
        {
            if (data == IntPtr.Zero) return false;
            try
            {
                var v = Marshal.PtrToStructure<retro_variable>(data);
                string? key = Marshal.PtrToStringAnsi(v.key);
                if (key == null || !_coreOptions.TryGetValue(key, out var val)) return false;

                if (!_coreOptionPtrs.TryGetValue(key, out var ptr) || Marshal.PtrToStringAnsi(ptr) != val)
                {
                    // Fresh ptr; never free the old one here (the core may still reference it) — all are
                    // freed in Dispose.
                    ptr = Marshal.StringToHGlobalAnsi(val);
                    _allocatedOptionPtrs.Add(ptr);
                    _coreOptionPtrs[key] = ptr;
                }
                Marshal.WriteIntPtr(data, IntPtr.Size, ptr);   // retro_variable.value (second field)
                _coreOptionsDirty = false;
                return true;
            }
            catch { return false; }
        }

        // Insert disk 0 if the core booted with the disk ejected (FDS). Runs on the emu thread,
        // right after retro_load_game, before the run loop — so the disk is present from frame 0 and
        // the FDS BIOS reads it instead of waiting on "Set the Disk Card".
        private void TryInsertFirstDisk()
        {
            try
            {
                if (_setEjectState == null) return;
                bool ejected = _getEjectState?.Invoke() ?? true;   // assume ejected if the core doesn't say
                if (!ejected) return;                              // already inserted (PS1/Saturn) — leave it
                _setImageIndex?.Invoke(0);                         // select disk 0 (allowed while ejected)
                _setEjectState(false);                             // insert
                System.Diagnostics.Trace.WriteLine("[Emu] disk-control: inserted disk 0 (was ejected at boot)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Emu] disk-control insert failed: {ex.Message}");
            }
        }

        // Restore the battery save into the core's SRAM region, if a .srm exists and the core has SRAM.
        private void LoadSram()
        {
            try
            {
                if (_srmPath == null || !System.IO.File.Exists(_srmPath)) return;
                var data = System.IO.File.ReadAllBytes(_srmPath);
                if (data.Length > 0 && (_core?.LoadSaveRam(data) ?? false))
                {
                    _lastSrm = data;
                    Trace.WriteLine($"[Emu] SRAM loaded ({data.Length} bytes) from {_srmPath}");
                }
            }
            catch (Exception ex) { Trace.WriteLine($"[Emu] SRAM load failed: {ex.Message}"); }
        }

        // Persist the core's SRAM to disk if it changed since the last write. Atomic (temp + replace) so a
        // crash mid-write can't corrupt the save. Called periodically from the loop and on exit. Runs on
        // the emu thread only (reads the live core memory). Returns quietly if the core exposes no SRAM.
        private void SaveSram()
        {
            try
            {
                if (_srmPath == null) return;
                byte[]? data = _core?.GetSaveRam();
                if (data == null || data.Length == 0) return;
                if (_lastSrm != null && _lastSrm.Length == data.Length && data.AsSpan().SequenceEqual(_lastSrm)) return; // unchanged
                string tmp = _srmPath + ".tmp";
                System.IO.File.WriteAllBytes(tmp, data);
                System.IO.File.Move(tmp, _srmPath, overwrite: true);
                _lastSrm = data;
                Trace.WriteLine($"[Emu] SRAM saved ({data.Length} bytes)");
            }
            catch (Exception ex) { Trace.WriteLine($"[Emu] SRAM save failed: {ex.Message}"); }
        }

        private void RetroLog_cb(uint level, IntPtr fmt, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3)
        {
            // Best-effort: print the core's format string (full printf expansion isn't needed for
            // bring-up diagnostics). Helps when validating new cores during the port.
            try
            {
                string? s = Marshal.PtrToStringAnsi(fmt);
                if (!string.IsNullOrEmpty(s)) System.Diagnostics.Trace.Write($"[core:{level}] {s}");
            }
            catch { /* never let a log call throw back into native code */ }
        }

        private unsafe void Video_cb(IntPtr data, uint width, uint height, UIntPtr pitch)
        {
            if (data == IntPtr.Zero || width == 0 || height == 0) return; // duplicate frame
            int w = (int)width, h = (int)height, pitchB = (int)pitch;
            int need = w * h * 4;
            if (_convBuf == null || _convBuf.Length != need) _convBuf = new byte[need];  // reused; realloc only on size change
            var bgra = _convBuf;
            byte* src = (byte*)data;
            fixed (byte* dst0 = bgra)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* dst = dst0 + y * w * 4;
                    if (_pixelFormat == 1) // XRGB8888: little-endian bytes already B,G,R,X
                    {
                        byte* row = src + y * pitchB;
                        for (int x = 0; x < w; x++)
                        {
                            dst[x * 4 + 0] = row[x * 4 + 0];
                            dst[x * 4 + 1] = row[x * 4 + 1];
                            dst[x * 4 + 2] = row[x * 4 + 2];
                            dst[x * 4 + 3] = 255;
                        }
                    }
                    else // 16-bit formats
                    {
                        ushort* row = (ushort*)(src + y * pitchB);
                        for (int x = 0; x < w; x++)
                        {
                            ushort v = row[x];
                            byte r, g, b;
                            if (_pixelFormat == 2) // RGB565
                            {
                                r = (byte)(((v >> 11) & 0x1F) * 255 / 31);
                                g = (byte)(((v >> 5) & 0x3F) * 255 / 63);
                                b = (byte)((v & 0x1F) * 255 / 31);
                            }
                            else // 0RGB1555
                            {
                                r = (byte)(((v >> 10) & 0x1F) * 255 / 31);
                                g = (byte)(((v >> 5) & 0x1F) * 255 / 31);
                                b = (byte)((v & 0x1F) * 255 / 31);
                            }
                            dst[x * 4 + 0] = b; dst[x * 4 + 1] = g; dst[x * 4 + 2] = r; dst[x * 4 + 3] = 255;
                        }
                    }
                }
            }
            // Honor a core-requested rotation by rotating the BGRA buffer (and swapping dims for
            // 90/270) so the displayed Image is upright with the correct aspect — no UI transform.
            // Rotated games (90/270) get a fresh rotated buffer (rare path; not reused). For the common
            // un-rotated case bgra IS _convBuf, so the swap below recycles the previous front buffer.
            if (_rotationDeg != 0) bgra = RotateBgra(bgra, ref w, ref h, _rotationDeg);

            lock (_frameLock)
            {
                var prev = _frame;
                _frame = bgra; _frameW = w; _frameH = h; _frameSeq++;
                // Recycle the previous front buffer as the next working buffer (un-rotated path only,
                // and only if it's the right size) so we ping-pong two buffers with zero allocation.
                if (_rotationDeg == 0 && prev != null && prev.Length == need) _convBuf = prev;
            }
            System.Threading.Interlocked.Increment(ref _frameCountSample);   // real produced-frame rate
            FrameReady?.Invoke();                                            // push the frame to the window to present
        }

        // Rotate a tightly-packed BGRA buffer counter-clockwise by deg (90/180/270). Returns the
        // new buffer; w/h are updated (swapped for 90/270).
        private static byte[] RotateBgra(byte[] src, ref int w, ref int h, int deg)
        {
            int sw = w, sh = h;
            var dst = new byte[src.Length];
            if (deg == 180)
            {
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                    {
                        int s = (y * sw + x) * 4, d = ((sh - 1 - y) * sw + (sw - 1 - x)) * 4;
                        dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
                    }
                return dst;
            }
            int dw = sh, dh = sw;   // 90/270 swap dimensions
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int s = (y * sw + x) * 4;
                    int dx, dy;
                    if (deg == 90) { dx = y; dy = sw - 1 - x; }       // 90° CCW
                    else           { dx = sh - 1 - y; dy = x; }       // 270° CCW (= 90° CW)
                    int d = (dy * dw + dx) * 4;
                    dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
                }
            w = dw; h = dh;
            return dst;
        }

        /// <summary>
        /// Hands the UI the latest frame if it's newer than <paramref name="lastSeq"/>.
        /// Returns false when no new frame is available. Copies into the reusable _uiBuf UNDER the lock
        /// (the emu thread reuses/ping-pongs its buffers, so the front buffer must not be read off-lock);
        /// the returned buffer is UI-thread-owned, so PumpFrame can blit it without holding the lock.
        /// </summary>
        public bool TrySnapshot(ref long lastSeq, out byte[]? buf, out int w, out int h)
        {
            lock (_frameLock)
            {
                if (_frame == null || _frameSeq == lastSeq) { buf = null; w = h = 0; return false; }
                lastSeq = _frameSeq; w = _frameW; h = _frameH;
                int need = w * h * 4;
                if (_uiBuf == null || _uiBuf.Length != need) _uiBuf = new byte[need];
                System.Buffer.BlockCopy(_frame, 0, _uiBuf, 0, need);
                buf = _uiBuf; return true;
            }
        }

        private void Audio_cb(short left, short right) => _audio?.QueueSample(left, right);
        private UIntPtr AudioBatch_cb(IntPtr data, UIntPtr frames) { _audio?.QueueBatch(data, (int)frames); return frames; }
        private void InputPoll_cb() { /* SdlInput.Poll already called at top of the loop */ }

        // Number of live emulator sessions. The Controls-panel ControllerManager checks this so it
        // doesn't call SDL_PumpEvents concurrently with the emu loop (SDL pumping isn't multi-thread safe).
        private static int _activeCount;
        public static bool AnyActive => System.Threading.Volatile.Read(ref _activeCount) > 0;

        public void Dispose()
        {
            if (_running) System.Threading.Interlocked.Decrement(ref _activeCount);
            _running = false;
            // The emu thread must fully exit retro_run before we free the core / SDL handles it
            // calls into (video/audio/input callbacks). For software cores retro_run returns in
            // ~one frame, so this joins immediately. If a core hangs and the thread does NOT join,
            // we deliberately LEAK the native resources rather than free them out from under a
            // still-running native callback (which would be an uncatchable use-after-free crash).
            bool joined = _thread == null || _thread.Join(5000);
            if (!joined)
            {
                System.Diagnostics.Trace.WriteLine(
                    "[Emu] emulation thread did not exit; leaking core/SDL/Vulkan handles to avoid use-after-free.");
                return;
            }

            try { _overlay?.Dispose(); } catch { }   // present thread joined → safe to tear down Vulkan + X Display
            _overlay = null;
            _vulkanOk = false;
            _audio?.Dispose();
            _input.Dispose();
            _core?.Dispose();
            if (_systemDirPtr != IntPtr.Zero) Marshal.FreeHGlobal(_systemDirPtr);
            if (_saveDirPtr != IntPtr.Zero) Marshal.FreeHGlobal(_saveDirPtr);
            if (_coreAssetsDirPtr != IntPtr.Zero) Marshal.FreeHGlobal(_coreAssetsDirPtr);
            _systemDirPtr = _saveDirPtr = _coreAssetsDirPtr = IntPtr.Zero;
            foreach (var ptr in _allocatedOptionPtrs) if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            _allocatedOptionPtrs.Clear();
            _coreOptionPtrs.Clear();
        }
    }
}
