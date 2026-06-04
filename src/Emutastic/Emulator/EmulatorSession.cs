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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetImageIndexFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetNumImagesFn();
        private SetEjectStateFn? _setEjectState;
        private SetImageIndexFn? _setImageIndex;
        private GetEjectStateFn? _getEjectState;
        private GetImageIndexFn? _getImageIndex;
        private GetNumImagesFn? _getNumImages;
        private bool _diskControlAvailable;     // core registered a disk-control interface (multi-disc / FDS)
        private int _fdsSideChangeFrames;        // FDS: inject JOYPAD_L for N polled frames = "disk side change"
        private int _diskInsertPendingFrames;    // deferred set_eject_state(false) countdown after a swap
        private bool _diskSwapPrevHeld;          // rising-edge latch for the swap chord
        private volatile string _diskMsg = "";   // transient "Disk N/M" OSD message (read by the present loop)
        private long _diskMsgUntil;              // Stopwatch ticks; message shown while now < this

        [StructLayout(LayoutKind.Sequential)]
        private struct retro_disk_control_callback   // first 7 fields are shared with the EXT version
        {
            public IntPtr set_eject_state, get_eject_state, get_image_index,
                          set_image_index, get_num_images, replace_image_index, add_image_index;
        }

        // ── GL hardware render for 3D cores (Phase 1). SET_HW_RENDER hands us a retro_hw_render_callback;
        //    we render the core into libwlpresent's offscreen FBO and read it back to the normal frame. ──
        const uint ENV_SET_HW_RENDER = 14;
        static readonly IntPtr RETRO_HW_FRAME_BUFFER_VALID = (IntPtr)(-1);   // Video_cb data sentinel for HW frames
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HwContextResetFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr HwGetFramebufferFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr HwGetProcAddressFn([MarshalAs(UnmanagedType.LPStr)] string sym);
        private bool _hwRenderActive, _hwBottomLeft, _hwDepth, _hwStencil;
        private int _hwCtxType, _hwMajor, _hwMinor;
        private HwContextResetFn? _hwContextReset, _hwContextDestroy;
        private HwGetFramebufferFn? _hwGetFb;        // kept alive — pointer handed to the core
        private HwGetProcAddressFn? _hwGetProc;      // kept alive — pointer handed to the core
        private byte[]? _hwBufA, _hwBufB;            // true double-buffer for HW readback (never write the front)
        private double _hwReadbackMs;                // smoothed glReadPixels readback cost (diagnostic)

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
        // PROVEN windowed-60 fix: present through our OWN Wayland xdg_toplevel (RetroArch's model) via the
        // libwlpresent shim, instead of SDL's window (which caps at ~55 windowed). EMUTASTIC_GL_TOPLEVEL=1
        // routes the decoupled present thread through WlToplevelPresenter; SDL stays for gamepad + audio.
        private readonly bool _toplevelMode = Environment.GetEnvironmentVariable("EMUTASTIC_GL_TOPLEVEL") == "1";
        private WlToplevelPresenter? _wlTop;
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
        public void RequestQuit() { _quitRequested = true; _running = false; }
        private volatile bool _quitRequested;

        /// <summary>True once a clean quit has been requested (window closed / signal / pre-warm budget hit).</summary>
        public bool QuitRequested => _quitRequested;

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
        // Schema captured from SET_VARIABLES, persisted after a successful load so the Preferences
        // "Core Options" tab lists this core (upstream: every core exposes options after first run).
        private readonly List<Models.CoreOptionEntry> _coreOptionSchema = new();
        private readonly Services.CoreOptionsService _coreOptionsStore = new();
        private readonly string _coreName;   // file stem, e.g. "parallel_n64_libretro" — the schema/values key

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
            // User choices from the Preferences "Core Options" tab override the handler's curated
            // defaults (upstream priority order). Values the core won't accept are repaired against
            // its valid list in ParseSetVariables.
            _coreName = System.IO.Path.GetFileNameWithoutExtension(corePath);
            foreach (var kv in _coreOptionsStore.LoadValues(_coreName))
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
            _inputStateCb = InputState_cb;
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

                // Persist the option schema announced via SET_VARIABLES so the Preferences
                // "Core Options" tab lists this core from now on (upstream saves at the same point —
                // after retro_load_game succeeds, so a core that rejects the ROM never registers).
                if (_coreOptionSchema.Count > 0)
                {
                    _coreOptionsStore.SaveSchema(_coreName, new Services.CoreOptionsSchema
                    {
                        DisplayName = Services.CoreOptionsService.DisplayNameFor(_coreName),
                        ConsoleName = _console,
                        Options = new List<Models.CoreOptionEntry>(_coreOptionSchema),
                    });
                    Trace.WriteLine($"[Emu] Core options schema saved: {_coreName} ({_coreOptionSchema.Count} options)");
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
                // 3D cores: now that av_info (max geometry) is known, create the GL HW context + FBO and fire
                // context_reset, on this (emu) thread so the context is current for every retro_run.
                InitHwRenderContext();
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
                ServiceDiskSwap();   // disc-swap chord (L3+Start) + FDS/deferred-insert ticks

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
            if (_gl == null && _wlTop == null) { Trace.WriteLine("[Emu] decoupled: GL present failed to start; stopping"); _running = false; }

            var frameTimer = Stopwatch.StartNew();
            long drcLogTick = 0;
            while (_running)
            {
                if (_resetRequested) { _resetRequested = false; try { _core!.Reset(); } catch (Exception ex) { Trace.WriteLine($"[Emu] reset threw: {ex}"); } }
                if (_paused) { Thread.Sleep(16); frameTimer.Restart(); continue; }
                if (!_noInputPoll) _input.Poll();
                ServiceDiskSwap();   // disc-swap chord (L3+Start) + FDS/deferred-insert ticks
                _audio?.ApplyDrc();

                long runT0 = frameTimer.ElapsedTicks;
                try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                System.Threading.Interlocked.Add(ref _coreRunTicks, frameTimer.ElapsedTicks - runT0);
                System.Threading.Interlocked.Increment(ref _coreRunCalls);

                // PACE TO THE TARGET RATE (Phase 0.2): targetFrameMs comes from _fps, which is the console
                // handler's HardwareTargetFps or the core's reported fps. Pace the emu to that fixed content
                // rate — RetroArch's model (slave the loop to a rate, DRC resamples audio to match) — rather
                // than free-running on audio drain, which drifted to ~61fps and beat against the ~60Hz
                // display. Production-timing jitter here is harmless: the present thread is vsync-paced
                // SEPARATELY and shows the latest frame, so a loose emu tick can't judder the display.
                int guard = 0;
                while (_running && frameTimer.Elapsed.TotalMilliseconds < targetFrameMs && guard++ < 8000)
                {
                    double remaining = targetFrameMs - frameTimer.Elapsed.TotalMilliseconds;
                    if (remaining > 1.5) Thread.Sleep(1); else Thread.SpinWait(40);
                }
                // Audio cushion cap (secondary): if the core ran ahead and the buffer overfilled well past
                // the cushion, drain before the next frame so audio can't run away. DRC handles the ±0.5%
                // trim. Use the SMOOTHED occupancy (not raw QueuedMsReal) so a single device gulp can't fire
                // a spurious drain-stall — matches SdlAudio's "coarse guards use the smoothed value" rule.
                if (_audio != null && _audio.IsOpen)
                {
                    guard = 0;
                    while (_running && _audio.QueuedMsSmoothed > cushionMs + 40 && guard++ < 4000) Thread.Sleep(1);
                }
                double frameMs = frameTimer.Elapsed.TotalMilliseconds; frameTimer.Restart();
                _frameMsEma = _frameMsEma <= 0 ? frameMs : _frameMsEma + 0.05 * (frameMs - _frameMsEma);

                var glRef = _gl;
                if (glRef != null && glRef.CloseRequested) { _running = false; break; }
                if (_wlTop != null && _wlTop.CloseRequested) { _running = false; break; }

                if (_audio != null && (++drcLogTick % 600) == 0)
                {
                    _audio.SampleDrc(out double qms, out double ratio, out long underruns);
                    double fps = _frameMsEma > 0 ? 1000.0 / _frameMsEma : 0;
                    string hwRb = "";
                    if (_hwRenderActive)
                    {
                        // issue = glReadPixels enqueue (big ⇒ driver syncing on the FBO), map = PBO map
                        // wait + copy (big ⇒ DMA not done / slow PCIe copy). Both 0 ⇒ sync fallback path.
                        var (issueMs, mapMs, mapcallMs, copyMs) = Platform.HwGlContext.ReadbackTimes();
                        hwRb = $" hwReadback={_hwReadbackMs:F2}ms(issue={issueMs:F2} map={mapMs:F2}=sync{mapcallMs:F2}+copy{copyMs:F2})";
                    }
                    Trace.WriteLine($"[Emu] DECOUPLED emu={_frameMsEma:F2}ms(~{fps:F1}fps) DRC q={qms:F0}ms ratio={ratio:F5} underruns={underruns}{hwRb}");
                }
                if (!_paused && (++_srmAutoSaveTick % 600) == 0) SaveSram();
            }

            _running = false;
            try { presentThread.Join(1500); } catch { }
            SaveSram();   // flush battery save on clean exit (emu thread, core still loaded)
            // Tear down the HW-render context on THIS (emu) thread, where it's current. We deliberately do
            // NOT call the core's context_destroy (mupen/PPSSPP run async cleanup that crashes if we do —
            // per the per-core quirks); just drop our EGL context + FBO.
            if (_hwRenderActive) { try { Platform.HwGlContext.Destroy(); } catch { } _hwRenderActive = false; }
        }

        // Present thread: owns the GL window + context + event pump. Shows the latest produced frame at
        // vsync; never runs the core. GlStats logged here = the TRUE display cadence.
        private void PresentThreadProc(System.Threading.ManualResetEventSlim ready)
        {
            if (_toplevelMode) { PresentToplevelProc(ready); return; }
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

            long lastSeq = -1;
            bool swapOnly = Environment.GetEnvironmentVariable("EMUTASTIC_GL_SWAPONLY") == "1";
            // Attribution found the ~55fps was the _frameSeq gate + Sleep(1) chase (phase), NOT draw work.
            // NOGATE presents the latest frame EVERY iteration and lets the blocking FIFO swap pace to vblank
            // (re-presenting a duplicate on a slow frame is correct on Wayland) → should hit a clean 60.
            bool noGate = Environment.GetEnvironmentVariable("EMUTASTIC_GL_NOGATE") == "1";
            var pt = Stopwatch.StartNew();
            while (_running && !_gl.CloseRequested)
            {
                if (swapOnly)
                {
                    _gl.SwapOnly();   // DIAGNOSTIC: swap with no upload/draw — isolates draw-work vs FIFO
                }
                else if (noGate)
                {
                    // Present the latest frame every iteration; FIFO swap is the pace. No seq-gate, no sleep.
                    byte[]? buf = null; int pw = 0, ph = 0;
                    lock (_frameLock)
                    {
                        if (_frame != null)
                        {
                            pw = _frameW; ph = _frameH; int need = pw * ph * 4;
                            if (_presentBuf == null || _presentBuf.Length != need) _presentBuf = new byte[need];
                            System.Buffer.BlockCopy(_frame, 0, _presentBuf, 0, need);
                            buf = _presentBuf;
                        }
                    }
                    if (buf != null) _gl.Present(buf, pw, ph);
                    else { _gl.PumpEvents(); Thread.Sleep(1); }   // only before the first frame exists
                }
                else
                {
                    // Present ONLY when the emu produced a NEW frame (Phase 0.1, gate on _frameSeq).
                    // Re-presenting the same buffer every iteration spammed duplicates and turned the cadence
                    // into a 61/60 beat (and polluted GlStats). Copy under the lock (the emu ping-pongs its
                    // two buffers, so the front buffer must not be read off-lock).
                    byte[]? toPresent = null; int pw = 0, ph = 0;
                    lock (_frameLock)
                    {
                        if (_frame != null && _frameSeq != lastSeq)
                        {
                            lastSeq = _frameSeq;
                            pw = _frameW; ph = _frameH; int need = pw * ph * 4;
                            if (_presentBuf == null || _presentBuf.Length != need) _presentBuf = new byte[need];
                            System.Buffer.BlockCopy(_frame, 0, _presentBuf, 0, need);
                            toPresent = _presentBuf;
                        }
                    }
                    if (toPresent == null) { _gl.PumpEvents(); Thread.Sleep(1); continue; }   // no new frame — service events only, don't re-present
                    _gl.Present(toPresent, pw, ph);   // pumps events + vsync swap
                }

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
                    Trace.WriteLine($"[GlStats] DECOUPLED {_glStatCount}f mean={mean:F2}ms ({1000.0 / mean:F1}fps) stddev={Math.Sqrt(variance):F2}ms min={_glStatMin:F2} max={_glStatMax:F2} workMax={_glStatWorkMax:F1}ms gen2gc={gen2gc} bufAge={_gl.LastBufferAge} focus={_gl.IsFocused} swapEma={_glSwapMsEma:F2}ms");
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

        // Present thread, OWN-xdg_toplevel variant (EMUTASTIC_GL_TOPLEVEL=1). Same decoupled contract as
        // PresentThreadProc — show the latest produced frame at vsync, never run the core — but through the
        // libwlpresent shim's own Wayland window (the proven windowed-60 path) instead of SDL's surface.
        // Keyboard arrives via wl_seat (translated to SDL scancodes in the presenter) → reuses OnGlKey.
        private void PresentToplevelProc(System.Threading.ManualResetEventSlim ready)
        {
            double ar = DisplayAspectRatio > 0 ? DisplayAspectRatio : 4.0 / 3.0;
            // Size the window so the GAME AREA (window minus the title/status chrome) equals the display
            // aspect — otherwise the chrome makes the area wider than DAR and the game can't fill it.
            int chrome = (int)GlOsd.TitleBarHeight + (int)GlOsd.StatusBarHeight;
            int winH = 720, winW = Math.Max(1, (int)Math.Round((winH - chrome) * ar));
            _wlTop = WlToplevelPresenter.TryCreate(winW, winH, out string? err);
            if (_wlTop == null)
            {
                Trace.WriteLine($"[Emu] decoupled OWN-TOPLEVEL present unavailable ({err})");
                PresenterResolved?.Invoke(false);
                ready.Set();
                return;
            }
            _wlTop.KeyEvent += OnGlKey; _wlTop.MouseMoved += OnGlMouseMoved; _wlTop.MouseLeft += OnGlMouseLeft;
            Trace.WriteLine("[Emu] GL present ACTIVE (DECOUPLED: own xdg_toplevel + audio-clock emu thread)");
            PresenterResolved?.Invoke(true);
            ready.Set();

            // ── OSD: permanent bottom status line (fps/target/run-avg) + the Windows-style hover HUD pill
            //    (Power · Pause · Reset · Save · Record · | · Cog). Power/Pause/Reset are wired; Save/Record/
            //    Cog are placeholders (drawn + clickable, no action yet — wired in a later phase). 2.5s
            //    auto-hide, 150ms fade-in / 300ms fade-out, mirroring EmulatorWindow.xaml. ──
            const double HudTimeoutMs = 2500;
            var osd = new GlOsd();
            var clock = Stopwatch.StartNew();
            double hudHideAtMs = -1e9;          // HUD hidden until the pointer moves (or while paused)
            bool hudVisible = false; int hover = -1; float hudAlpha = 0f; int titleHover = -1;
            double lastStatusMs = -1e9; string statusText = "Starting…"; int zeroFpsSeconds = 0;

            // Themed title bar: follow the user's WindowButtonStyle (macOS / Windows11 / Linux). The game-host
            // loads the same JSON config the app does, so the choice is honored. Reserve chrome so the game
            // is framed by the title bar (top) + status bar (bottom) rather than covered by them.
            string winStyle = App.Configuration?.GetThemeConfiguration()?.WindowButtonStyle ?? "macOS";
            string title = $"Emutastic — {CoreName}";
            _wlTop.SetInsets((int)GlOsd.TitleBarHeight, (int)GlOsd.StatusBarHeight);
            _wlTop.SetAspect(DisplayAspectRatio);   // render at the display aspect (0 → frame pixel ratio)

            Action<int, bool> onBtn = (button, down) =>
            {
                if (button != 0 || !down) return;
                // 1) Title-bar controls always win (so close/min/max stay clickable even at a corner).
                switch (titleHover)
                {
                    case GlOsd.TbMin:   _wlTop!.Minimize(); return;
                    case GlOsd.TbMax:   _wlTop!.ToggleMaximize(); return;
                    case GlOsd.TbClose: RequestQuit(); return;
                }
                // 2) Edge / corner → interactive resize (grab from anywhere on the border).
                if (!_wlTop!.IsMaximized)
                {
                    _wlTop.GetSize(out int rw, out int rh);
                    int edge = GlOsd.ResizeHitTest(rw, rh, _wlTop.MouseX, _wlTop.MouseY);
                    if (edge != 0) { _wlTop.StartResize(edge); return; }
                }
                // 3) Title-bar interior → drag to move.
                if (titleHover == GlOsd.TbDrag) { _wlTop!.StartMove(); return; }
                // 4) HUD pill.
                if (!hudVisible || hover < 0) return;
                switch (hover)
                {
                    case GlOsd.BtnPower: RequestQuit(); break;
                    case GlOsd.BtnPause: SetPaused(!IsPaused); break;
                    case GlOsd.BtnReset: RequestReset(); break;
                    // Save / Record / Cog: placeholders — no action wired yet (later phase).
                    default: break;
                }
                hudHideAtMs = clock.Elapsed.TotalMilliseconds + HudTimeoutMs;   // any click keeps the HUD up
            };
            Action showHud = () => hudHideAtMs = clock.Elapsed.TotalMilliseconds + HudTimeoutMs;
            _wlTop.PointerButton += onBtn;
            _wlTop.MouseMoved += showHud;   // any pointer motion (re)shows the HUD + restarts the countdown

            double prevNowMs = clock.Elapsed.TotalMilliseconds;
            var pt = Stopwatch.StartNew();
            while (_running && !_wlTop.CloseRequested)
            {
                _wlTop.PumpEvents();   // drain input first so a click/hover affects THIS frame's HUD

                double nowMs = clock.Elapsed.TotalMilliseconds;
                double dt = nowMs - prevNowMs; prevNowMs = nowMs;
                if (nowMs - lastStatusMs >= 1000)
                {
                    lastStatusMs = nowMs;
                    if (IsPaused) { statusText = $"Paused  (target {TargetFps:F0} fps)"; zeroFpsSeconds = 0; }
                    else
                    {
                        SampleStats(out int fr, out double avgRunMs);
                        if (fr == 0) zeroFpsSeconds++; else zeroFpsSeconds = 0;
                        // Exact Windows format (two-space separators); stall hint when no frame for ≥2s.
                        statusText = $"{fr} fps  (target {TargetFps:F0})  core.Run avg {avgRunMs:F1}ms";
                        if (zeroFpsSeconds >= 2) statusText += $"    ⏳ Working… ({zeroFpsSeconds}s with no frame)";
                    }
                }
                _wlTop.GetSize(out int ww, out int wh);
                if (ww <= 0) { ww = winW; wh = winH; }
                hudVisible = IsPaused || nowMs < hudHideAtMs;
                float tgt = hudVisible ? 1f : 0f;                       // 150ms fade-in / 300ms fade-out
                if (hudAlpha < tgt) hudAlpha = (float)Math.Min(tgt, hudAlpha + dt / 150.0);
                else if (hudAlpha > tgt) hudAlpha = (float)Math.Max(tgt, hudAlpha - dt / 300.0);
                hover = (hudVisible && _wlTop.MouseInside) ? GlOsd.HitTest(ww, wh, _wlTop.MouseX, _wlTop.MouseY) : -1;
                titleHover = _wlTop.MouseInside ? GlOsd.TitleHitTest(ww, winStyle, _wlTop.MouseX, _wlTop.MouseY) : -1;
                // Cursor feedback: resize arrows over the edges/corners (but not over the title controls).
                if (_wlTop.MouseInside)
                {
                    bool onCtl = titleHover == GlOsd.TbMin || titleHover == GlOsd.TbMax || titleHover == GlOsd.TbClose;
                    int rEdge = (!_wlTop.IsMaximized && !onCtl) ? GlOsd.ResizeHitTest(ww, wh, _wlTop.MouseX, _wlTop.MouseY) : 0;
                    _wlTop.SetCursorShape(GlOsd.CursorShapeForEdge(rEdge));
                }
                // A transient disc-swap message ("Disk N / M") preempts the fps line while it's active.
                string shownStatus = ActiveDiskMessage ?? statusText;
                if (osd.Build(ww, wh, shownStatus, title, winStyle, _wlTop.IsMaximized, titleHover, hudAlpha, hover, IsPaused))
                    _wlTop.SetOverlay(osd.Pixels, osd.Width, osd.Height);

                // Present the latest frame every iteration; the shim's FIFO swap is the pace (re-presenting a
                // duplicate on a slow frame is correct on Wayland). Copy under the lock — the emu ping-pongs.
                byte[]? buf = null; int pw = 0, ph = 0;
                lock (_frameLock)
                {
                    if (_frame != null)
                    {
                        pw = _frameW; ph = _frameH; int need = pw * ph * 4;
                        if (_presentBuf == null || _presentBuf.Length != need) _presentBuf = new byte[need];
                        System.Buffer.BlockCopy(_frame, 0, _presentBuf, 0, need);
                        buf = _presentBuf;
                    }
                }
                if (buf == null) { Thread.Sleep(1); continue; }   // no frame yet — input already pumped above
                _wlTop.Present(buf, pw, ph);

                double frameMs = pt.Elapsed.TotalMilliseconds; pt.Restart();
                _glSwapMsEma = _glSwapMsEma <= 0 ? _wlTop.LastSwapMs : _glSwapMsEma + 0.05 * (_wlTop.LastSwapMs - _glSwapMsEma);
                if (_glStatGc2Base < 0) _glStatGc2Base = GC.CollectionCount(2);
                double workMs = frameMs - _wlTop.LastSwapMs; if (workMs > _glStatWorkMax) _glStatWorkMax = workMs;
                _glStatSum += frameMs; _glStatSumSq += frameMs * frameMs; _glStatCount++;
                if (frameMs < _glStatMin) _glStatMin = frameMs;
                if (frameMs > _glStatMax) _glStatMax = frameMs;
                if (_glStatCount >= 300)
                {
                    double mean = _glStatSum / _glStatCount;
                    double variance = Math.Max(0, _glStatSumSq / _glStatCount - mean * mean);
                    int gc2now = GC.CollectionCount(2); int gen2gc = gc2now - _glStatGc2Base; _glStatGc2Base = gc2now;
                    Trace.WriteLine($"[GlStats] TOPLEVEL {_glStatCount}f mean={mean:F2}ms ({1000.0 / mean:F1}fps) stddev={Math.Sqrt(variance):F2}ms min={_glStatMin:F2} max={_glStatMax:F2} workMax={_glStatWorkMax:F1}ms gen2gc={gen2gc} swapEma={_glSwapMsEma:F2}ms");
                    _glStatSum = _glStatSumSq = _glStatMax = _glStatWorkMax = 0; _glStatMin = double.MaxValue; _glStatCount = 0;
                }
            }

            _running = false;   // window closed → stop the emu thread
            _wlTop.PointerButton -= onBtn; _wlTop.MouseMoved -= showHud;
            try { osd.Dispose(); } catch { }
            var w = _wlTop; _wlTop = null;
            if (w != null)
            {
                w.KeyEvent -= OnGlKey; w.MouseMoved -= OnGlMouseMoved; w.MouseLeft -= OnGlMouseLeft;
                try { w.Dispose(); } catch { }
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
                        if (dc.get_image_index != IntPtr.Zero) _getImageIndex = Marshal.GetDelegateForFunctionPointer<GetImageIndexFn>(dc.get_image_index);
                        if (dc.get_num_images != IntPtr.Zero) _getNumImages = Marshal.GetDelegateForFunctionPointer<GetNumImagesFn>(dc.get_num_images);
                        _diskControlAvailable = true;
                    }
                    return true;
                case ENV_SET_HW_RENDER:
                    return HandleSetHwRender(data);
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
                _coreOptionSchema.Clear();   // a core may re-announce; the latest set wins
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

                    // Capture for the Preferences tab (filtered values, like upstream — the combo must
                    // not offer values FilterCoreOptionValues removed, e.g. GameCube's buggy 1x/2x).
                    _coreOptionSchema.Add(new Models.CoreOptionEntry
                    {
                        Key = key!,
                        Description = semi >= 0 ? desc[..semi].Trim() : key!,
                        ValidValues = vals,
                        DefaultValue = vals[0],
                    });
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
                    // First query (or value change) only — proves which value the core actually received.
                    Trace.WriteLine($"[Emu] core option {key} = {val}");
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

        // RETRO_ENVIRONMENT_SET_HW_RENDER (env 14): the core wants a GPU context. Phase 1 accepts GL/GLES
        // (context_type 1/2/3/4); Vulkan (6) is declined for now → caller falls through to "unsupported".
        // We give the core our offscreen FBO (get_current_framebuffer) + a symbol resolver (get_proc_address);
        // the actual context + FBO are created post-load in InitHwRenderContext, and context_reset is called
        // THEN (per libretro spec — calling it mid-load breaks mupen/Dolphin). Layout matches LibretroCore's
        // retro_hw_render_callback: type@0, context_reset@8, get_current_framebuffer@16, get_proc_address@24,
        // depth@32, stencil@33, bottom_left_origin@34, version_major@36, version_minor@40, context_destroy@48.
        private bool HandleSetHwRender(IntPtr data)
        {
            if (data == IntPtr.Zero) return false;
            int ctxType = Marshal.ReadInt32(data, 0);
            if (ctxType != 1 && ctxType != 2 && ctxType != 3 && ctxType != 4)
            {
                Trace.WriteLine($"[Emu] SET_HW_RENDER context_type={ctxType} not supported yet (GL only in phase 1) — declining");
                return false;   // Vulkan(6)/others: phase 2
            }
            _hwCtxType = ctxType;
            _hwDepth   = Marshal.ReadByte(data, 32) != 0;
            _hwStencil = Marshal.ReadByte(data, 33) != 0;
            _hwBottomLeft = Marshal.ReadByte(data, 34) != 0;
            _hwMajor = Marshal.ReadInt32(data, 36);
            _hwMinor = Marshal.ReadInt32(data, 40);
            IntPtr resetPtr = Marshal.ReadIntPtr(data, 8);
            IntPtr destroyPtr = Marshal.ReadIntPtr(data, 48);
            _hwContextReset   = resetPtr   != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<HwContextResetFn>(resetPtr)   : null;
            _hwContextDestroy = destroyPtr != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<HwContextResetFn>(destroyPtr) : null;
            // Hand the core our callbacks (keep the delegates alive as fields so the pointers stay valid).
            _hwGetFb   = () => (UIntPtr)Platform.HwGlContext.Fbo();
            _hwGetProc = sym => Platform.HwGlContext.Proc(sym);
            Marshal.WriteIntPtr(data, 16, Marshal.GetFunctionPointerForDelegate(_hwGetFb));
            Marshal.WriteIntPtr(data, 24, Marshal.GetFunctionPointerForDelegate(_hwGetProc));
            _hwRenderActive = true;
            Trace.WriteLine($"[Emu] SET_HW_RENDER GL accepted: type={ctxType} v{_hwMajor}.{_hwMinor} depth={_hwDepth} stencil={_hwStencil} bottomLeft={_hwBottomLeft}");
            return true;
        }

        // Create the offscreen GL context + FBO and fire context_reset. Runs on the emu thread AFTER
        // retro_load_game (av_info now valid → we size the FBO to the core's max geometry).
        private void InitHwRenderContext()
        {
            if (!_hwRenderActive) return;
            var geo = _core!.AvInfo.geometry;
            int maxW = (int)Math.Max(geo.max_width, geo.base_width);
            int maxH = (int)Math.Max(geo.max_height, geo.base_height);
            if (!Platform.HwGlContext.Init(_hwCtxType, _hwMajor, _hwMinor, _hwDepth, _hwStencil, maxW, maxH))
            {
                Trace.WriteLine("[Emu] HW-render GL context init FAILED — 3D core will not render");
                _hwRenderActive = false;
                return;
            }
            Platform.HwGlContext.MakeCurrent();   // stays current on this (emu) thread for every retro_run
            // Frame buffers sized to the FBO MAX (the async readback may return any frame size up to it).
            int maxBytes = Math.Max(1, maxW * maxH * 4);
            _hwBufA = new byte[maxBytes]; _hwBufB = new byte[maxBytes];
            Trace.WriteLine($"[Emu] HW-render context ready ({maxW}x{maxH}); calling context_reset");
            // Which device did the surfaceless EGL context land on? llvmpipe ↔ real GPU flips the
            // readback cost ~1ms ↔ ~11ms, and the native stderr line is dropped when app-launched.
            Trace.WriteLine($"[Emu] HW-render {Platform.HwGlContext.Info()}");
            try { _hwContextReset?.Invoke(); } catch (Exception ex) { Trace.WriteLine($"[Emu] context_reset threw: {ex}"); }
        }

        // ── In-game disc switching (L3 + Start chord) ───────────────────────────────────────────────
        // Wraps SdlInput.GetInputState so we can inject a JOYPAD_L press on port 0 for FDS "disk side
        // change" (FDS cores don't expose the disk-control interface — they read an L press instead).
        private short InputState_cb(uint port, uint device, uint index, uint id)
        {
            if (port == 0 && device == SdlInput.RETRO_DEVICE_JOYPAD
                && id == LibretroInput.JOYPAD_L && _fdsSideChangeFrames > 0)
                return 1;
            return _input.GetInputState(port, device, index, id);
        }

        // Called once per emu frame (after input poll). Detects the disc-swap chord (rising edge) and
        // ticks the FDS-injection + deferred-reinsert countdowns.
        private void ServiceDiskSwap()
        {
            if (_fdsSideChangeFrames > 0) _fdsSideChangeFrames--;
            if (_diskInsertPendingFrames > 0 && --_diskInsertPendingFrames == 0)
            {
                try { _setEjectState?.Invoke(false); } catch (Exception ex) { Trace.WriteLine($"[Emu] disk deferred insert failed: {ex.Message}"); }
            }

            // Chord = L3 + Start on controller 0, read raw so it works regardless of the per-console
            // mapping (NES/FDS etc. don't map L3). Rising edge so a held chord fires once.
            bool held = _input.IsRawButtonDown(SdlInput.SdlButtonLeftStick)
                     && _input.IsRawButtonDown(SdlInput.SdlButtonStart);
            if (held && !_diskSwapPrevHeld) SwapToNextDisk();
            _diskSwapPrevHeld = held;
        }

        // Cycle to the next disc image (eject → set index → deferred re-insert), mirroring RetroArch's
        // timing. FDS uses the JOYPAD_L injection path instead of the disk-control interface.
        private void SwapToNextDisk()
        {
            // FDS-family: cores expose no disk-control interface; inject JOYPAD_L for ~6 frames.
            if (!_diskControlAvailable && string.Equals(_console, "FDS", StringComparison.OrdinalIgnoreCase))
            {
                _fdsSideChangeFrames = 6;
                ShowDiskMessage("Disk: side change");
                return;
            }
            if (!_diskControlAvailable)
            {
                ShowDiskMessage("Disc switch: not supported by this core");
                return;
            }
            if (_getNumImages == null || _setImageIndex == null || _setEjectState == null)
            {
                ShowDiskMessage("Disc switch: incomplete disc interface");
                return;
            }
            try
            {
                uint count = _getNumImages.Invoke();
                if (count <= 1)
                {
                    ShowDiskMessage("Disc switch: only one disc — put all discs in one folder and re-import");
                    return;
                }
                uint cur = _getImageIndex?.Invoke() ?? 0;
                uint next = (cur + 1) % count;
                // RetroArch's pattern: eject + set index immediately, defer re-insert ~100 frames (Beetle
                // PSX's CD engine expects the disc to spin down between swaps; others tolerate it).
                bool ejected = _getEjectState?.Invoke() ?? false;
                if (!ejected) _setEjectState.Invoke(true);
                _setImageIndex.Invoke(next);
                _diskInsertPendingFrames = 100;
                ShowDiskMessage($"Disk {next + 1} / {count}");
                Trace.WriteLine($"[Emu] disc swap {cur} -> {next} of {count}");
            }
            catch (Exception ex) { Trace.WriteLine($"[Emu] disc swap failed: {ex.Message}"); }
        }

        // Surface a transient disc-swap message in the OSD status line for ~3s (read by the present loop).
        private void ShowDiskMessage(string msg)
        {
            _diskMsg = msg;
            _diskMsgUntil = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
        }

        /// <summary>The active disc-swap OSD message, or null if none is currently showing.</summary>
        public string? ActiveDiskMessage => (_diskMsg.Length > 0 && Stopwatch.GetTimestamp() < _diskMsgUntil) ? _diskMsg : null;

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
            try
            {
                string f = Marshal.PtrToStringAnsi(fmt) ?? "";
                string msg = FormatCoreLog(f, a0, a1, a2, a3);
                string[] labels = { "DEBUG", "INFO", "WARN", "ERROR" };
                string tag = level < (uint)labels.Length ? labels[level] : $"L{level}";
                System.Diagnostics.Trace.WriteLine($"[CORE {tag}] {msg.TrimEnd('\n', '\r')}");
            }
            catch { /* never let a log call throw back into native code */ }
        }

        /// <summary>
        /// Minimal printf formatter for core log messages (port of upstream FormatCoreLog).
        /// Handles the common specifiers cores use (%s, %d, %i, %u, %x, %X, %ld, %02d, etc.).
        /// ABI NOTE (differs from upstream/Windows): on Linux x86-64 (SysV) variadic floats
        /// go in XMM registers ONLY — they are NOT mirrored into the integer registers our
        /// a0..a3 slots capture. So float specifiers print a placeholder and must NOT advance
        /// argIdx (the next integer arg still arrives in the next integer register). This is
        /// the exact inverse of the Windows rule documented upstream.
        /// Covers the first 4 integer varargs (rdx, rcx, r8, r9); later args print literally.
        /// </summary>
        private static string FormatCoreLog(string fmt, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3)
        {
            if (!fmt.Contains('%')) return fmt;

            var args = new IntPtr[] { a0, a1, a2, a3 };
            int argIdx = 0;

            return System.Text.RegularExpressions.Regex.Replace(fmt,
                @"%%|%[-+0 #]*\d*(?:\.\d+)?(hh?|ll?|[Lqjzt])?([diouxXscpfFgGeE])",
                m =>
                {
                    if (m.Value == "%%") return "%";
                    char type = m.Groups[2].Value[0];

                    // Floats live in XMM registers we don't capture — placeholder, no slot consumed.
                    if (type is 'f' or 'F' or 'g' or 'G' or 'e' or 'E') return "(flt)";

                    if (argIdx >= args.Length) return m.Value;
                    IntPtr arg = args[argIdx++];
                    string spec = m.Value;
                    bool wide = m.Groups[1].Value.StartsWith("l") || m.Groups[1].Value is "j" or "z" or "t";

                    // Honour width/precision from the original specifier where practical.
                    string widthStr = System.Text.RegularExpressions.Regex.Match(spec, @"0?(\d+)").Groups[1].Value;
                    int width = int.TryParse(widthStr, out int w) ? w : 0;
                    bool zeroPad = spec.Contains('0') && !spec.Contains('-');

                    return type switch
                    {
                        's' => Marshal.PtrToStringAnsi(arg) ?? "(null)",
                        // 32-bit ints arrive in 64-bit registers; the SysV caller need not
                        // zero/sign-extend, so truncate unless an 'l'-class length says 64-bit.
                        'd' or 'i' => PadNum((wide ? (long)arg : (int)(long)arg).ToString(), width, zeroPad),
                        'u'        => PadNum((wide ? (ulong)arg : (uint)(ulong)arg).ToString(), width, zeroPad),
                        'x'        => PadNum((wide ? (ulong)arg : (uint)(ulong)arg).ToString("x"), width, zeroPad),
                        'X'        => PadNum((wide ? (ulong)arg : (uint)(ulong)arg).ToString("X"), width, zeroPad),
                        'p'        => "0x" + ((ulong)arg).ToString("x16"),
                        'c'        => ((char)(byte)arg).ToString(),
                        _          => m.Value
                    };
                });
        }

        private static string PadNum(string s, int width, bool zeroPad)
            => width > 0 ? (zeroPad ? s.PadLeft(width, '0') : s.PadLeft(width)) : s;

        private unsafe void Video_cb(IntPtr data, uint width, uint height, UIntPtr pitch)
        {
            // HW-rendered core: the frame lives in our GL FBO (data == RETRO_HW_FRAME_BUFFER_VALID). Read it
            // back to BGRA; data==0 means "duplicate, nothing new". Runs inside retro_run on the emu thread,
            // where the HW context is current. The SW pixel-copy path below is never used by HW cores.
            if (_hwRenderActive)
            {
                // Dupe frame (data != VALID): COUNT it for the fps display — N64 cores dupe VIs when the
                // game renders below 60 internally (OoT = 20fps) and Windows' counter includes dupes, so
                // ours must too — but do NOT redo the GPU readback. Upstream re-reads the FBO on dupes,
                // but on our driver per-call readback at 60Hz tripled hwReadback (1ms → ~11ms) and dragged
                // the emu thread to ~43fps in real (focused) play. The present thread keeps showing the
                // latest frame regardless, so skipping the readback loses nothing.
                if (data != RETRO_HW_FRAME_BUFFER_VALID || width == 0 || height == 0)
                {
                    System.Threading.Interlocked.Increment(ref _frameCountSample);
                    return;
                }
                if (_hwBufA != null && _hwBufB != null)
                {
                    // TRUE double-buffer: always read into the buffer the present thread is NOT holding,
                    // so it can't copy a half-written frame (the transparent-flash cause). Async PBO readback
                    // returns the PREVIOUS frame + its dims (ow/oh) — present those, not the current cb dims.
                    byte[] back = ReferenceEquals(_frame, _hwBufA) ? _hwBufB : _hwBufA;
                    long t0 = Stopwatch.GetTimestamp();
                    bool ok = Platform.HwGlContext.Readback(back, (int)width, (int)height, _hwBottomLeft, out int ow, out int oh);
                    _hwReadbackMs += 0.05 * ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency - _hwReadbackMs);
                    if (ok && ow > 0 && oh > 0)
                    {
                        lock (_frameLock) { _frame = back; _frameW = ow; _frameH = oh; _frameSeq++; }
                        FrameReady?.Invoke();
                    }
                    // Count the frame even when the never-block ring had no completed readback yet
                    // (the core DID render; its pixels just land 1-3 frames later) — the fps display
                    // must reflect the core's cadence, like Windows.
                    System.Threading.Interlocked.Increment(ref _frameCountSample);
                }
                return;
            }
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
