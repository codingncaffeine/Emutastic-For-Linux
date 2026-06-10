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
        const uint ENV_SET_INPUT_DESCRIPTORS = 11; // per-port button labels (feeds the turbo menu)
        const uint ENV_GET_SYSTEM_DIRECTORY = 9;
        const uint ENV_SET_PIXEL_FORMAT = 10;
        const uint ENV_SET_SYSTEM_AV_INFO = 32;  // full AV reset (geometry + timing) mid-session
        const uint ENV_SET_GEOMETRY = 37;        // lightweight geometry change (NDS layout switch etc.)
        const uint ENV_GET_VARIABLE = 15;
        const uint ENV_SET_VARIABLES = 16;
        const uint ENV_GET_VARIABLE_UPDATE = 17;
        const uint ENV_GET_CORE_OPTIONS_VERSION = 52;
        const uint ENV_GET_LOG_INTERFACE = 27;
        const uint ENV_GET_CORE_ASSETS_DIRECTORY = 30;
        const uint ENV_GET_SAVE_DIRECTORY = 31;
        const uint ENV_SET_DISK_CONTROL_INTERFACE = 13;
        const uint ENV_GET_RUMBLE_INTERFACE = 23;
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
        // Frame-delivery diagnostics: how often the core hands us a real HW frame vs a dupe.
        // A core that never calls video_cb at all shows 0/0 here while audio keeps flowing
        // (PPSSPP's retro_run skips video_cb when its render thread has nothing ready).
        private long _vidValid, _vidDupes;
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
        // ── Rumble (upstream OnSetRumbleState): struct retro_rumble_interface is a single
        //    set_rumble_state function pointer. effect 0 = RETRO_RUMBLE_STRONG (low-freq/left),
        //    1 = RETRO_RUMBLE_WEAK (high-freq/right). Cores send each motor independently, so
        //    P1 accumulates both before applying (upstream tracks accumulators for port 0 only).
        //    Providing the interface also matters beyond vibration: Reicast/Flycast won't
        //    initialise maple-bus sub-peripherals (VMU, Purupuru) without it. ─────────────────
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetRumbleStateFn(uint port, uint effect, ushort strength);
        private readonly SetRumbleStateFn _rumbleCb;   // kept alive; handed to the core via GET_RUMBLE_INTERFACE
        private ushort _rumbleStrong, _rumbleWeak;     // P1 motor accumulators

        private bool OnSetRumbleState(uint port, uint effect, ushort strength)
        {
            if (port >= 4) return true;
            if (port == 0)
            {
                if (effect == 0) _rumbleStrong = strength; else _rumbleWeak = strength;
                _input.SetRumble(0, _rumbleStrong, _rumbleWeak);
            }
            else
                // Ports 1-3: apply directly (no cross-frame accumulation, upstream parity)
                _input.SetRumble((int)port,
                    effect == 0 ? strength : (ushort)0,
                    effect == 1 ? strength : (ushort)0);
            return true;
        }

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
        private volatile bool _userFlip;     // cog "Flip Display": extra 180° composed with the core rotation (session-local, like upstream)

        // ── Turbo / autofire (upstream parity): per-port set of JOYPAD ids modulated at RetroArch's
        // defaults — period 6 frames, duty 3 (~10Hz at 60fps), clocked once per emu frame. The cog
        // toggle swaps a FRESH set into the slot (reference assignment is atomic), so the emu thread's
        // per-read snapshot never sees a mid-mutation set.
        private readonly HashSet<uint>[] _turboButtons = { new(), new(), new(), new() };
        private long _turboFrames;           // emu-frame counter driving the duty cycle (emu thread only)
        private const long TurboPeriodFrames = 6;
        private const long TurboDutyFrames = 3;
        // Buttons that are never turbo-able regardless of user choice (upstream blacklist).
        private static readonly HashSet<uint> TurboBlacklist = new()
        {
            LibretroInput.JOYPAD_SELECT, LibretroInput.JOYPAD_START,
            LibretroInput.JOYPAD_UP, LibretroInput.JOYPAD_DOWN,
            LibretroInput.JOYPAD_LEFT, LibretroInput.JOYPAD_RIGHT,
            LibretroInput.JOYPAD_L3, LibretroInput.JOYPAD_R3,
        };
        // Per-port JOYPAD id → label from SET_INPUT_DESCRIPTORS, so the turbo menu lists only the
        // buttons the current core actually uses (e.g. NES = just B and A).
        private readonly Dictionary<uint, string>[] _joypadDescriptors = { new(), new(), new(), new() };
        private volatile bool _descriptorsReceived;

        // ── Bezels (The Bezel Project) + Vectrex overlays: static deco layers in the wlpresent shim.
        // Art is fetched/decoded on a background task; the decoded RGBA lands in a pending slot that
        // the PRESENT thread uploads (the GL context lives there), then the show flags rule. The
        // bezel forces the render DAR to its own AR while active so the game lands in its cutout.
        private volatile byte[]? _pendingBezelRgba; private int _pendingBezelW, _pendingBezelH;
        private volatile byte[]? _pendingGovRgba; private int _pendingGovW, _pendingGovH;
        private volatile bool _bezelRowVisible, _bezelActive, _bezelLoaded, _bezelFetching;
        private volatile bool _govRowVisible, _govActive;                           // Vectrex overlay state

        // NDS screen-layout display names (upstream's NdsLayoutLabels — DeSmuME option values).
        // melonDS values are already human-readable and pass through unmapped.
        private static readonly Dictionary<string, string> NdsLayoutLabels = new()
        {
            { "top/bottom",    "Top / Bottom" },
            { "bottom/top",    "Bottom / Top" },
            { "left/right",    "Side by Side" },
            { "right/left",    "Side by Side (reversed)" },
            { "top only",      "Top Screen Only" },
            { "bottom only",   "Bottom Screen Only" },
            { "hybrid/top",    "Hybrid (Top focus)" },
            { "hybrid/bottom", "Hybrid (Bottom focus)" },
        };

        // ── Built-in shader presets (upstream's ShaderPreset enum, same order/names — the index is
        // the shim's wlp_set_shader id). Persisted per game as shader_{gameId} = enum name; a saved
        // "slang:" value (downloaded pack, not yet ported on Linux) degrades gracefully to None.
        private static readonly (string EnumName, string Display)[] ShaderPresets =
        {
            ("None", "None"), ("CrtScanlines", "CRT Scanlines"), ("GameBoyDmg", "Game Boy (DMG)"),
            ("GameBoyDmgLcd", "Game Boy (DMG LCD)"), ("GameBoyPocket", "Game Boy Pocket"),
            ("LcdGrid", "LCD Grid"), ("Smooth", "Smooth"),
        };
        private int _shaderPreset;   // present-thread state (restored at init, set by the cog)
        private string? _glslpRel;   // active downloaded .glslp (relative path) — overrides the built-in

        /// <summary>Per-game window size from the parent ("--win-size", saved at last session end);
        /// 0 = use the default. The host can't read per-game config rows itself.</summary>
        public int RestoreWinW, RestoreWinH;
        private long _frameSeq;
        private int _frameCountSample;            // frames the core PRODUCED since last sample (emulation rate)
        // Display cadence: unique game frames actually presented to screen since the last HUD
        // sample, counted on the present thread when _frameSeq advances between presents. Below
        // the emulation rate when presentation (compositor / GPU / busy UI) is the bottleneck;
        // that gap is what the HUD's "emu N" suffix surfaces. (Port of upstream's display-vs-emu
        // split — on Linux the present thread is already decoupled, so this is the natural place.)
        private int _displayFrameSample;
        private long _lastPresentedSeq = -1;
        private bool _fxLayerShown;   // pause-effect GPU layer currently uploaded (cleared once on resume)
        // EMUTASTIC_FPS_LOG=1 mirrors the per-second HUD stats line to emulator-host.log.
        private static readonly bool FpsLogEnabled =
            Environment.GetEnvironmentVariable("EMUTASTIC_FPS_LOG") == "1";
        // EMUTASTIC_FULLRES_READBACK=1 disables the HW downscale-before-readback stage.
        private static readonly bool FullresReadback =
            Environment.GetEnvironmentVariable("EMUTASTIC_FULLRES_READBACK") == "1";
        private int _lastTargetW, _lastTargetH;   // present-target last pushed to the native side
        private long _coreRunTicks, _coreRunCalls; // accumulated retro_run time + call count for avg ms
        private double _coreRunMsEma;              // smoothed per-frame retro_run cost (decoupled loop diag)
        private double _paceWaitMsEma, _cushionWaitMsEma; // where the rest of the frame goes (diag)
        private double _audioAddedMsEma;                  // smoothed audio-ms one retro_run produces (the game's clock)
        private readonly Dictionary<string, string> _deferredOptions = new();  // applied at frame 1 (DeferUntilAfterLoad)

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
        private IGamePresenter? _wlTop;   // Wayland shim OR the X11/SDL fallback — same OSD loop
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
        const int SC_ESCAPE = 41, SC_F11 = 68, SC_P = 19, SC_F5 = 62, SC_F7 = 64, SC_F9 = 66;
        const int SC_F12 = 69, SC_PRINTSCREEN = 70;

        // Emu-thread handler for the GL window's keyboard. Game buttons feed SdlInput's player-1 fallback;
        // a few non-game scancodes drive the session (quit / fullscreen / pause).
        private void OnGlKey(int scancode, bool down)
        {
            // Disk-swap keyboard chord held-state — tracked before the game-button dispatch so a
            // chord half that doubles as a game key (e.g. Enter=Start) still registers.
            if (scancode == _diskSwapKeySCa) _diskSwapKeyAHeld = down;
            if (scancode == _diskSwapKeySCb) _diskSwapKeyBHeld = down;
            if (_glKeyMap.TryGetValue(scancode, out int id)) { _input.SetKeyboardButton(id, down); return; }
            if (!down) return;
            switch (scancode)
            {
                case SC_ESCAPE: _running = false; break;
                case SC_F11:    _glFullscreen = !_glFullscreen; _gl?.SetFullscreen(_glFullscreen); break;
                case SC_P:      _paused = !_paused; break;
                case SC_F5:     RequestSaveState("Quick Save"); break;   // upstream's F5 quick save
                case SC_F7:     RequestQuickLoad(); break;               // upstream's F7 quick load
                case SC_F9:     ToggleRecording(); break;                // upstream's F9 record toggle
                // Screenshot (upstream's hotkey rules): PrintScreen always fires as the
                // hardware-baked fallback; the configured key (Preferences → Media, default
                // F12) takes the other slot.
                case SC_PRINTSCREEN: TakeScreenshot(); break;
                default:
                    if (scancode == (_screenshotScancode ??= ResolveScreenshotScancode())) TakeScreenshot();
                    break;
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
            _coreName = System.IO.Path.GetFileNameWithoutExtension(corePath);
            _handler = ConsoleHandlerFactory.Create(console);
            _handler.CoreFileName = _coreName;   // so the handler can return core-specific options (N64: parallel vs mupen)
            foreach (var kv in _handler.GetDefaultCoreOptions())   // pre-seed this console's curated options
                _coreOptions[kv.Key] = kv.Value;
            // User choices from the Preferences "Core Options" tab override the handler's curated
            // defaults (upstream priority order). Values the core won't accept are repaired against
            // its valid list in ParseSetVariables.
            foreach (var kv in _coreOptionsStore.LoadValues(_coreName))
                _coreOptions[kv.Key] = kv.Value;
            // Hold back load-fragile options (handler-declared): invisible during retro_load_game
            // (GET_VARIABLE misses -> core default), applied via the live path at frame 1 below.
            foreach (var k in _handler.DeferUntilAfterLoad)
                if (_coreOptions.Remove(k, out var deferredVal))
                    _deferredOptions[k] = deferredVal;
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
            _rumbleCb = OnSetRumbleState;
        }

        /// <summary>Loads the core+ROM and starts the emulation thread. Returns false on failure.</summary>
        public bool Start(out string? error)
        {
            error = null;
            try
            {
                _input.Initialize();
                _input.LoadConfiguration(_console, App.Configuration);   // honor the Controls-panel bindings
                LoadDiskSwapChord();                                     // P1 "Disk Swap" chord (or L3+Start default)
                // In-game controller hot-plug feedback: named connect/disconnect in the same
                // transient OSD slot as disc swaps — mirrors upstream EmulatorWindow's status-tick
                // diff (be69750). Fires on the emu thread; ShowDiskMessage only sets volatile fields.
                _input.DeviceChanged += (connected, name) =>
                    ShowDiskMessage($"Controller {(connected ? "connected" : "disconnected")}: {name}", 5);
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
                // ROM-hack entries share the base ROM file (and thus its stem); disambiguate their
                // battery save by the entry's (patched) hash so a hack never shares the base game's
                // .srm (upstream's rule; cloud sync mirrors it in LocalSrmPathFor).
                string srmStem = System.IO.Path.GetFileNameWithoutExtension(_romPath);
                if (!string.IsNullOrEmpty(PatchPath) && !string.IsNullOrEmpty(SaveRomHash))
                    srmStem += "." + SaveRomHash[..Math.Min(8, SaveRomHash.Length)];
                _srmPath = System.IO.Path.Combine(saveDir, srmStem + ".srm");
                _systemDirPtr = Marshal.StringToHGlobalAnsi(sysDir);
                _saveDirPtr = Marshal.StringToHGlobalAnsi(saveDir);
                _coreAssetsDirPtr = Marshal.StringToHGlobalAnsi(coreDir);
                _core.SetCallbacks(_envCb, _videoCb, _audioCb, _audioBatchCb, _inputPollCb, _inputStateCb);
                _core.Init();

                // ROM-hack soft patch (IPS/BPS/UPS): LoadGame applies it to the in-memory buffer;
                // the base ROM file on disk is never modified.
                if (!_core.LoadGame(_romPath, PatchPath))
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
                ReloadCheats();   // per-game cheats apply from frame one (no-op without --game-id)
                _sampleRate = _core.AvInfo.timing.sample_rate > 0 ? _core.AvInfo.timing.sample_rate : 44100;
                // DIAGNOSTIC ONLY (EMUTASTIC_NO_AUDIO=1): skip opening the sound device to test whether the
                // audio subsystem is what's dragging the present off a clean 60. Never a shipping setting.
                Trace.WriteLine($"[Emu] av_info: fps={_fps:F4} sample_rate={_sampleRate:F1} → target={1000.0 / _fps:F2}ms/frame, {_sampleRate / _fps:F1} audio-frames/frame");
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

            // RetroAchievements (A8c): login + identify on a worker thread so a slow RA
            // server can never delay first frame. RaDoFrame() no-ops until the game loads.
            new Thread(InitRetroAchievements) { IsBackground = true, Name = "RaInit" }.Start();

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
                if (_paused) { RaIdle(); Thread.Sleep(16); frameTimer.Restart(); continue; }

                if (!_noInputPoll) _input.Poll();
                ServiceDiskSwap();   // disc-swap chord (L3+Start) + FDS/deferred-insert ticks
                if (_saveStatePending) ExecuteSaveOnEmuThread();   // between retro_run calls, like upstream
                if (_loadStatePending) ExecuteLoadOnEmuThread();
                if (_cheatsApplyPending) ExecuteCheatsApplyOnEmuThread();
                if (_inputReloadPending) ExecuteInputReloadOnEmuThread();

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
                    System.Threading.Interlocked.Increment(ref _coreRunCalls); RaDoFrame();
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
                        System.Threading.Interlocked.Increment(ref _coreRunCalls); RaDoFrame();

                        // Low-watermark catch-up: buffer dipped below the cushion → run one extra frame so
                        // audio refills instead of underrunning (the latest video frame still wins). Counted
                        // in the stats so the fps readout stays honest.
                        if (_running && _audio != null && _audio.QueuedMs < lowWatermark)   // smooth time-based estimate
                        {
                            _input.Poll();
                            long t2 = frameTimer.ElapsedTicks;
                            try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                            System.Threading.Interlocked.Add(ref _coreRunTicks, frameTimer.ElapsedTicks - t2);
                            System.Threading.Interlocked.Increment(ref _coreRunCalls); RaDoFrame();
                        }
                    }
                }

                ApplyFrontendArToRam();   // hold AR-cheat values every frame (post-run, like upstream)

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
            // RetroArch audio-backpressure pacing for self-pacing cores (PPSSPP) — see PspHandler.
            bool backpressurePace = _handler.PaceByAudioBackpressure;
            while (_running)
            {
                if (_resetRequested) { _resetRequested = false; try { _core!.Reset(); } catch (Exception ex) { Trace.WriteLine($"[Emu] reset threw: {ex}"); } }
                if (_paused) { RaIdle(); Thread.Sleep(16); frameTimer.Restart(); continue; }
                if (!_noInputPoll) _input.Poll();
                ServiceDiskSwap();   // disc-swap chord (L3+Start) + FDS/deferred-insert ticks
                if (_saveStatePending) ExecuteSaveOnEmuThread();   // between retro_run calls, like upstream
                if (_loadStatePending) ExecuteLoadOnEmuThread();
                if (_cheatsApplyPending) ExecuteCheatsApplyOnEmuThread();
                if (_inputReloadPending) ExecuteInputReloadOnEmuThread();
                _audio?.ApplyDrc();

                long audioBefore = _audio?.FramesWrittenTotal ?? 0;
                long runT0 = frameTimer.ElapsedTicks;
                try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                ApplyFrontendArToRam();   // hold AR-cheat values every frame (post-run, like upstream)
                long runTicks = frameTimer.ElapsedTicks - runT0;
                System.Threading.Interlocked.Add(ref _coreRunTicks, runTicks);
                System.Threading.Interlocked.Increment(ref _coreRunCalls); RaDoFrame();
                double coreRunMs = runTicks * 1000.0 / Stopwatch.Frequency;
                _coreRunMsEma = _coreRunMsEma <= 0 ? coreRunMs : _coreRunMsEma + 0.05 * (coreRunMs - _coreRunMsEma);

                // Deferred options (DeferUntilAfterLoad): now that the core booted with its own
                // defaults, push the held-back values through the live variables-dirty path —
                // identical to a cog-menu change one frame in.
                if (_deferredOptions.Count > 0)
                {
                    foreach (var kv in _deferredOptions)
                    {
                        _coreOptions[kv.Key] = kv.Value;
                        Trace.WriteLine($"[Emu] core option (deferred post-load) {kv.Key} = {kv.Value}");
                    }
                    _deferredOptions.Clear();
                    _coreOptionsDirty = true;
                }

                // THE GAME'S INTERNAL CLOCK: how much audio did THIS retro_run produce? A 30fps-content
                // Dreamcast title (Hydro Thunder) emits ~33ms per run even though the console outputs
                // 60Hz — pacing such a run to the nominal 16.7ms floods the queue at 2× realtime and
                // turns the cushion backstop into multi-second drain stalls (the burst/starve bug:
                // 18fps bursts, 3-4s gaps, coreRun ~9ms — upstream paces HW cores by audio progress
                // for exactly this reason, see upstream EmulatorWindow "advanced N game frames").
                // Smoothed (EMA) so cores that batch audio irregularly don't jitter the pace; silent
                // scenes (no audio added) fall back to the nominal target.
                if (_audio != null)
                {
                    double addedMs = (_audio.FramesWrittenTotal - audioBefore) * 1000.0 / _sampleRate;
                    if (addedMs > 0.5)
                        _audioAddedMsEma = _audioAddedMsEma <= 0 ? addedMs : _audioAddedMsEma + 0.1 * (addedMs - _audioAddedMsEma);
                }
                // Lower bound is the NOMINAL frame time, not half of it: audio-progress pacing only
                // ever EXTENDS a run's budget (a run that advanced N>1 game frames produces N frames
                // of audio and should wait longer), never SHORTENS it below the content rate. A core
                // that emits less audio per run than a frame's worth (parallel_n64 reports ~14ms vs the
                // 16.6ms frame — sample-rate/AI quirk) must still pace to its av_info rate, or it free-
                // runs fast (N64 hit ~72fps). Upper bound 4× still allows the Dreamcast 30fps case.
                double tPaceStart = frameTimer.Elapsed.TotalMilliseconds;
                int guard = 0;
                if (backpressurePace && _audio != null && _audio.IsOpen)
                {
                    // RETRO-ARCH AUDIO-BACKPRESSURE PACING (runloop_iterate audio_sync path): the device
                    // drains at realtime, so blocking until the queue falls back to the cushion setpoint
                    // (cushionMs == DrcTargetMs == prefill level) paces the core to its NATURAL fps — no
                    // Thread.Sleep-to-a-computed-budget on top. For a core that self-paces its CPU to wall
                    // clock (PPSSPP), the audio-progress budget below double-paces into a feedback loop
                    // that drifts off 60 (~50fps), and the Sleep granularity overshoots and drains the
                    // queue (DRC pinned, audio pops). Holding the queue at the DRC setpoint keeps DRC near
                    // zero-error (ratio ~1, no fight) and the drain rate is the clock.
                    // Block until BOTH: (a) the queue drained to the cushion (audio realtime), AND (b) at
                    // least one refresh has elapsed since this frame started (minimum spacing). Without (b)
                    // the emu can BURST several retro_runs when the queue briefly dips below cushion (after
                    // any present hitch), and the decoupled present — which only ever shows the LATEST frame
                    // — drops the intermediates, so visible cadence collapses to ~35 while emu still reads 60.
                    // The spacing floor equals the audio period in steady state, so it only bites on bursts.
                    while (_running && guard++ < 8000 &&
                           (_audio.QueuedMsReal > cushionMs || frameTimer.Elapsed.TotalMilliseconds < targetFrameMs))
                    {
                        double remaining = Math.Max(_audio.QueuedMsReal - cushionMs,
                                                    targetFrameMs - frameTimer.Elapsed.TotalMilliseconds);
                        if (remaining > 1.5) Thread.Sleep(1); else Thread.SpinWait(40);
                    }
                }
                else
                {
                    double paceMs = _audioAddedMsEma > 0.5
                        ? Math.Clamp(_audioAddedMsEma, targetFrameMs, targetFrameMs * 4)
                        : targetFrameMs;

                    // PACE TO THE CONTENT RATE (Phase 0.2 + audio-progress correction): the budget is the
                    // audio each run actually adds (the game's clock), bounded around the handler/core
                    // nominal rate. RetroArch's model (slave the loop to a rate, DRC resamples audio to
                    // match) — rather than free-running on audio drain, which drifted to ~61fps and beat
                    // against the ~60Hz display. Production-timing jitter here is harmless: the present
                    // thread is vsync-paced SEPARATELY and shows the latest frame.
                    while (_running && frameTimer.Elapsed.TotalMilliseconds < paceMs && guard++ < 8000)
                    {
                        double remaining = paceMs - frameTimer.Elapsed.TotalMilliseconds;
                        if (remaining > 1.5) Thread.Sleep(1); else Thread.SpinWait(40);
                    }
                }
                double tCushionStart = frameTimer.Elapsed.TotalMilliseconds;
                int cushionIters = 0;
                // Audio cushion cap (secondary): if the core ran ahead and the buffer overfilled well past
                // the cushion, drain before the next frame so audio can't run away. DRC handles the ±0.5%
                // trim. ENTER on the SMOOTHED occupancy (a single device gulp can't fire a spurious
                // drain-stall) but EXIT on the LIVE queue: the smoothed value is only refreshed by
                // ApplyDrc at the loop top, so polling it inside this wait reads a FROZEN number — the
                // loop then always ran its full 4000-iteration guard (~4s of Thread.Sleep(1)) while the
                // real queue drained to empty underneath. That frozen-estimate spin WAS the transition
                // stall / burst-starve bug (Dreamcast tab entry; ~207ms/frame drain on the old box).
                if (_audio != null && _audio.IsOpen && _audio.QueuedMsSmoothed > cushionMs + 40)
                {
                    guard = 0;
                    while (_running && _audio.QueuedMsReal > cushionMs + 40 && guard++ < 4000) Thread.Sleep(1);
                    cushionIters = guard;
                }
                double tEnd = frameTimer.Elapsed.TotalMilliseconds;
                double paceWaitMs = tCushionStart - tPaceStart;   // time spent in the rate-pace loop
                double cushionWaitMs = tEnd - tCushionStart;      // time spent in the audio cushion-drain loop
                _paceWaitMsEma    = _paceWaitMsEma    <= 0 ? paceWaitMs    : _paceWaitMsEma    + 0.05 * (paceWaitMs    - _paceWaitMsEma);
                _cushionWaitMsEma = _cushionWaitMsEma <= 0 ? cushionWaitMs : _cushionWaitMsEma + 0.05 * (cushionWaitMs - _cushionWaitMsEma);
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
                    Trace.WriteLine($"[Emu] DECOUPLED emu={_frameMsEma:F2}ms(~{fps:F1}fps) target={targetFrameMs:F1}ms coreRun={_coreRunMsEma:F2}ms audioAdded={_audioAddedMsEma:F2}ms paceWait={_paceWaitMsEma:F1}ms cushionWait={_cushionWaitMsEma:F1}ms DRC q={qms:F0}ms ratio={ratio:F5} underruns={underruns}{hwRb} vid(valid={_vidValid} dupe={_vidDupes})");
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
            // X11/SDL fallback (no Wayland): run the SAME OSD loop over the SDL presenter. This
            // branch used to be a bare present loop — pure-X11 users (XFCE etc.) got a game window
            // with NO in-game UI at all (no status line, HUD pill, or cog menu).
            double ar = DisplayAspectRatio > 0 ? DisplayAspectRatio : 4.0 / 3.0;
            // Native WM provides the title bar here; only the OSD status bar comes out of the height.
            int chrome = (int)GlOsd.StatusBarHeight;
            int winH = 720, winW = Math.Max(1, (int)Math.Round((winH - chrome) * ar));
            if (RestoreWinW > 200 && RestoreWinH > 150) { winW = RestoreWinW; winH = RestoreWinH; }
            _glFullscreen = Environment.GetEnvironmentVariable("EMUTASTIC_GL_FULLSCREEN") == "1";
            var gl = GlPresenter.TryCreate(winW, winH, _glFullscreen, out string? glErr);
            if (gl == null)
            {
                Trace.WriteLine($"[Emu] decoupled GL present unavailable ({glErr})");
                PresenterResolved?.Invoke(false);
                ready.Set();
                return;
            }
            _wlTop = gl;
            Trace.WriteLine("[Emu] GL present ACTIVE (DECOUPLED: present thread + audio-clock emu thread)");
            RunPresenterOsdLoop(ready, "DECOUPLED");
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
            // Per-game remembered size (saved at session end, handed down by the parent) wins
            // over the AR-derived default — resize Mario Bros once, it reopens that way.
            if (RestoreWinW > 200 && RestoreWinH > 150) { winW = RestoreWinW; winH = RestoreWinH; }
            _wlTop = WlToplevelPresenter.TryCreate(winW, winH, out string? err);
            if (_wlTop == null)
            {
                Trace.WriteLine($"[Emu] decoupled OWN-TOPLEVEL present unavailable ({err})");
                PresenterResolved?.Invoke(false);
                ready.Set();
                return;
            }
            Trace.WriteLine("[Emu] GL present ACTIVE (DECOUPLED: own xdg_toplevel + audio-clock emu thread)");
            RunPresenterOsdLoop(ready, "TOPLEVEL");
        }

        // The shared present/OSD loop — drives whichever IGamePresenter the caller stored in _wlTop
        // (Wayland shim or X11/SDL fallback). Capability flags gate what a backend can't do; chrome
        // (title bar + caption buttons) is OSD-drawn only when the window is borderless.
        private void RunPresenterOsdLoop(System.Threading.ManualResetEventSlim ready, string statLabel)
        {
            _wlTop!.KeyEvent += OnGlKey; _wlTop.MouseMoved += OnGlMouseMoved; _wlTop.MouseLeft += OnGlMouseLeft;
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
            string winStyle = _wlTop.HasWindowChrome
                ? App.Configuration?.GetThemeConfiguration()?.WindowButtonStyle ?? "macOS"
                : "none";   // native WM decorations — the OSD draws no title bar
            string title = $"Emutastic — {CoreName}";
            _wlTop.SetInsets(_wlTop.HasWindowChrome ? (int)GlOsd.TitleBarHeight : 0, (int)GlOsd.StatusBarHeight);
            _wlTop.SetAspect(DisplayAspectRatio);   // render at the display aspect (0 → frame pixel ratio)
            if (_wlTop.HasDecoLayers) InitDecorations();   // bezel + Vectrex overlay (art loads off-thread)
            // Restore the per-game shader (upstream's RestoreShaderPreset): a built-in enum name,
            // or "glsl:<relpath>" for a downloaded pack preset (Linux runs the GLSL pack — a
            // Windows-saved "slang:" value degrades to None, mirroring upstream's missing-pack
            // fallback). Missing/failed presets degrade silently to None the same way.
            if (CheatGameId >= 0 && _wlTop.HasShaderChain)
            {
                string saved = App.Configuration?.GetValue($"shader_{CheatGameId}", "None") ?? "None";
                if (saved.StartsWith("glsl:", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = saved.Substring(5);
                    string? abs = Services.ShaderCatalog.Resolve(rel);
                    if (abs != null && _wlTop.SetGlslp(abs)) _glslpRel = rel;
                }
                else
                {
                    int idx = Array.FindIndex(ShaderPresets, p => p.EnumName.Equals(saved, StringComparison.OrdinalIgnoreCase));
                    if (idx > 0) { _shaderPreset = idx; _wlTop.SetShader(idx); }
                }
            }

            // Save-state load picker (upstream's inline LoadPickerPanel): null = closed.
            List<(string Name, string RelTime, string Path)>? statePicker = null;
            int pickerHover = -1;

            // Pause effect (the Theme tab's animation, upstream parity): starts on pause, fades out
            // on resume. PauseFx renders the same Skia frames the Preferences preview shows.
            Views.PauseEffects.PauseFx? pauseFx = null;
            bool wasPaused = false;

            // Cog menu (upstream's OverlayMenu): rows carry an action key; null key = placeholder
            // (drawn greyed). "\x01VISUALS"/"\x01BACK" switch levels; other keys are core options
            // cycled live via CycleCoreOption. Item order/labels mirror upstream's XAML.
            List<(string Label, bool Enabled, string? Value, string? Key)>? cogMenu = null;
            int cogHover = -1;
            Func<List<(string, bool, string?, string?)>> buildCogMain = () =>
            {
                var m = new List<(string, bool, string?, string?)>
                {
                    // Asks the MAIN APP to open Preferences → Controls for this console (the panel
                    // lives there; the request crosses processes via the stdout command channel).
                    ("Edit Game Controls…", EmitHostCommand != null, null, "\x01CONTROLS"),
                    ("Turbo Buttons…",      true, "›", "\x01TURBO"),
                    (_userFlip ? "Flip Display ✓" : "Flip Display", true, null, "\x01FLIP"),
                };
                // Shader picker — SW cores only, like upstream (OverlayShaderBtn is collapsed for
                // HW cores: their frames carry the core's own enhanced rendering).
                if (!_hwRenderActive && _wlTop?.HasShaderChain == true)
                {
                    string shLabel = _glslpRel != null
                        ? $"Shader: {Path.GetFileNameWithoutExtension(_glslpRel)}"
                        : $"Shader: {ShaderPresets[_shaderPreset].Display}";
                    m.Add((shLabel, true, "›", "\x01SHADER"));
                }
                // Vectrex overlay / bezel rows appear only when their art exists for this game
                // (upstream keeps OverlayToggleBtn/BezelToggleBtn Collapsed the same way).
                if (_govRowVisible)
                    m.Add((_govActive ? "Overlay: On" : "Overlay: Off", true, null, "\x01OVERLAY"));
                if (_bezelRowVisible)
                    m.Add((_bezelActive ? "Bezel: On" : "Bezel: Off", true, null, "\x01BEZEL"));
                // NDS screen layout (upstream's OverlayScreenLayoutBtn): cycle the core's layout
                // option — DeSmuME and melonDS announce different keys; show whichever this
                // session's core declared. Applies live via the generic CycleCoreOption path.
                if (HandlerConsoleName == "NDS")
                {
                    string layoutKey = CoreOptionValue("desmume_screens_layout").Length > 0
                        ? "desmume_screens_layout"
                        : CoreOptionValue("melonds_screen_layout").Length > 0 ? "melonds_screen_layout" : "";
                    if (layoutKey.Length > 0)
                    {
                        string cur = CoreOptionValue(layoutKey);
                        m.Add(("Screen Layout", true, NdsLayoutLabels.GetValueOrDefault(cur, cur), layoutKey));
                    }
                }
                if (HandlerConsoleName == "N64" && CoreOptionValue("mupen64plus-pak1").Length > 0)
                    m.Add(("Pak", true, CoreOptionValue("mupen64plus-pak1"), "mupen64plus-pak1"));
                m.Add((IsRecording ? "Stop Recording" : "Record", true, null, "\x01RECORD"));
                m.Add(("View Recordings", true, null, "\x01VIEWREC"));
                // Cheats: live when the core supports them (or legacy cheats exist for this game) and
                // we know which game this is (--game-id; absent on bare CLI launches).
                bool cheatable = CheatGameId >= 0 && !RaHardcoreActive
                    && (Services.CheatSupport.Lookup(_corePath).Level != Services.CheatSupportLevel.NotSupported
                        || CheatsSnapshot().Count > 0);
                m.Add(("Cheats", cheatable, "›", cheatable ? "\x01CHEATS" : null));
                // Notes / Manual windows live in the MAIN APP (Avalonia + DB; the host has
                // neither) — the request crosses processes like Controls/Cheats. They need
                // --game-id to know which game (absent on bare CLI launches).
                bool gameLinked = CheatGameId >= 0 && EmitHostCommand != null;
                m.Add(("Notes",  gameLinked, null, gameLinked ? "\x01NOTES" : null));
                m.Add(("Manual", gameLinked, null, gameLinked ? "\x01MANUAL" : null));
                if (VisualOptions.Count > 0)
                    m.Add(("Visuals", true, "›", "\x01VISUALS")); // live core options (upstream's Visuals panel)
                return m;
            };
            Func<List<(string, bool, string?, string?)>> buildCogVisuals = () =>
            {
                var m = new List<(string, bool, string?, string?)> { ("‹ Back", true, null, "\x01BACK") };
                foreach (var (key, label) in VisualOptions)
                {
                    string cur = CoreOptionValue(key);
                    // Unannounced this session (core didn't expose it) → greyed with n/a.
                    m.Add(cur.Length > 0 ? (label, true, cur, key) : (label, false, "n/a", null));
                }
                return m;
            };
            // Turbo level (upstream's TurboButtonsDialog as a cog submenu): one pill-toggle row per
            // turbo-able button. With descriptors, they are authoritative for EVERY port (a single-port
            // NES must not render phantom P2-P4 rows); without them, fall back to the canonical 8 for
            // port 0. Row keys "\x03<port>:<id>" toggle + save through the parent. PAGINATED — the GL
            // menu doesn't scroll, and 4 declared ports × 8 buttons would overflow the window.
            const int TurboPageSize = 12;
            int turboPage = 0;
            Func<List<(string, bool, string?, string?)>> buildCogTurbo = () =>
            {
                var m = new List<(string, bool, string?, string?)> { ("‹ Back", true, null, "\x01BACK") };
                var fallback = new Dictionary<uint, string>
                {
                    { 0, "B" }, { 1, "Y" }, { 8, "A" }, { 9, "X" },
                    { 10, "L" }, { 11, "R" }, { 12, "L2" }, { 13, "R2" },
                };
                // Prefix rows with the player number only when more than one port has buttons.
                int portsWithButtons = _descriptorsReceived
                    ? _joypadDescriptors.Count(d => d.Keys.Any(k => !TurboBlacklist.Contains(k))) : 1;
                var rows = new List<(string, bool, string?, string?)>();
                for (int p = 0; p < 4; p++)
                {
                    var btns = _descriptorsReceived ? _joypadDescriptors[p] : (p == 0 ? fallback : null);
                    if (btns == null) continue;
                    var set = _turboButtons[p];
                    foreach (var (id, label) in btns.OrderBy(kv => kv.Key))
                    {
                        if (TurboBlacklist.Contains(id)) continue;
                        // Cores with native autofire declare separate "Turbo A"/"Turbo B" buttons
                        // (FCEUmm etc.) — adding OUR turbo to a turbo button is meaningless and
                        // made the list read as duplicates, so hide those entries.
                        if (label.Contains("turbo", StringComparison.OrdinalIgnoreCase)) continue;
                        string row = portsWithButtons > 1 ? $"P{p + 1} · {label}" : label;
                        rows.Add((row, true, set.Contains(id) ? "\x01ON" : "\x01OFF", $"\x03{p}:{id}"));
                    }
                }
                if (rows.Count == 0) { m.Add(("No turbo-able buttons", false, null, null)); return m; }
                int pages = Math.Max(1, (rows.Count + TurboPageSize - 1) / TurboPageSize);
                turboPage %= pages;   // "more…" advances past the last page → wrap to the first
                m.AddRange(rows.Skip(turboPage * TurboPageSize).Take(TurboPageSize));
                if (pages > 1)
                    m.Add(($"more… ({turboPage + 1}/{pages})", true, "›", "\x07"));
                return m;
            };
            // Shader level (upstream's ShaderPanel picker, as a cog submenu): the 7 built-ins with
            // a check on the active one, then one row per downloaded-pack category ("crt ›" …).
            // Row keys: "\x04<index>" built-in, "\x05<cat>:<page>" open category, "\x06<relpath>"
            // pick a downloaded preset. Category pages are capped (the GL menu doesn't scroll).
            const int ShaderPageSize = 12;
            // The top shader menu lists the built-in presets first, then one row per downloaded
            // GLSL-pack category. The full libretro pack is ~28 categories, so the combined list
            // overflows the non-scrolling cog menu (it grows upward from the HUD pill and runs off
            // the top of the screen) — which hid the built-ins (Game Boy etc.) and the early-
            // alphabet categories. Paginate it like the category submenu so every row stays
            // reachable; built-ins always land on page 0. "\x0e<page>" turns the page (wraps).
            Func<int, List<(string, bool, string?, string?)>> buildCogShader = (page) =>
            {
                var all = new List<(string, bool, string?, string?)>();
                for (int i = 0; i < ShaderPresets.Length; i++)
                    all.Add((ShaderPresets[i].Display, true,
                             _glslpRel == null && i == _shaderPreset ? "✓" : null, $"\x04{i}"));
                var downloaded = Services.ShaderCatalog.GetDownloaded();
                foreach (var cat in downloaded.Select(d => d.Category).Distinct())
                    all.Add(($"{cat} ›", true,
                             _glslpRel != null && _glslpRel.StartsWith(cat + "/", StringComparison.OrdinalIgnoreCase) ? "✓" : null,
                             $"\x05{cat}:0"));

                int pages = Math.Max(1, (all.Count + ShaderPageSize - 1) / ShaderPageSize);
                page = Math.Clamp(page, 0, pages - 1);
                var m = new List<(string, bool, string?, string?)> { ("‹ Back", true, null, "\x01BACK") };
                foreach (var it in all.Skip(page * ShaderPageSize).Take(ShaderPageSize))
                    m.Add(it);
                if (pages > 1)
                    m.Add(($"more… ({page + 1}/{pages})", true, "›", $"\x0e{(page + 1) % pages}"));
                return m;
            };
            Func<string, int, List<(string, bool, string?, string?)>> buildCogShaderCat = (cat, page) =>
            {
                var m = new List<(string, bool, string?, string?)> { ("‹ Back", true, null, "\x01SHADER") };
                var items = Services.ShaderCatalog.GetDownloaded()
                    .Where(d => d.Category.Equals(cat, StringComparison.OrdinalIgnoreCase)).ToList();
                int pages = Math.Max(1, (items.Count + ShaderPageSize - 1) / ShaderPageSize);
                page = Math.Clamp(page, 0, pages - 1);
                foreach (var it in items.Skip(page * ShaderPageSize).Take(ShaderPageSize))
                    m.Add((it.Display, true, _glslpRel == it.RelativePath ? "✓" : null, $"\x06{it.RelativePath}"));
                if (pages > 1)
                    m.Add(($"more… ({page + 1}/{pages})", true, "›", $"\x05{cat}:{(page + 1) % pages}"));
                return m;
            };
            // Cheats level (upstream's CheatsMenu): Add/Import actions + one toggle row per cheat.
            // Row keys "\x02<index>" toggle; the unsupported hint mirrors CheatsUnsupportedHint.
            Func<List<(string, bool, string?, string?)>> buildCogCheats = () =>
            {
                var m = new List<(string, bool, string?, string?)> { ("‹ Back", true, null, "\x01BACK") };
                m.Add(("Add Cheat…", EmitHostCommand != null, null, "\x01ADDCHEAT"));
                m.Add(("Import from Database…", Services.CheatDatabaseService.IsInstalled(), null, "\x01IMPORTCHEATS"));
                if (Services.CheatSupport.Lookup(_corePath).Level == Services.CheatSupportLevel.NotSupported)
                    m.Add(("This core does not support cheats.", false, null, null));
                var cheats = CheatsSnapshot();
                for (int i = 0; i < cheats.Count; i++)
                    // \x01ON/\x01OFF = GlOsd's pill-toggle sentinel (the Windows-style 34×18 toggle).
                    m.Add((cheats[i].Title, true, cheats[i].Enabled ? "\x01ON" : "\x01OFF", $"\x02{i}"));
                return m;
            };

            Action<int, bool> onBtn = (button, down) =>
            {
                // While paused, right-click rotates the pause-screen effect to the next one in the
                // catalog (round-robin, skipping "None") and persists the pick — upstream's
                // GameScreen_RightDown. Doesn't fire during gameplay so it can't interfere with
                // mouse-driven cores (CDi / MAME).
                if (button == 1 && down && IsPaused)
                {
                    var rotation = Views.PauseEffects.PauseEffectRegistry.All
                        .Where(e => !string.Equals(e.Id, Views.PauseEffects.PauseEffectRegistry.NoneId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (rotation.Count == 0) return;
                    var theme = App.Configuration?.GetThemeConfiguration();
                    string currentId = theme?.PauseEffect ?? Views.PauseEffects.PauseEffectRegistry.NoneId;
                    int idx = rotation.FindIndex(e => string.Equals(e.Id, currentId, StringComparison.OrdinalIgnoreCase));
                    string nextId = rotation[idx < 0 ? 0 : (idx + 1) % rotation.Count].Id;
                    if (theme != null) theme.PauseEffect = nextId;          // host-local, for the next pause edge
                    EmitHostCommand?.Invoke($"set-pause-effect {nextId}");  // main app persists to config
                    double inten = Math.Clamp(theme?.PauseEffectIntensity ?? 1.0, 0.5, 2.0);
                    _wlTop!.GetSize(out int fw, out int fh);
                    pauseFx ??= new Views.PauseEffects.PauseFx();
                    pauseFx.Start(nextId, inten, fw, fh);                   // restart immediately, like upstream
                    ShowDiskMessage($"Pause effect: {rotation[idx < 0 ? 0 : (idx + 1) % rotation.Count].DisplayName}", 2);
                    return;
                }
                if (button != 0 || !down) return;
                // 0a) Open cog menu: enabled-row clicks act, panel clicks swallow, elsewhere closes
                //     (upstream collapses OverlayMenu the same way).
                if (cogMenu != null && titleHover < 0)
                {
                    _wlTop!.GetSize(out int mrw, out int mrh);
                    int row = GlOsd.MenuHitTest(mrw, mrh, cogMenu.Count, _wlTop.MouseX, _wlTop.MouseY);
                    if (row >= 0)
                    {
                        var (_, enabled, _, key) = cogMenu[row];
                        if (!enabled || key == null) return;       // placeholder — swallow
                        if (key == "\x01VISUALS") cogMenu = buildCogVisuals();
                        else if (key == "\x01CHEATS") cogMenu = buildCogCheats();
                        else if (key == "\x01BACK") cogMenu = buildCogMain();
                        else if (key == "\x01CONTROLS")
                        {
                            EmitHostCommand?.Invoke($"open-controls {HandlerConsoleName}");
                            cogMenu = null;                        // close, like upstream after navigation
                        }
                        else if (key == "\x01ADDCHEAT")
                        {
                            // The editor dialog lives in the main app; it saves the JSON and sends
                            // "reload-cheats" back down our stdin. Keep the panel open — the next
                            // rebuild (toggle/back/reopen) shows the new entry.
                            EmitHostCommand?.Invoke($"open-cheat-editor {CheatGameId}");
                            cogMenu = null;
                        }
                        else if (key == "\x01NOTES")
                        {
                            EmitHostCommand?.Invoke($"open-notes {CheatGameId}");
                            cogMenu = null;
                        }
                        else if (key == "\x01MANUAL")
                        {
                            EmitHostCommand?.Invoke($"open-manual {CheatGameId}");
                            cogMenu = null;
                        }
                        else if (key == "\x01FLIP")
                        {
                            // Upstream's OverlayFlip_Click: toggle the extra 180° and collapse the
                            // menu. The flip is baked into the next published frame, so no presenter
                            // or AR poke is needed (180° preserves the aspect).
                            _userFlip = !_userFlip;
                            // While paused no new frame is published, so flip the frozen frame NOW
                            // (upstream's transform applies instantly). Copy-rotate + swap under the
                            // lock: in-flight presents keep the old (now-immutable) buffer; the seq
                            // bump makes the present loop upload the flipped one. Toggling flip is
                            // always exactly 180° of the published frame, whatever the core rotation.
                            if (_paused)
                            {
                                lock (_frameLock)
                                {
                                    if (_frame != null && _frameW > 0 && _frameH > 0)
                                    {
                                        int fw = _frameW, fh = _frameH;
                                        _frame = RotateBgra(_frame, ref fw, ref fh, 180);
                                        _frameSeq++;
                                    }
                                }
                            }
                            cogMenu = null;
                        }
                        else if (key == "\x01OVERLAY")
                        {
                            // Vectrex overlay toggle (upstream's OverlayToggle_Click). Texture is
                            // decoded/uploaded at init, so this is just a show flag + persistence.
                            _govActive = !_govActive;
                            _wlTop!.ShowGameOverlay(_govActive);
                            if (CheatGameId >= 0)
                                Services.VectrexOverlayService.SetOverlayEnabled(CheatGameId, _govActive);
                            cogMenu = buildCogMain();   // refresh the On/Off label
                        }
                        else if (key == "\x01BEZEL")
                        {
                            // Bezel toggle (upstream's BezelToggle_Click). First enable fetches the
                            // art (network) + decodes off-thread; it shows when the upload lands.
                            if (!_bezelLoaded && !_bezelFetching)
                            {
                                _bezelFetching = true; _bezelActive = true;
                                ShowDiskMessage("Fetching bezel…", 3);
                                string bezRom = _romPath, bezConsole = HandlerConsoleName;
                                _ = System.Threading.Tasks.Task.Run(async () =>
                                {
                                    string? png = await Services.BezelService
                                        .EnsureBezelAsync(bezRom, bezConsole).ConfigureAwait(false);
                                    if (png == null || !QueueDecoPng(png, bezel: true))
                                    {
                                        _bezelActive = false;
                                        ShowDiskMessage("No bezel available for this game", 4);
                                    }
                                    _bezelFetching = false;
                                });
                            }
                            else
                            {
                                // The shim fits the game (at ITS aspect) inside the bezel rect, so
                                // no render-DAR change is needed here.
                                _bezelActive = !_bezelActive;
                                _wlTop!.ShowBezel(_bezelActive);
                            }
                            if (CheatGameId >= 0)
                                Services.BezelService.SetEnabledForGame(CheatGameId, _bezelActive);
                            cogMenu = buildCogMain();   // refresh the On/Off label
                        }
                        else if (key == "\x01RECORD")
                        {
                            ToggleRecording();
                            cogMenu = buildCogMain();   // refresh the Record ↔ Stop Recording label
                        }
                        else if (key == "\x01VIEWREC")
                        {
                            OpenRecordingsFolder();
                            cogMenu = null;
                        }
                        else if (key == "\x01IMPORTCHEATS")
                        {
                            int added = ImportCheatsFromDatabase();
                            ShowDiskMessage(added < 0 ? "No cheats found for this game in the database"
                                : added == 0 ? "All database cheats already imported"
                                : $"Added {added} cheat{(added == 1 ? "" : "s")} (off by default)", 4);
                            cogMenu = buildCogCheats();
                        }
                        else if (key == "\x01TURBO") { turboPage = 0; cogMenu = buildCogTurbo(); }
                        else if (key == "\x07") { turboPage++; cogMenu = buildCogTurbo(); }   // next turbo page (builder wraps)
                        else if (key == "\x01SHADER") cogMenu = buildCogShader(0);
                        else if (key.Length > 1 && key[0] == '\x0e' && int.TryParse(key.AsSpan(1), out int shMenuPage))
                            cogMenu = buildCogShader(shMenuPage);   // turn the shader-menu page
                        else if (key.Length > 1 && key[0] == '\x04' && int.TryParse(key.AsSpan(1), out int shaderIdx)
                                 && shaderIdx >= 0 && shaderIdx < ShaderPresets.Length)
                        {
                            // Apply live (we're on the present thread — the GL context is here) and
                            // persist per game through the parent, like upstream's immediate save.
                            _shaderPreset = shaderIdx;
                            _glslpRel = null;             // SetShader clears the chain in the shim too
                            _wlTop!.SetShader(shaderIdx);
                            if (CheatGameId >= 0)
                                EmitHostCommand?.Invoke($"save-shader {CheatGameId} {ShaderPresets[shaderIdx].EnumName}");
                            cogMenu = buildCogShader(0);   // refresh the check mark (built-ins are on page 0)
                        }
                        else if (key.Length > 1 && key[0] == '\x05')
                        {
                            // Open a downloaded-pack category page: "\x05<cat>:<page>".
                            var spec = key.Substring(1);
                            int colon = spec.LastIndexOf(':');
                            string cat = colon > 0 ? spec[..colon] : spec;
                            int page = colon > 0 && int.TryParse(spec.AsSpan(colon + 1), out int pg) ? pg : 0;
                            cogMenu = buildCogShaderCat(cat, page);
                        }
                        else if (key.Length > 1 && key[0] == '\x06')
                        {
                            // Pick a downloaded preset: compile + activate the chain right here
                            // (present thread). Failure (unsupported feature, compile error) keeps
                            // the previous state and says so, like upstream's IsReady fallback.
                            string rel = key.Substring(1);
                            string? abs = Services.ShaderCatalog.Resolve(rel);
                            if (abs != null && _wlTop!.SetGlslp(abs))
                            {
                                _glslpRel = rel;
                                if (CheatGameId >= 0)
                                    EmitHostCommand?.Invoke($"save-shader {CheatGameId} glsl:{rel}");
                            }
                            else
                            {
                                // The failed load cleared any active chain in the shim.
                                _glslpRel = null;
                                ShowDiskMessage("Shader failed to load — see emulator.log", 4);
                            }
                            int slash = rel.IndexOf('/');
                            cogMenu = buildCogShaderCat(slash > 0 ? rel[..slash] : "misc", 0);
                        }
                        else if (key.Length > 1 && key[0] == '\x02' && int.TryParse(key.AsSpan(1), out int cheatIdx))
                        {
                            ToggleCheat(cheatIdx);                 // persists + queues live re-apply
                            cogMenu = buildCogCheats();
                        }
                        else if (key.Length > 1 && key[0] == '\x03')
                        {
                            // Turbo toggle: "\x03<port>:<id>". Applies live; persisted per game by the
                            // PARENT (save-on-click, like upstream's dialog — the host writes no config).
                            var spec = key.AsSpan(1);
                            int colon = spec.IndexOf(':');
                            if (colon > 0 && int.TryParse(spec[..colon], out int tPort)
                                && uint.TryParse(spec[(colon + 1)..], out uint tId) && tPort is >= 0 and < 4)
                            {
                                string csv = ToggleTurbo(tPort, tId);
                                if (CheatGameId >= 0)
                                    EmitHostCommand?.Invoke($"save-turbo {CheatGameId} {tPort} {csv}");
                            }
                            cogMenu = buildCogTurbo();
                        }
                        else
                        {
                            CycleCoreOption(key);                  // live option change (variables-dirty)
                            // Rebuild the open level so the row shows the new value immediately.
                            cogMenu = cogMenu.Any(i => i.Key == "\x01BACK") ? buildCogVisuals() : buildCogMain();
                        }
                        return;
                    }
                    if (row == -2) return;                          // panel chrome — swallow
                    cogMenu = null;                                 // clicked elsewhere — close, fall through
                }
                // 0b) Open load picker: row click loads, panel clicks swallow, anywhere else closes it
                //     (matches upstream's toggle behavior).
                if (statePicker != null && titleHover < 0)
                {
                    _wlTop!.GetSize(out int prw, out int prh);
                    int row = GlOsd.PickerHitTest(prw, prh, statePicker.Count, _wlTop.MouseX, _wlTop.MouseY);
                    if (row >= 0)
                    {
                        var pick = statePicker[row];
                        statePicker = null;
                        RequestLoadState(pick.Path, pick.Name);
                        return;
                    }
                    if (row == -2) return;   // inside panel chrome — swallow
                    statePicker = null;      // clicked elsewhere — close, then fall through to normal handling
                }
                // 1) Title-bar controls always win (so close/min/max stay clickable even at a corner).
                switch (titleHover)
                {
                    case GlOsd.TbMin:   _wlTop!.Minimize(); return;
                    case GlOsd.TbMax:   _wlTop!.ToggleMaximize(); return;
                    case GlOsd.TbClose: RequestQuit(); return;
                }
                // 2) Edge / corner → interactive resize (grab from anywhere on the border —
                //    borderless shim only; the native WM owns resize on the SDL path).
                if (_wlTop!.HasWindowChrome && !_wlTop.IsMaximized)
                {
                    _wlTop.GetSize(out int rw, out int rh);
                    int edge = GlOsd.ResizeHitTest(rw, rh, _wlTop.MouseX, _wlTop.MouseY);
                    if (edge != 0) { _wlTop.StartResize(edge); return; }
                }
                // 3) Title-bar interior → drag to move.
                if (titleHover == GlOsd.TbDrag) { _wlTop!.StartMove(); return; }
                // 4) Status-bar Save State / Load State buttons (always visible, like upstream's).
                _wlTop.GetSize(out int sbw, out int sbh);
                switch (GlOsd.StatusHitTest(sbw, sbh, _wlTop.MouseX, _wlTop.MouseY))
                {
                    case GlOsd.StatusBtnSave:
                        statePicker = null;   // upstream collapses the picker before saving
                        cogMenu = null;
                        RequestSaveState(DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
                        return;
                    case GlOsd.StatusBtnLoad:
                        cogMenu = null;
                        if (RaHardcoreActive)   // hidden in hardcore, but gate the hit-test too
                        {
                            ShowDiskMessage("Save state loading is disabled in hardcore mode", 4);
                            return;
                        }
                        statePicker = statePicker == null ? RecentSaveStates() : null;
                        return;
                }
                // 5) HUD pill.
                if (!hudVisible || hover < 0) return;
                switch (hover)
                {
                    case GlOsd.BtnPower: RequestQuit(); break;
                    case GlOsd.BtnPause: SetPaused(!IsPaused); break;
                    case GlOsd.BtnReset: RequestReset(); ShowDiskMessage("Game reset", 2); break;   // upstream OverlayReset_Click feedback
                    // Pill Save mirrors the status-bar Save State button (timestamped, like upstream).
                    case GlOsd.BtnSave:
                        RequestSaveState(DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
                        break;
                    // Record: toggle capture; icon turns red while recording (upstream parity).
                    case GlOsd.BtnRecord:
                        ToggleRecording();
                        break;
                    // Cog: toggle the settings menu (upstream's OverlayMenu).
                    case GlOsd.BtnCog:
                        statePicker = null;
                        cogMenu = cogMenu == null ? buildCogMain() : null;
                        break;
                    default: break;
                }
                hudHideAtMs = clock.Elapsed.TotalMilliseconds + HudTimeoutMs;   // any click keeps the HUD up
            };
            Action showHud = () => hudHideAtMs = clock.Elapsed.TotalMilliseconds + HudTimeoutMs;
            _wlTop.PointerButton += onBtn;
            _wlTop.MouseMoved += showHud;   // any pointer motion (re)shows the HUD + restarts the countdown

            double prevNowMs = clock.Elapsed.TotalMilliseconds;
            var pt = Stopwatch.StartNew();
            long presentIters = 0;   // diag: present-loop spins/sec vs unique frames picked up (displayFps)
            while (_running && !_wlTop.CloseRequested)
            {
                presentIters++;
                _wlTop.PumpEvents();   // drain input first so a click/hover affects THIS frame's HUD

                double nowMs = clock.Elapsed.TotalMilliseconds;
                double dt = nowMs - prevNowMs; prevNowMs = nowMs;
                if (nowMs - lastStatusMs >= 1000)
                {
                    lastStatusMs = nowMs;
                    if (IsPaused) { statusText = $"Paused  (target {TargetFps:F0} fps)"; zeroFpsSeconds = 0; }
                    else
                    {
                        SampleStats(out int emuFps, out double avgRunMs);
                        int displayFps = _displayFrameSample; _displayFrameSample = 0;
                        // Stall hint keys on the CORE (emuFps): a compile/load hitch is the core
                        // not producing, distinct from presentation falling behind.
                        if (emuFps == 0) zeroFpsSeconds++; else zeroFpsSeconds = 0;
                        // Headline is display cadence (frames actually shown); "target" is always
                        // the goal rate. When the core steps faster than the screen presents, append
                        // "emu N" — that gap means presentation (compositor/GPU/UI), not the core,
                        // is the bottleneck. (Upstream's display-vs-emu split; two-space separators.)
                        statusText = (emuFps - displayFps > 2 && displayFps > 0)
                            ? $"{displayFps} fps  (target {TargetFps:F0}, emu {emuFps})  core.Run avg {avgRunMs:F1}ms"
                            : $"{displayFps} fps  (target {TargetFps:F0})  core.Run avg {avgRunMs:F1}ms";
                        // EMUTASTIC_FPS_LOG=1: mirror to emulator-host.log (benchmarking hook).
                        if (FpsLogEnabled)
                            Trace.WriteLine($"[fps] display={displayFps} emu={emuFps} target={TargetFps:F0} core.Run={avgRunMs:F1}ms presentLoop={presentIters}/s");
                        presentIters = 0;
                        if (zeroFpsSeconds >= 2) statusText += $"    ⏳ Working… ({zeroFpsSeconds}s with no frame)";
                    }
                }
                _wlTop.GetSize(out int ww, out int wh);
                if (ww <= 0) { ww = 960; wh = 720; }   // pre-first-configure fallback (size unknown yet)
                // Feed the window size to the HW readback's downscale stage (no-op for SW cores;
                // EMUTASTIC_FULLRES_READBACK=1 keeps full-internal-res readback for quality recording).
                if (_hwRenderActive && !FullresReadback && (ww != _lastTargetW || wh != _lastTargetH))
                {
                    Platform.HwGlContext.SetPresentTarget(ww, wh);
                    _lastTargetW = ww; _lastTargetH = wh;
                }
                hudVisible = IsPaused || nowMs < hudHideAtMs || cogMenu != null;   // cog menu pins the pill
                float tgt = hudVisible ? 1f : 0f;                       // 150ms fade-in / 300ms fade-out
                if (hudAlpha < tgt) hudAlpha = (float)Math.Min(tgt, hudAlpha + dt / 150.0);
                else if (hudAlpha > tgt) hudAlpha = (float)Math.Max(tgt, hudAlpha - dt / 300.0);
                hover = (hudVisible && _wlTop.MouseInside) ? GlOsd.HitTest(ww, wh, _wlTop.MouseX, _wlTop.MouseY) : -1;
                titleHover = (_wlTop.HasWindowChrome && _wlTop.MouseInside) ? GlOsd.TitleHitTest(ww, winStyle, _wlTop.MouseX, _wlTop.MouseY) : -1;
                // Cursor feedback: resize arrows over the edges/corners (but not over the title controls).
                if (_wlTop.HasWindowChrome && _wlTop.MouseInside)
                {
                    bool onCtl = titleHover == GlOsd.TbMin || titleHover == GlOsd.TbMax || titleHover == GlOsd.TbClose;
                    int rEdge = (!_wlTop.IsMaximized && !onCtl) ? GlOsd.ResizeHitTest(ww, wh, _wlTop.MouseX, _wlTop.MouseY) : 0;
                    _wlTop.SetCursorShape(GlOsd.CursorShapeForEdge(rEdge));
                }
                // Pause-effect lifecycle: start on the pause edge (per the Theme tab's setting),
                // fade out on resume, tick into its frame while anything is visible.
                if (IsPaused != wasPaused)
                {
                    wasPaused = IsPaused;
                    if (IsPaused)
                    {
                        var theme = App.Configuration?.GetThemeConfiguration();
                        string fxId = theme?.PauseEffect ?? "none";
                        double inten = Math.Clamp(theme?.PauseEffectIntensity ?? 1.0, 0.5, 2.0);
                        if (fxId != Views.PauseEffects.PauseEffectRegistry.NoneId)
                        {
                            pauseFx ??= new Views.PauseEffects.PauseFx();
                            pauseFx.Start(fxId, inten, ww, wh);
                        }
                    }
                    else pauseFx?.Stop();   // fade-out tail keeps ticking below until done
                }
                SkiaSharp.SKBitmap? fxFrame = null;
                if (pauseFx is { Active: true })
                {
                    pauseFx.Resize(ww, wh);
                    if (pauseFx.TickInto(dt / 1000.0)) fxFrame = pauseFx.Frame;
                }
                // On a presenter with a GPU fx layer (Wayland shim), the pause effect rides its OWN
                // capped-res layer the shim stretches full-window — NOT the window-sized software OSD,
                // which would otherwise re-render + re-upload at full window res every frame while paused
                // (the fullscreen slow-motion bug). Upload the capped frame each tick; clear it once on
                // resume. Presenters without the layer (X11/SDL) keep the effect baked into the OSD below.
                bool fxOnGpuLayer = _wlTop.SupportsFxLayer;
                if (fxOnGpuLayer)
                {
                    if (fxFrame != null)
                    {
                        var px = fxFrame.GetPixels();
                        if (px != IntPtr.Zero) { _wlTop.SetFxOverlay(px, fxFrame.Width, fxFrame.Height); _fxLayerShown = true; }
                    }
                    else if (_fxLayerShown)
                    {
                        _wlTop.SetFxOverlay(IntPtr.Zero, 0, 0);
                        _fxLayerShown = false;
                    }
                }

                // A transient disc-swap message ("Disk N / M") preempts the fps line while it's active.
                string shownStatus = ActiveDiskMessage ?? statusText;
                pickerHover = statePicker != null && _wlTop.MouseInside
                    ? GlOsd.PickerHitTest(ww, wh, statePicker.Count, _wlTop.MouseX, _wlTop.MouseY) : -1;
                int statusHover = _wlTop.MouseInside ? GlOsd.StatusHitTest(ww, wh, _wlTop.MouseX, _wlTop.MouseY) : -1;
                // Static deco layers decoded off-thread land here (the GL context lives on this
                // thread): upload once; afterwards the show flags + cog toggles rule.
                if (_pendingBezelRgba is byte[] bezRgba)
                {
                    _pendingBezelRgba = null;
                    _wlTop.SetBezel(bezRgba, _pendingBezelW, _pendingBezelH);
                    _bezelLoaded = true;
                    _wlTop.ShowBezel(_bezelActive);   // the shim fits the game inside the bezel rect
                }
                if (_pendingGovRgba is byte[] govRgba)
                {
                    _pendingGovRgba = null;
                    _wlTop.SetGameOverlay(govRgba, _pendingGovW, _pendingGovH);
                    _wlTop.ShowGameOverlay(_govActive);
                }

                cogHover = cogMenu != null && _wlTop.MouseInside
                    ? GlOsd.MenuHitTest(ww, wh, cogMenu.Count, _wlTop.MouseX, _wlTop.MouseY) : -1;
                var pickerItems = statePicker?.Select(p => (p.Name, p.RelTime)).ToList();
                var cogItems = cogMenu?.Select(m => (m.Label, m.Enabled, m.Value)).ToList();
                var (raToast, raToastAlpha) = RaToastForPresent();
                var (raChallenges, raProgress, raIndVer) = RaIndicatorsForPresent();
                if (osd.Build(ww, wh, shownStatus, title, winStyle, _wlTop.IsMaximized, titleHover, hudAlpha, hover, IsPaused,
                              pickerItems, pickerHover, statusHover,
                              cogItems, cogHover, cogMenu != null ? CoreName : "", fxOnGpuLayer ? null : fxFrame, IsRecording,
                              raToast, raToastAlpha, RaHardcoreActive,
                              raChallenges, raProgress, raIndVer))
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
                        // Display cadence: this present shows a NEW game frame only when the seq
                        // advanced since the last one we presented (the loop re-presents the latest
                        // frame every vsync for the OSD, so most iterations are duplicates).
                        if (_frameSeq != _lastPresentedSeq) { _lastPresentedSeq = _frameSeq; _displayFrameSample++; }
                    }
                }
                if (buf == null) { Thread.Sleep(1); continue; }   // no frame yet — input already pumped above
                _wlTop.Present(buf, pw, ph);

                // Screenshot collect: F12 armed the shim inside this Present's event pump, so the
                // capture (displayed-res, pre-OSD) is ready right after the present that drew it.
                if (_screenshotArmed)
                {
                    _wlTop.GetSize(out int capW, out int capH);
                    var capBuf = new byte[Math.Max(1, capW * capH * 4)];
                    if (_wlTop.TryTakeCapture(capBuf, out int gotW, out int gotH))
                    {
                        _screenshotArmed = false;
                        SaveScreenshotAsync(capBuf, gotW, gotH);
                    }
                }

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
                    Trace.WriteLine($"[GlStats] {statLabel} {_glStatCount}f mean={mean:F2}ms ({1000.0 / mean:F1}fps) stddev={Math.Sqrt(variance):F2}ms min={_glStatMin:F2} max={_glStatMax:F2} workMax={_glStatWorkMax:F1}ms gen2gc={gen2gc} swapEma={_glSwapMsEma:F2}ms");
                    _glStatSum = _glStatSumSq = _glStatMax = _glStatWorkMax = 0; _glStatMin = double.MaxValue; _glStatCount = 0;
                }
            }

            _running = false;   // window closed → stop the emu thread
            // Remember this game's window size for the next launch (parent persists; skipped when
            // maximized so a fullscreen session doesn't bake in a giant floating size).
            if (CheatGameId >= 0 && !_wlTop.IsMaximized)
            {
                _wlTop.GetSize(out int endW, out int endH);
                if (endW > 200 && endH > 150)
                    EmitHostCommand?.Invoke($"save-win-size {CheatGameId} {endW} {endH}");
            }
            _wlTop.PointerButton -= onBtn; _wlTop.MouseMoved -= showHud;
            // Quitting mid-recording: stop + encode rather than losing the capture.
            try { if (_recording is { IsRecording: true }) _recording.Stop(); } catch { }
            try { pauseFx?.Dispose(); } catch { }
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
                case ENV_SET_INPUT_DESCRIPTORS:
                    ParseInputDescriptors(data);
                    return true;
                case ENV_SET_GEOMETRY:
                    // Live geometry change — NDS screen-layout switches resize the output
                    // (256×384 ↔ 512×192 ↔ hybrid) and announce the new shape here. Without
                    // this the present kept letterboxing at the LOAD-time aspect, squishing
                    // the new frame into the old box.
                    if (data != IntPtr.Zero)
                        ApplyGeometry(Marshal.PtrToStructure<retro_game_geometry>(data));
                    return true;
                case ENV_SET_SYSTEM_AV_INFO:
                    // Full AV reset: honor the geometry half (the timing half — fps/sample-rate
                    // changes — is rare mid-game and would need an audio-stream rebuild; log it
                    // so a core that needs it is visible in the logs).
                    if (data != IntPtr.Zero)
                    {
                        var av = Marshal.PtrToStructure<retro_system_av_info>(data);
                        ApplyGeometry(av.geometry);
                        if (av.timing.fps > 0 && Math.Abs(av.timing.fps - _fps) > 0.01)
                            Trace.WriteLine($"[Emu] SET_SYSTEM_AV_INFO timing change requested ({_fps:F2} → {av.timing.fps:F2} fps) — geometry applied, timing kept");
                    }
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
                case ENV_GET_RUMBLE_INTERFACE:
                    // retro_rumble_interface is a single function-pointer field (see _rumbleCb).
                    if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, Marshal.GetFunctionPointerForDelegate(_rumbleCb));
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
                case 36: // RETRO_ENVIRONMENT_SET_MEMORY_MAPS (const retro_memory_map*)
                {
                    // Captured for rcheevos's descriptor-aware memory reads. Stored even before
                    // the RA client exists (it's created async on RaInit) and replayed at init;
                    // cores that publish during the first retro_run trigger the client's
                    // re-validate reload (see RetroAchievementsClient.SetMemoryDescriptors).
                    if (data == IntPtr.Zero) return false;
                    try
                    {
                        IntPtr descs = Marshal.ReadIntPtr(data);
                        int num = Marshal.ReadInt32(data, IntPtr.Size);
                        // retro_memory_descriptor: u64 flags; void* ptr; size_t offset, start,
                        // select, disconnect, len; const char* addrspace  (64 bytes on x86-64)
                        int stride = 8 + IntPtr.Size * 7;
                        var regions = new Services.RetroAchievementsClient.MemoryRegion[Math.Max(0, num)];
                        for (int i = 0; i < num; i++)
                        {
                            IntPtr d = descs + i * stride;
                            ulong flags  = (ulong)Marshal.ReadInt64(d, 0);
                            IntPtr ptr   = Marshal.ReadIntPtr(d, 8);
                            ulong offset = (ulong)Marshal.ReadInt64(d, 8 + IntPtr.Size);
                            ulong start  = (ulong)Marshal.ReadInt64(d, 8 + IntPtr.Size * 2);
                            ulong len    = (ulong)Marshal.ReadInt64(d, 8 + IntPtr.Size * 6);
                            regions[i] = new Services.RetroAchievementsClient.MemoryRegion(flags, ptr, offset, start, len);
                        }
                        _raPendingMemoryRegions = regions;
                        _raClient?.SetMemoryDescriptors(regions);
                        Trace.WriteLine($"[RA] SET_MEMORY_MAPS captured: {num} descriptor(s)");
                    }
                    catch (Exception ex) { Trace.WriteLine($"[RA] SET_MEMORY_MAPS parse failed: {ex.Message}"); }
                    return true;
                }
                case 44: // RETRO_ENVIRONMENT_SET_SERIALIZATION_QUIRKS (uint64* in/out)
                {
                    // Track SINGLE_SESSION (bit 4 — Kronos/Saturn: states invalid across launches; the
                    // load path refuses with a message instead of shipping a frozen game). OR-in
                    // FRONT_VARIABLE_SIZE (bit 3) so cores know we tolerate variable-size states —
                    // exactly upstream's handling.
                    if (data == IntPtr.Zero) return false;
                    ulong quirks = (ulong)Marshal.ReadInt64(data);
                    _coreSingleSessionStates = (quirks & (1UL << 4)) != 0;
                    Marshal.WriteInt64(data, (long)(quirks | (1UL << 3)));
                    if (_coreSingleSessionStates)
                        Trace.WriteLine("[State] core declares SINGLE_SESSION serialization quirk");
                    return true;
                }
                case 72:  // RETRO_ENVIRONMENT_GET_SAVESTATE_CONTEXT (72|EXPERIMENTAL, masked) — int*
                case 213: // upstream also answered 213 (FBNeo-private id with the same meaning)
                    // RETRO_SAVESTATE_CONTEXT_NORMAL (0) = standard save states; FBNeo gates
                    // save-state/hiscore support on this.
                    if (data != IntPtr.Zero) Marshal.WriteInt32(data, 0);
                    return true;
                case 51:  // RETRO_ENVIRONMENT_GET_INPUT_BITMASKS — we honor the
                          // RETRO_DEVICE_ID_JOYPAD_MASK read in SdlInput.GetInputState.
                          // Cores that gate on this (LRPS2/PS2) then poll the whole pad
                          // in one call; without it their input never reaches the core.
                    return true;
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
            // GLEW-based cores (PPSSPP) can't init on our surfaceless EGL contexts at all:
            // glewInit()'s X11 build requires a GLX display, and it also rejects core
            // profiles. Give them a legacy GLX compat context (via XWayland) instead —
            // versionless type-1 so the driver returns its highest compatibility GL.
            int ctxType = _hwCtxType, ctxMajor = _hwMajor, ctxMinor = _hwMinor;
            bool useGlx = _handler.ForceCompatibilityGlProfile;
            if (useGlx && ctxType == 3)
            {
                Trace.WriteLine("[Emu] HW-render: downgrading core-profile request to GLX compatibility (handler override)");
                ctxType = 1; ctxMajor = 0; ctxMinor = 0;
            }
            if (!Platform.HwGlContext.Init(ctxType, ctxMajor, ctxMinor, _hwDepth, _hwStencil, maxW, maxH, useGlx))
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

        // Recompute the display aspect from a mid-session geometry announcement and push it to the
        // live presenter (the shim re-letterboxes from the next present; the aligned double write
        // is safe cross-thread). Mirrors the load-time computation.
        private void ApplyGeometry(retro_game_geometry geo)
        {
            double old = DisplayAspectRatio;
            DisplayAspectRatio = _handler.GetDisplayAspectRatio(geo.base_width, geo.base_height, geo.aspect_ratio);
            if (Math.Abs(DisplayAspectRatio - old) > 0.001)
            {
                Trace.WriteLine($"[Emu] geometry change: {geo.base_width}x{geo.base_height} ar={geo.aspect_ratio:F3} → DAR {DisplayAspectRatio:F3}");
                _wlTop?.SetAspect(DisplayAspectRatio);
            }
        }

        // ── Turbo / autofire ─────────────────────────────────────────────────────────────────────────
        // SET_INPUT_DESCRIPTORS: per-port JOYPAD id → human label (upstream's ParseInputDescriptors).
        // Cores re-send wholesale on device-type changes (e.g. Saturn 3D pad → digital shrinks the
        // set), so clear before re-populating.
        private void ParseInputDescriptors(IntPtr data)
        {
            if (data == IntPtr.Zero) return;
            try
            {
                for (int i = 0; i < _joypadDescriptors.Length; i++)
                    _joypadDescriptors[i].Clear();
                // struct retro_input_descriptor { uint port; uint device; uint index;
                //                                 uint id; const char *description; }
                // Terminated by an entry whose description pointer is NULL.
                int stride = (4 * 4) + IntPtr.Size;
                IntPtr p = data;
                int safety = 0;
                while (safety++ < 4096)
                {
                    IntPtr descPtr = Marshal.ReadIntPtr(p, 16);
                    if (descPtr == IntPtr.Zero) break;
                    uint port = (uint)Marshal.ReadInt32(p, 0);
                    uint device = (uint)Marshal.ReadInt32(p, 4);
                    // index at +8 — not used for joypad digital buttons
                    uint id = (uint)Marshal.ReadInt32(p, 12);
                    if (port < 4 && device == SdlInput.RETRO_DEVICE_JOYPAD && id < 16)
                    {
                        string label = Marshal.PtrToStringAnsi(descPtr) ?? "";
                        if (!string.IsNullOrWhiteSpace(label))
                            _joypadDescriptors[port][id] = label;
                    }
                    p = IntPtr.Add(p, stride);
                }
                _descriptorsReceived = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Emu] ParseInputDescriptors: " + ex.Message);
            }
        }

        // ── Screenshots (upstream's TakeScreenshot + ScreenshotService.Save) ─────────────────────────
        private int? _screenshotScancode;

        // The config stores an Avalonia key NAME (captured in Preferences → Media); the host sees
        // SDL scancodes, so resolve function keys by name and fall back to upstream's F12 default
        // for anything we can't map (the capture box is effectively always an F-key).
        private static int ResolveScreenshotScancode()
        {
            string k = App.Configuration?.GetUserPreferences()?.ScreenshotKey ?? "";
            if ((k.Length == 2 || k.Length == 3) && (k[0] == 'F' || k[0] == 'f')
                && int.TryParse(k.AsSpan(1), out int fn) && fn >= 1 && fn <= 12)
                return 58 + fn - 1;   // SDL scancodes F1..F12 = 58..69
            return SC_F12;
        }

        private volatile bool _screenshotArmed;   // wl path: collect the shim capture after the present

        /// <summary>F12 / PrintScreen / configured key: save the DISPLAYED frame into the Screenshots
        /// library. On the wl path the shim reads the rendered game/bezel rect back at displayed
        /// resolution, pre-OSD (upstream captures the rendered window the same way — its native-res
        /// path is "backstop only"). Elsewhere falls back to the native-res published frame.</summary>
        private void TakeScreenshot()
        {
            if (_wlTop is { HasCapture: true })
            {
                // OnGlKey fires inside PumpEvents, which runs BEFORE the native present in the same
                // Present() call — so arming here captures THIS frame; the loop collects it after.
                _wlTop.RequestCapture();
                _screenshotArmed = true;
                return;
            }
            // Fallback (non-wl presents): the published post-rotation/flip BGRA at native res.
            byte[]? shot = null; int sw = 0, sh = 0;
            lock (_frameLock)
            {
                if (_frame != null && _frameW > 0 && _frameH > 0)
                {
                    sw = _frameW; sh = _frameH;
                    shot = new byte[sw * sh * 4];
                    Array.Copy(_frame, shot, shot.Length);
                }
            }
            if (shot == null) { ShowDiskMessage("Screenshot not available", 3); return; }
            SaveScreenshotAsync(shot, sw, sh);
        }

        // Encode + save off-thread using upstream's filename convention
        // ("{yyyyMMdd_HHmmss} {title} ({console}).png" under Screenshots/{console}/),
        // which the Screenshots tab's parser picks up.
        private void SaveScreenshotAsync(byte[] bgra, int w, int h)
        {
            string title = string.IsNullOrWhiteSpace(SaveGameTitle)
                ? Path.GetFileNameWithoutExtension(_romPath) : SaveGameTitle;
            string console = HandlerConsoleName;
            _ = Task.Run(() =>
            {
                try
                {
                    string safeTitle = FileNameHelper.SanitizeFileName(title);
                    string safeConsole = FileNameHelper.SanitizeFileName(console);
                    string fileName = $"{DateTime.Now:yyyyMMdd_HHmmss} {safeTitle} ({safeConsole}).png";
                    string path = Path.Combine(AppPaths.GetFolder("Screenshots", safeConsole), fileName);
                    Services.PngEncoder.WriteBgra(path, bgra, w, h);
                    ShowDiskMessage("Screenshot saved", 3);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Screenshot] save failed: {ex.Message}");
                    ShowDiskMessage("Screenshot failed", 3);
                }
            });
        }

        // ── Bezel + Vectrex overlay init (called on the present thread; heavy work goes to a task) ──
        private void InitDecorations()
        {
            string console = HandlerConsoleName;
            // Bezels: arcade family only, feature-gated (Preferences → Extras). The cog row shows
            // whenever the feature applies; art is fetched on demand (auto when enabled for this
            // game, else on first toggle). Mirrors upstream's InitBezelOverlay + LoadBezelAsync.
            if (Services.BezelService.AppliesTo(console) && Services.BezelService.FeatureEnabled)
            {
                _bezelRowVisible = true;
                if (CheatGameId >= 0 && Services.BezelService.IsEnabledForGame(CheatGameId))
                {
                    _bezelActive = true; _bezelFetching = true;
                    string rom = _romPath;
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        string? png = await Services.BezelService.EnsureBezelAsync(rom, console).ConfigureAwait(false);
                        if (png == null || !QueueDecoPng(png, bezel: true)) _bezelActive = false;
                        _bezelFetching = false;
                    });
                }
            }
            // Vectrex overlays: folder-driven (Overlays/Vectrex), fuzzy filename match. Decoded up
            // front even when disabled so the cog toggle is instant; default-enabled per upstream.
            if (console == "Vectrex")
            {
                string? ov = Services.VectrexOverlayService.FindOverlay(_romPath);
                if (ov != null)
                {
                    _govRowVisible = true;
                    _govActive = CheatGameId < 0 || Services.VectrexOverlayService.IsOverlayEnabled(CheatGameId);
                    _ = System.Threading.Tasks.Task.Run(() => QueueDecoPng(ov, bezel: false));
                }
            }
        }

        // Decode a PNG to straight-alpha RGBA8 and queue it for the present thread to upload.
        private bool QueueDecoPng(string path, bool bezel)
        {
            try
            {
                using var bmp = SkiaSharp.SKBitmap.Decode(path);
                if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return false;
                var info = new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height,
                    SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
                var rgba = new byte[info.BytesSize];
                var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                bool ok;
                try
                {
                    using var pix = bmp.PeekPixels();
                    ok = pix != null && pix.ReadPixels(info, pin.AddrOfPinnedObject(), info.RowBytes);
                }
                finally { pin.Free(); }
                if (!ok) return false;
                if (bezel)
                {
                    _pendingBezelW = bmp.Width; _pendingBezelH = bmp.Height;
                    _pendingBezelRgba = rgba;   // volatile publish LAST (dims travel with it)
                }
                else
                {
                    _pendingGovW = bmp.Width; _pendingGovH = bmp.Height;
                    _pendingGovRgba = rgba;
                }
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Emu] deco decode failed ({path}): {ex.Message}");
                return false;
            }
        }

        /// <summary>"--turbo" launch payload: the four per-port id CSVs joined with ';' (e.g. "0,8;;;").
        /// Call before Start; the saved per-game sets come from the parent (the host reads no config).</summary>
        public void SetTurboConfig(string spec)
        {
            string[] ports = spec.Split(';');
            for (int p = 0; p < _turboButtons.Length && p < ports.Length; p++)
            {
                var set = new HashSet<uint>();
                foreach (var part in ports[p].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (uint.TryParse(part.Trim(), out uint id) && id < 16 && !TurboBlacklist.Contains(id))
                        set.Add(id);
                _turboButtons[p] = set;
            }
        }

        // Toggle one button's turbo membership by swapping a fresh set into the slot (atomic ref
        // assignment — the emu thread snapshots the reference per read). Returns the new CSV for
        // the save-turbo host command.
        private string ToggleTurbo(int port, uint id)
        {
            var next = new HashSet<uint>(_turboButtons[port]);
            if (!next.Remove(id)) next.Add(id);
            _turboButtons[port] = next;
            return string.Join(",", next);
        }

        // ── In-game disc switching (L3 + Start chord) ───────────────────────────────────────────────
        // Wraps SdlInput.GetInputState so we can inject a JOYPAD_L press on port 0 for FDS "disk side
        // change" (FDS cores don't expose the disk-control interface — they read an L press instead).
        private int InputState_cb(uint port, uint device, uint index, uint id)
        {
            // Joypad bitmask read (LRPS2/PS2 and other GET_INPUT_BITMASKS cores poll
            // the whole pad in one call): fold the 16 per-button results — each of
            // which still passes through the FDS disk-inject + turbo gating below —
            // into one value. Without this the core reads 0 for every button.
            if (device == SdlInput.RETRO_DEVICE_JOYPAD && id == SdlInput.RETRO_DEVICE_ID_JOYPAD_MASK)
            {
                int mask = 0;
                for (uint b = 0; b < 16; b++)
                    if (InputState_cb(port, device, index, b) != 0)
                        mask |= 1 << (int)b;
                return mask;
            }

            if (port == 0 && device == SdlInput.RETRO_DEVICE_JOYPAD
                && id == LibretroInput.JOYPAD_L && _fdsSideChangeFrames > 0)
                return 1;
            int v = _input.GetInputState(port, device, index, id);
            // Turbo gate (upstream's TurboGate): a held turbo button reports pressed only during the
            // duty window of each period, auto-releasing for the rest (~10Hz at 60fps). Snapshot the
            // set reference once — toggles swap in a fresh set, so this read is tear-free.
            if (v != 0 && device == SdlInput.RETRO_DEVICE_JOYPAD && port < 4 && id < 16)
            {
                var set = _turboButtons[port];
                if (set.Count != 0 && set.Contains(id) && (_turboFrames % TurboPeriodFrames) >= TurboDutyFrames)
                    return 0;
            }
            return v;
        }

        // ── Disk Swap chord (upstream EmulatorWindow): consoles whose cores expose the libretro
        //    disk control interface. Excluded by upstream after testing: TurboGrafx16/PCECD/TG16
        //    (Beetle PCE/PCE Fast lack registration), 3DO (Opera has no disk-control code). ─────
        private static readonly HashSet<string> DiskCapableConsoles =
            new(StringComparer.OrdinalIgnoreCase) { "FDS", "PS1", "Saturn", "SegaCD", "Amiga" };
        public static bool ConsoleSupportsDiskSwap(string console)
            => !string.IsNullOrEmpty(console) && DiskCapableConsoles.Contains(console);

        // User-configured chord halves (Preferences → Controls → FRONTEND → Disk Swap).
        // Controller: panel raw-id space; -1 = unbound → fall back to the L3+Start default.
        // Keyboard: SDL scancodes resolved from the stored Avalonia Key names ("KeyA+KeyB").
        private int _diskSwapCtrlA = -1, _diskSwapCtrlB = -1;
        private int _diskSwapKeySCa = -1, _diskSwapKeySCb = -1;
        private volatile bool _diskSwapKeyAHeld, _diskSwapKeyBHeld;

        // Parse the P1 "Disk Swap" chord from config (upstream loads it alongside the keyboard
        // mappings; same "A+B" identifier format both sides). Called from Start() after
        // LoadConfiguration so prefs edits apply at next launch like every other binding.
        private void LoadDiskSwapChord()
        {
            _diskSwapCtrlA = _diskSwapCtrlB = -1;
            _diskSwapKeySCa = _diskSwapKeySCb = -1;
            _diskSwapKeyAHeld = _diskSwapKeyBHeld = false;
            var cfg = App.Configuration;
            if (cfg == null) return;
            var p1 = cfg.GetInputConfiguration($"{_console}_P1");
            if (p1.ControllerMappings.Count == 0 && p1.KeyboardMappings.Count == 0)
                p1 = cfg.GetInputConfiguration(_console);   // legacy single-player saves

            foreach (var m in p1.ControllerMappings)
                if (string.Equals(m.ButtonName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = (m.InputIdentifier ?? "").Split('+', 2);
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int a)
                                          && int.TryParse(parts[1].Trim(), out int b))
                    { _diskSwapCtrlA = a; _diskSwapCtrlB = b; }
                    break;
                }
            foreach (var m in p1.KeyboardMappings)
                if (string.Equals(m.ButtonName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = (m.InputIdentifier ?? "").Split('+', 2);
                    if (parts.Length == 2)
                    {
                        _diskSwapKeySCa = KeyNameToScancode(parts[0].Trim());
                        _diskSwapKeySCb = KeyNameToScancode(parts[1].Trim());
                        if (_diskSwapKeySCa < 0 || _diskSwapKeySCb < 0)
                            _diskSwapKeySCa = _diskSwapKeySCb = -1;
                    }
                    break;
                }
            if (_diskSwapCtrlA >= 0 || _diskSwapKeySCa >= 0)
                Trace.WriteLine($"[Emu] disk swap chord: ctrl {_diskSwapCtrlA}+{_diskSwapCtrlB}, key sc {_diskSwapKeySCa}+{_diskSwapKeySCb}");
        }

        // Avalonia Key enum name → SDL scancode for the chord-capturable set. -1 = unmappable
        // (the chord is then ignored rather than half-matched).
        private static int KeyNameToScancode(string name)
        {
            if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z') return 4 + (name[0] - 'A');
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
                return name[1] == '0' ? 39 : 30 + (name[1] - '1');
            if (name.Length is 2 or 3 && name[0] == 'F'
                && int.TryParse(name.AsSpan(1), out int fn) && fn >= 1 && fn <= 12) return 58 + fn - 1;
            return name switch
            {
                "Up" => 82, "Down" => 81, "Left" => 80, "Right" => 79,
                "Space" => 44, "Enter" or "Return" => 40, "Back" => 42, "Tab" => 43,
                "LeftShift" => 225, "RightShift" => 229, "LeftCtrl" => 224, "RightCtrl" => 228,
                "LeftAlt" => 226, "RightAlt" => 230,
                _ => -1,
            };
        }

        // Called once per emu frame (after input poll). Detects the disc-swap chord (rising edge) and
        // ticks the FDS-injection + deferred-reinsert countdowns.
        private void ServiceDiskSwap()
        {
            _turboFrames++;   // turbo duty-cycle clock: once per emu frame, both loop variants
            if (_fdsSideChangeFrames > 0) _fdsSideChangeFrames--;
            if (_diskInsertPendingFrames > 0 && --_diskInsertPendingFrames == 0)
            {
                try { _setEjectState?.Invoke(false); } catch (Exception ex) { Trace.WriteLine($"[Emu] disk deferred insert failed: {ex.Message}"); }
            }

            // Chord on controller 0, read raw so it works regardless of the per-console mapping
            // (NES/FDS etc. don't map L3). User-configured chord (Preferences → Controls →
            // FRONTEND) wins; L3 + Start is the unbound default. A configured keyboard chord
            // (held flags fed by OnGlKey) also triggers. Rising edge so a held chord fires once.
            bool held = _diskSwapCtrlA >= 0 && _diskSwapCtrlB >= 0
                ? _input.IsRawControlDown(_diskSwapCtrlA) && _input.IsRawControlDown(_diskSwapCtrlB)
                : _input.IsRawButtonDown(SdlInput.SdlButtonLeftStick)
                  && _input.IsRawButtonDown(SdlInput.SdlButtonStart);
            held |= _diskSwapKeySCa >= 0 && _diskSwapKeyAHeld && _diskSwapKeyBHeld;
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

        // Surface a transient message in the OSD status line (read by the present loop). Shared by
        // disc-swap feedback and save-state feedback — same single-line OSD slot upstream uses.
        private void ShowDiskMessage(string msg, int seconds = 3)
        {
            _diskMsg = msg;
            _diskMsgUntil = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        }

        /// <summary>The active disc-swap OSD message, or null if none is currently showing.</summary>
        public string? ActiveDiskMessage => (_diskMsg.Length > 0 && Stopwatch.GetTimestamp() < _diskMsgUntil) ? _diskMsg : null;

        // ── Recording (ported from upstream's ffmpeg path; Linux has no WGC — every core's frames
        //    already land here as packed BGRA, so the ffmpeg path covers all of them) ────────────────
        private Services.RecordingService? _recording;
        public bool IsRecording => _recording?.IsRecording == true;

        /// <summary>F9 / HUD record button / cog "Record": start or stop the capture.</summary>
        public void ToggleRecording()
        {
            if (_recording is { IsRecording: true })
            {
                var elapsed = _recording.Elapsed;
                _recording.Stop();   // encode continues in the background; onComplete shows the result
                ShowDiskMessage($"Recording stopped ({elapsed:mm\\:ss}) — encoding…", 4);
                return;
            }
            int w, h;
            lock (_frameLock) { w = _frameW; h = _frameH; }
            string safeTitle = FileNameHelper.SanitizeFileName(SaveGameTitle.Length > 0 ? SaveGameTitle : "game");
            string outDir = Path.Combine(AppPaths.GetFolder("Recordings", FileNameHelper.SanitizeFileName(_handler.ConsoleName)), safeTitle);
            string outputPath = Path.Combine(outDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");

            var cfg = App.Configuration?.GetRecordingConfiguration();
            var settings = new Services.RecordingEncodeSettings
            {
                Quality = cfg?.Quality ?? "High",
                OutputScale = cfg?.OutputScale ?? 2,
                Encoder = cfg?.Encoder ?? "Auto",
                HighChroma = cfg?.HighChroma ?? false,
                AudioBitrateKbps = cfg?.AudioBitrateKbps ?? 192,
                // CD-i's half-height interlaced framebuffer breaks uniform scaling (upstream's only
                // aspect special case — every other console records at the buffer's pixel aspect).
                DisplayAspectRatio = _handler.ConsoleName == "CDi" ? _core?.AvInfo.geometry.aspect_ratio ?? 0f : 0f,
            };
            var rec = new Services.RecordingService();
            string? err = rec.Start(outputPath, w, h, TargetFps, (int)Math.Round(_sampleRate),
                error => ShowDiskMessage(error ?? "Recording saved to Recordings", error == null ? 4 : 6),
                settings);
            if (err != null) { ShowDiskMessage(err, 5); return; }
            _recording = rec;
            ShowDiskMessage("Recording started — press F9 to stop", 4);
        }

        /// <summary>Cog "View Recordings": open this game's recordings folder in the file manager.</summary>
        public void OpenRecordingsFolder()
        {
            try
            {
                string safeTitle = FileNameHelper.SanitizeFileName(SaveGameTitle.Length > 0 ? SaveGameTitle : "game");
                string dir = Path.Combine(AppPaths.GetFolder("Recordings", FileNameHelper.SanitizeFileName(_handler.ConsoleName)), safeTitle);
                Directory.CreateDirectory(dir);
                Services.ShellOpen.Open(dir);
            }
            catch (Exception ex) { ShowDiskMessage($"Could not open folder: {ex.Message}", 4); }
        }

        // ── Cheats (ported from upstream EmulatorWindow's cheat engine) ─────────────────────────────
        // Per-game cheats load at boot and apply ON THE EMU THREAD between retro_run calls (same
        // pending-flag pattern as save states). CheatService.Sort splits codes: core-format codes
        // (Game Genie / GameShark / raw) go to retro_cheat_set; Action-Replay "addr:value" codes
        // are written by US into system RAM every frame (many cores stub retro_cheat_set for AR).
        // Hardcore-mode cheat blocking lives in ApplyAllCheats — the single chokepoint every
        // entry path (toggle, import, live re-apply, session start) funnels through.

        /// <summary>Library row id for this game — names Cheats/{Console}/{id}.json (host has no DB).</summary>
        public int CheatGameId { get; set; } = -1;

        private List<Models.Cheat> _cheats = new();
        private volatile bool _cheatsApplyPending;
        private List<Models.Cheat>? _cheatsApplyPayload;
        private readonly object _cheatsApplyLock = new();
        private volatile Services.CheatService.ParsedAr[] _frontendArCheats = Array.Empty<Services.CheatService.ParsedAr>();
        private IntPtr _systemRamPtr = IntPtr.Zero;
        private uint _systemRamSize;

        // CheatService/CheatDatabaseService key off Game.Id/Console/RomPath only — a stub carries
        // the host's identity without needing the DB-backed object.
        private Models.Game CheatGameStub() => new()
        {
            Id = CheatGameId,
            Console = _handler.ConsoleName,
            Title = SaveGameTitle,
            RomPath = _romPath,
        };

        /// <summary>Load this game's cheats from disk and queue an apply (boot + reload-cheats).</summary>
        public void ReloadCheats()
        {
            if (CheatGameId < 0) return;
            var loaded = Services.CheatService.Load(CheatGameStub());
            lock (_cheatsApplyLock)
            {
                _cheats = loaded;
                _cheatsApplyPayload = new List<Models.Cheat>(loaded);
                _cheatsApplyPending = true;
            }
            Trace.WriteLine($"[Cheats] loaded {loaded.Count} ({loaded.Count(c => c.Enabled)} enabled) for game {CheatGameId}");
        }

        // ── Live input reload (Windows parity): a Controls-panel edit applies to the RUNNING game,
        // not just the next launch. On Windows the emulator shares one process with the panel; on Linux
        // the game runs in a separate host that loads input once at launch, so the parent saves the
        // config then sends "reload-input" down our stdin and we rebind here. ──
        private volatile bool _inputReloadPending;
        /// <summary>Queue a live rebind of the input map from the (just-saved) config. Deferred to the
        /// emu thread, where the input is polled — same contract as ReloadCheats.</summary>
        public void ReloadInputConfig() => _inputReloadPending = true;

        private void ExecuteInputReloadOnEmuThread()
        {
            _inputReloadPending = false;
            try
            {
                // Our in-memory config was loaded at launch and is now stale; re-read the file the parent
                // just wrote, then rebind the live input map + the disk-swap chord (both come from it).
                App.Configuration?.LoadAsync().GetAwaiter().GetResult();
                _input.LoadConfiguration(_console, App.Configuration);
                LoadDiskSwapChord();
                Trace.WriteLine("[Emu] input config reloaded live (Controls edit applied to the running game)");
            }
            catch (Exception ex) { Trace.WriteLine($"[Emu] live input reload failed: {ex}"); }
        }

        /// <summary>Current list snapshot for the cog panel.</summary>
        public List<Models.Cheat> CheatsSnapshot() { lock (_cheatsApplyLock) return new List<Models.Cheat>(_cheats); }

        /// <summary>Toggle one cheat (cog panel row), persist, and queue a live re-apply.</summary>
        public void ToggleCheat(int index)
        {
            lock (_cheatsApplyLock)
            {
                if (index < 0 || index >= _cheats.Count) return;
                _cheats[index].Enabled = !_cheats[index].Enabled;
                Services.CheatService.Save(CheatGameStub(), _cheats);
                _cheatsApplyPayload = new List<Models.Cheat>(_cheats);
                _cheatsApplyPending = true;
            }
        }

        /// <summary>Cog "Import from Database…": merge the cheat DB's entries (dedupe by code),
        /// persist, queue apply. Returns how many were added, or -1 when no .cht matched.</summary>
        public int ImportCheatsFromDatabase()
        {
            if (CheatGameId < 0) return -1;
            var result = Services.CheatDatabaseService.LookupForGame(CheatGameStub());
            if (result == null || result.Cheats.Count == 0) return -1;
            int added = 0;
            lock (_cheatsApplyLock)
            {
                var seen = new HashSet<string>(_cheats.Select(c => c.Code.Trim()), StringComparer.OrdinalIgnoreCase);
                foreach (var c in result.Cheats)
                    if (seen.Add(c.Code.Trim())) { _cheats.Add(c); added++; }
                if (added > 0)
                {
                    Services.CheatService.Save(CheatGameStub(), _cheats);
                    _cheatsApplyPayload = new List<Models.Cheat>(_cheats);
                    _cheatsApplyPending = true;
                }
            }
            return added;
        }

        /// <summary>Emu thread, between retro_run calls: apply the queued cheat list.</summary>
        private void ExecuteCheatsApplyOnEmuThread()
        {
            List<Models.Cheat>? payload;
            lock (_cheatsApplyLock) { payload = _cheatsApplyPayload; _cheatsApplyPayload = null; _cheatsApplyPending = false; }
            if (payload == null) return;
            ApplyAllCheats(payload);
        }

        private void ApplyAllCheats(IList<Models.Cheat> cheats)
        {
            if (_core == null) return;
            // RA hardcore-compliance: cheats are an auto-fail blocker. Single chokepoint —
            // every entry path (toggle, import, live re-apply, session start) funnels here.
            if (RaHardcoreActive)
            {
                ShowDiskMessage("Cheats are disabled in hardcore mode", 4);
                return;
            }
            var (coreHandled, frontendAr) = Services.CheatService.Sort(cheats, _handler.ConsoleName);
            _frontendArCheats = frontendAr.ToArray();
            if (_systemRamPtr == IntPtr.Zero && _frontendArCheats.Length > 0)
            {
                (_systemRamPtr, _systemRamSize) = _core.GetMemoryRegion(2); // RETRO_MEMORY_SYSTEM_RAM
                if (_systemRamPtr == IntPtr.Zero)
                    Trace.WriteLine("[Cheats] core exposes no system RAM — AR codes will be inert");
            }
            Services.CheatService.Apply(_core, coreHandled);
            Trace.WriteLine($"[Cheats] applied: core={coreHandled.Count(c => c.Enabled)} frontendAR={_frontendArCheats.Length}");
        }

        // Every frame after retro_run: hold AR-cheat RAM values. Mask keeps the offset inside the
        // region for non-power-of-2 bases; 2-byte writes are big-endian with wrap (upstream parity).
        private unsafe void ApplyFrontendArToRam()
        {
            var ar = _frontendArCheats;
            if (ar.Length == 0 || _systemRamPtr == IntPtr.Zero || _systemRamSize == 0) return;
            byte* ram = (byte*)_systemRamPtr;
            for (int i = 0; i < ar.Length; i++)
            {
                uint offset = ar[i].Address & (_systemRamSize - 1);
                if (ar[i].ByteCount == 1)
                    ram[offset] = (byte)ar[i].Value;
                else
                {
                    ram[offset] = (byte)(ar[i].Value >> 8);
                    ram[(offset + 1) & (_systemRamSize - 1)] = (byte)ar[i].Value;
                }
            }
        }

        // ── Cog menu support (ported from upstream's OverlayMenu) ───────────────────────────────────
        /// <summary>Host→main-app command channel (set by GameHost: writes "EMUTASTIC-CMD …" to stdout,
        /// which GameHostLauncher reads on the parent side). Null when running in-process.</summary>
        public static Action<string>? EmitHostCommand;
        /// <summary>Per-console visual options (cog → Visuals), from the console handler.</summary>
        public List<(string key, string label)> VisualOptions => _handler.GetVisualOptions();
        public string HandlerConsoleName => _handler.ConsoleName;

        /// <summary>Current value of a core option (for cog-menu value labels).</summary>
        public string CoreOptionValue(string key) => _coreOptions.TryGetValue(key, out var v) ? v : "";

        /// <summary>
        /// Cycle a core option to its next announced valid value and mark the variables dirty —
        /// the core re-reads them via GET_VARIABLE_UPDATE on its next frame, so visual options
        /// (internal resolution, texture filter, …) apply LIVE, like upstream's Visuals panel.
        /// Returns the new value, or null when the option wasn't announced this session.
        /// </summary>
        public string? CycleCoreOption(string key)
        {
            var entry = _coreOptionSchema.FirstOrDefault(e => e.Key == key);
            if (entry == null || entry.ValidValues.Length == 0) return null;
            string cur = _coreOptions.TryGetValue(key, out var c) ? c : entry.DefaultValue;
            int idx = Array.IndexOf(entry.ValidValues, cur);
            string next = entry.ValidValues[(idx + 1) % entry.ValidValues.Length];
            _coreOptions[key] = next;
            _coreOptionsDirty = true;
            // Persist the change to the per-core values store (upstream parity, EmulatorWindow:8235).
            // Without this the cog change was in-memory only: live options didn't survive a restart,
            // and "⚠ restart" options (N64 resolution, parallel upscaling) could NEVER take effect —
            // they revert to the default before the restart that's supposed to apply them. SaveValues
            // merges, so this updates just this key.
            try { _coreOptionsStore.SaveValues(_coreName, new Dictionary<string, string> { [key] = next }); }
            catch (Exception ex) { Trace.WriteLine($"[Emu] core option persist failed ({key}): {ex.Message}"); }
            Trace.WriteLine($"[Emu] core option (live, cog menu) {key} = {next}");
            return next;
        }

        // ── Save states (ported from upstream EmulatorWindow.xaml.cs) ───────────────────────────────
        // Save/load run ON THE EMU THREAD between retro_run calls via volatile pending flags; the
        // host window (hotkeys F5/F7, HUD Save/Load buttons) and GameHost startup (--load-state)
        // only set the flags. The host process has no DB — saves write .state/.png/.json into
        // SaveStateDir and the main app ingests rows on host exit (ImportService.DiscoverSaveStates).

        // Context handed in by GameHost (no DB in this process).
        public string SaveStateDir { get; set; } = "";
        public string SaveGameTitle { get; set; } = "";
        public string SaveRomHash { get; set; } = "";
        /// <summary>ROM-hack patch (IPS/BPS/UPS) applied in memory at load; also keys the
        /// hack's own .srm (stem + first 8 hash chars) so it never shares the base game's.</summary>
        public string? PatchPath { get; set; }

        private volatile bool _saveStatePending;
        private volatile bool _loadStatePending;
        private string _pendingSaveName = "";
        private byte[]? _pendingLoadData;
        private byte[]? _pendingLoadCheevosBlob;
        private string _pendingLoadName = "";
        private string _pendingLoadSavedCoreName = "";
        private int _loadStateAttempts;
        private const int MaxLoadStateAttempts = 600;   // ~10s @60fps — PSX serialize_size settles during BIOS boot
        private int _loadStateWarmup;
        private const int LoadStateWarmupFrames = 60;   // HW renderers (Beetle PSX HW) need the pipeline warm first
        private bool _coreSingleSessionStates;          // env 44 RETRO_SERIALIZATION_QUIRK_SINGLE_SESSION

        private bool IsSaveStateUnreliable()
        {
            string p = (_core?.CorePath ?? "").ToLowerInvariant();
            return p.Contains("mame2003_plus");
        }

        /// <summary>Request a named save (any thread). Emu thread picks it up before the next retro_run.</summary>
        public void RequestSaveState(string name)
        {
            if (IsSaveStateUnreliable())
            {
                ShowDiskMessage("Save states are disabled for MAME 2003-Plus (unreliable per-game)", 5);
                return;
            }
            if (string.IsNullOrEmpty(SaveStateDir))
            {
                ShowDiskMessage("Save states unavailable (no save directory for this session)", 5);
                return;
            }
            _pendingSaveName = name;
            _saveStatePending = true;
        }

        /// <summary>Request a load from a .state file path (any thread).</summary>
        public void RequestLoadState(string statePath, string name)
        {
            // RA hardcore-compliance: loading save states is an auto-fail blocker
            // (docs.retroachievements.org hardcore requirements). Saving stays allowed —
            // only loading is blocked. Single gate: every load path funnels through here.
            if (RaHardcoreActive)
            {
                ShowDiskMessage("Save state loading is disabled in hardcore mode", 4);
                return;
            }
            if (IsSaveStateUnreliable())
            {
                ShowDiskMessage("Save states are disabled for MAME 2003-Plus (unreliable per-game)", 5);
                return;
            }
            try
            {
                _pendingLoadData = File.ReadAllBytes(statePath);
                // Pair the rcheevos progress side-car when one exists. Older states
                // predate it and load with a null blob (DeserializeProgress no-ops).
                _pendingLoadCheevosBlob = null;
                try
                {
                    string cheevosPath = Path.ChangeExtension(statePath, ".cheevos");
                    if (File.Exists(cheevosPath))
                        _pendingLoadCheevosBlob = File.ReadAllBytes(cheevosPath);
                }
                catch (Exception ex) { Trace.WriteLine($"[RA] Cheevos side-car read failed: {ex.Message}"); }
                _pendingLoadName = name;
                _pendingLoadSavedCoreName = ReadSavedCoreName(statePath);
                _loadStatePending = true;
            }
            catch (Exception ex)
            {
                ShowDiskMessage($"Could not read state file: {ex.Message}", 5);
            }
        }

        /// <summary>F7 / quick load: resolve "Quick Save" by file convention (host has no DB).</summary>
        public void RequestQuickLoad()
        {
            string p = Path.Combine(SaveStateDir, "Quick Save.state");
            if (string.IsNullOrEmpty(SaveStateDir) || !File.Exists(p))
            {
                ShowDiskMessage("No Quick Save found", 3);
                return;
            }
            RequestLoadState(p, "Quick Save");
        }

        /// <summary>--load-state startup path (mirrors upstream's pendingLoadStatePath ctor param).</summary>
        public void QueuePendingLoad(string statePath)
        {
            if (!File.Exists(statePath))
            {
                ShowDiskMessage($"Save state not found: {Path.GetFileName(statePath)}", 5);
                Trace.WriteLine($"[State] pending load missing on disk: {statePath}");
                return;
            }
            RequestLoadState(statePath, Path.GetFileNameWithoutExtension(statePath));
            Trace.WriteLine($"[State] queued pending state load: {statePath} (saved core='{_pendingLoadSavedCoreName}')");
        }

        // The .json sidecar carries the creating core's name for the cross-core load guard.
        private static string ReadSavedCoreName(string statePath)
        {
            try
            {
                string json = Path.ChangeExtension(statePath, ".json");
                if (!File.Exists(json)) return "";
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("CoreName", out var cn))
                    return cn.GetString() ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>The 5 most recent states in SaveStateDir (name, relative age, path) for the HUD load picker.</summary>
        public List<(string Name, string RelTime, string Path)> RecentSaveStates()
        {
            var result = new List<(string, string, string)>();
            try
            {
                if (string.IsNullOrEmpty(SaveStateDir) || !Directory.Exists(SaveStateDir)) return result;
                var entries = new List<(string name, DateTime at, string path)>();
                foreach (string json in Directory.EnumerateFiles(SaveStateDir, "*.json"))
                {
                    string state = Path.ChangeExtension(json, ".state");
                    if (!File.Exists(state)) continue;
                    string name = Path.GetFileNameWithoutExtension(json);
                    DateTime at = File.GetLastWriteTime(state);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                        if (doc.RootElement.TryGetProperty("Name", out var n)) name = n.GetString() ?? name;
                        if (doc.RootElement.TryGetProperty("CreatedAt", out var c)
                            && DateTime.TryParse(c.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                            at = dt;
                    }
                    catch { }
                    entries.Add((name, at, state));
                }
                foreach (var e in entries.OrderByDescending(e => e.at).Take(5))
                    result.Add((e.name, RelativeTime(e.at), e.path));
            }
            catch (Exception ex) { Trace.WriteLine($"[State] picker scan failed: {ex.Message}"); }
            return result;
        }

        // Same buckets as Models/SaveState.RelativeTime — keep the HUD picker consistent with the tab.
        private static string RelativeTime(DateTime at)
        {
            var span = DateTime.Now - at;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
            return at.ToString("MMM d, yyyy");
        }

        /// <summary>Called on the emu thread between retro_run calls.</summary>
        private void ExecuteSaveOnEmuThread()
        {
            _saveStatePending = false;
            string name = _pendingSaveName;

            byte[]? data = _core?.SaveState();
            if (data == null)
            {
                ShowDiskMessage("Save state not supported by this core", 5);
                return;
            }

            // Snapshot the frame NOW (emu thread owns _convBuf swaps) before handing off. _frame is
            // BGRA top-down — exactly what PngEncoder wants; no rotation plumbing exists in this port
            // yet (upstream rotates by _coreRotation; revisit if a rotated-core screenshot looks wrong).
            byte[]? shot = null; int sw = 0, sh = 0;
            lock (_frameLock)
            {
                if (_frame != null && _frameW > 0 && _frameH > 0)
                {
                    sw = _frameW; sh = _frameH;
                    shot = new byte[sw * sh * 4];
                    Array.Copy(_frame, shot, shot.Length);
                }
            }
            string coreName = CoreName;
            Task.Run(() => FinalizeSave(name, data, shot, sw, sh, coreName));
        }

        // Heavy file work off the emu thread. Writes the upstream quartet: .state / .png / .json
        // / .cheevos (rcheevos runtime progress — restored by ExecuteLoadOnEmuThread).
        private void FinalizeSave(string name, byte[] data, byte[]? shot, int sw, int sh, string coreName)
        {
            try
            {
                Directory.CreateDirectory(SaveStateDir);
                string safeName = FileNameHelper.SanitizeFileName(name.Length > 0 ? name : "state");
                string statePath = Path.Combine(SaveStateDir, safeName + ".state");
                string pngPath   = Path.Combine(SaveStateDir, safeName + ".png");
                string jsonPath  = Path.Combine(SaveStateDir, safeName + ".json");
                string cheevosPath = Path.Combine(SaveStateDir, safeName + ".cheevos");

                File.WriteAllBytes(statePath, data);

                // Pair the libretro state with rcheevos's runtime state so achievement
                // hit counts and measured-progress trackers survive a load (RA Section A:
                // "Hit counts should be stored in save states"). Side-car format keeps the
                // .state binary-compatible with other libretro frontends. No-op when RA
                // isn't initialized for this session.
                try
                {
                    byte[]? cheevosBlob = _raClient?.SerializeProgress();
                    if (cheevosBlob != null && cheevosBlob.Length > 0)
                        File.WriteAllBytes(cheevosPath, cheevosBlob);
                    else if (File.Exists(cheevosPath))
                        File.Delete(cheevosPath); // overwriting an older state — a stale side-car would silently restore wrong progress
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RA] Save-state cheevos side-car write failed: {ex.Message}");
                }

                try
                {
                    if (shot != null && sw > 0 && sh > 0) Services.PngEncoder.WriteBgra(pngPath, shot, sw, sh);
                    else { Trace.WriteLine($"[State] screenshot skipped — no frame yet for {safeName}"); pngPath = ""; }
                }
                catch (Exception ex) { Trace.WriteLine($"[State] screenshot failed: {ex.Message}"); pngPath = ""; }

                var meta = new
                {
                    Name        = name,
                    GameTitle   = SaveGameTitle,
                    ConsoleName = _handler.ConsoleName,
                    CoreName    = coreName,
                    RomHash     = SaveRomHash,
                    CreatedAt   = DateTime.Now.ToString("o"),
                };
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(meta,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                ShowDiskMessage($"Saved: {name}", 3);
                Trace.WriteLine($"[State] saved '{name}' core={coreName} bytes={data.Length} png={(pngPath.Length > 0 ? "yes" : "no")}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[State] FinalizeSave error: {ex.Message}");
                ShowDiskMessage("Save state failed", 5);
            }
        }

        /// <summary>Called on the emu thread between retro_run calls.</summary>
        private void ExecuteLoadOnEmuThread()
        {
            // RA hardcore-compliance belt: a boot-time pending load (--load-state) is queued
            // before the async RA login lands, so the RequestLoadState gate can't see it.
            // Re-check here at execution time — mirrors upstream's pending-load refusal.
            if (RaHardcoreActive)
            {
                _loadStatePending = false;
                _pendingLoadData = null;
                ShowDiskMessage("Save state loading is disabled in hardcore mode", 4);
                return;
            }
            byte[]? data = _pendingLoadData;
            string name = _pendingLoadName;

            if (data == null)
            {
                _loadStatePending = false; _loadStateAttempts = 0; _loadStateWarmup = 0;
                _pendingLoadSavedCoreName = "";
                return;
            }

            // Cores declaring RETRO_SERIALIZATION_QUIRK_SINGLE_SESSION (Kronos/Saturn) document that
            // retro_unserialize is invalid across launches; the call returns true but the game freezes.
            // Better a clear message + normal boot than a frozen game.
            if (_coreSingleSessionStates)
            {
                Trace.WriteLine($"[State] load skipped — core declares SINGLE_SESSION quirk: {name}");
                ShowDiskMessage("This core doesn't support resuming save states across launches.", 6);
                _loadStatePending = false; _loadStateAttempts = 0;
                _pendingLoadData = null; _pendingLoadSavedCoreName = "";
                return;
            }

            // Cross-core guard: state byte formats are NOT portable between cores for the same system.
            // retro_unserialize accepts the bytes but the game wedges. Empty saved-core = legacy state
            // (pre-sidecar) — let those through.
            string activeCore = CoreName;
            if (!string.IsNullOrEmpty(_pendingLoadSavedCoreName) && !string.IsNullOrEmpty(activeCore)
                && !string.Equals(_pendingLoadSavedCoreName, activeCore, StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"[State] load refused — state from '{_pendingLoadSavedCoreName}', active core '{activeCore}': {name}");
                ShowDiskMessage($"Save state was made with {_pendingLoadSavedCoreName}; current core is {activeCore}.", 8);
                _loadStatePending = false; _loadStateAttempts = 0; _loadStateWarmup = 0;
                _pendingLoadData = null; _pendingLoadSavedCoreName = "";
                return;
            }

            // Warmup: HW renderers (Beetle PSX HW especially) defer VRAM uploads through context_reset's
            // queue drain — unserializing into a cold pipeline leaves the CPU waiting on a GPU IRQ that
            // never fires (frozen on the saved frame).
            if (_loadStateWarmup < LoadStateWarmupFrames) { _loadStateWarmup++; return; }

            bool ok = _core?.LoadState(data) ?? false;

            // Retry across frames until the core can accept this snapshot (PSX cores during BIOS boot:
            // serialize_size doesn't stabilize for dozens of frames). Bail after ~10 seconds.
            if (!ok && _loadStateAttempts < MaxLoadStateAttempts) { _loadStateAttempts++; return; }

            int attempts = _loadStateAttempts;
            _loadStatePending = false; _loadStateAttempts = 0; _loadStateWarmup = 0;
            _pendingLoadData = null; _pendingLoadSavedCoreName = "";

            // Restore rcheevos's runtime state from the .cheevos side-car — only on a
            // successful core-side load (restoring hits onto a failed/partial emulation
            // state would mis-credit unlocks). Empty/missing blob is a no-op.
            if (ok) _raClient?.DeserializeProgress(_pendingLoadCheevosBlob);
            _pendingLoadCheevosBlob = null;

            // Re-prime controller port-device assignments: Beetle PSX HW's FrontIO rebuilds its device
            // pointers during restore and the assignment can dangle (input dead, emulation alive).
            if (ok && _core != null)
            {
                try { _handler.ConfigureControllerPorts(_core); Trace.WriteLine("[State] re-primed controller ports post-unserialize"); }
                catch (Exception ex) { Trace.WriteLine($"[State] port re-prime failed: {ex.Message}"); }
            }

            // Re-seat the disc for disc-streaming cores: Beetle PSX HW's CDC loses its disc handle on
            // restore (upstream issue #297). Same recipe as a manual swap: eject, set index, deferred
            // re-insert (~100 frames) so the CD engine spins down properly.
            if (ok && _setEjectState != null && _setImageIndex != null && _getImageIndex != null)
            {
                try
                {
                    uint cur = _getImageIndex();
                    _setEjectState(true);
                    _setImageIndex(cur);
                    _diskInsertPendingFrames = 100;
                    Trace.WriteLine($"[State] re-seated disc index {cur} post-unserialize (insert deferred 100 frames)");
                }
                catch (Exception ex) { Trace.WriteLine($"[State] disc re-seat failed: {ex.Message}"); }
            }

            // Some cores wipe their cheat table on state load — re-apply so codes survive
            // (upstream snapshots the list before iterating to avoid racing UI edits).
            if (ok)
            {
                List<Models.Cheat> snapshot;
                lock (_cheatsApplyLock) snapshot = new List<Models.Cheat>(_cheats);
                if (snapshot.Count > 0)
                {
                    try { ApplyAllCheats(snapshot); }
                    catch (Exception ex) { Trace.WriteLine($"[Cheats] re-apply (post state-load) failed: {ex.Message}"); }
                }
            }

            ShowDiskMessage(ok ? $"Loaded: {name}" : $"Failed to load: {name}", 3);
            Trace.WriteLine($"[State] load {(ok ? "succeeded" : "gave up")} after {(ok ? attempts : MaxLoadStateAttempts)} attempts: {name}");
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
                    System.Threading.Interlocked.Increment(ref _vidDupes);
                    return;
                }
                System.Threading.Interlocked.Increment(ref _vidValid);
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
                        // Cog "Flip Display" (composed with a core-requested 180): reverse the pixel
                        // array in place BEFORE publish/record, so screen, recording and screenshots
                        // all agree. 90/270 core rotation stays unsupported on the HW path (as before).
                        if ((_rotationDeg + (_userFlip ? 180 : 0)) % 360 == 180) Reverse180(back, ow * oh);
                        lock (_frameLock) { _frame = back; _frameW = ow; _frameH = oh; _frameSeq++; }
                        FrameReady?.Invoke();
                        // Recording tap (HW): the readback buffer is packed BGRA top-down — exactly
                        // what the recorder wants. Drops itself if dims changed mid-recording.
                        _recording?.QueueVideoFrame(back, ow * oh * 4);
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
            // Honor core-requested rotation composed with the cog's "Flip Display" 180°, by rotating
            // the BGRA buffer (and swapping dims for 90/270) so the displayed Image is upright with
            // the correct aspect — no UI transform. 180° is an in-place pixel reversal (zero-alloc —
            // the user flip can stay on for a whole session, unlike the rare 90/270 core path, which
            // still gets a fresh rotated buffer per frame).
            int effRot = (_rotationDeg + (_userFlip ? 180 : 0)) % 360;
            if (effRot == 180) Reverse180(bgra, w * h);
            else if (effRot != 0) bgra = RotateBgra(bgra, ref w, ref h, effRot);

            lock (_frameLock)
            {
                var prev = _frame;
                _frame = bgra; _frameW = w; _frameH = h; _frameSeq++;
                // Recycle the previous front buffer as the next working buffer (only when bgra IS
                // _convBuf, i.e. not the 90/270 fresh-buffer path, and only if it's the right size)
                // so we ping-pong two buffers with zero allocation.
                if ((effRot == 0 || effRot == 180) && prev != null && prev.Length == need) _convBuf = prev;
            }
            System.Threading.Interlocked.Increment(ref _frameCountSample);   // real produced-frame rate
            FrameReady?.Invoke();                                            // push the frame to the window to present
            // Recording tap (SW): bgra is the finished packed top-down frame (post-rotation,
            // so the video matches what's on screen). Drops itself on mid-record dim changes.
            _recording?.QueueVideoFrame(bgra, w * h * 4);
        }

        // 180° rotation of a packed BGRA buffer IN PLACE: reverse the pixel array (swap 4-byte
        // pixels from both ends). Zero-alloc, used by both the SW funnel and the HW readback path.
        private static unsafe void Reverse180(byte[] buf, int pixels)
        {
            fixed (byte* p = buf)
            {
                uint* a = (uint*)p;
                uint* b = a + pixels - 1;
                while (a < b) { uint t = *a; *a++ = *b; *b-- = t; }
            }
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

        // Recording taps capture the core's RAW samples (its true rate, pre-DRC — DRC only nudges
        // the playback stream's resample ratio; the recording must not inherit that wobble).
        private byte[] _recAudioBuf = new byte[8192];
        private void Audio_cb(short left, short right)
        {
            _audio?.QueueSample(left, right);
            if (_recording is { IsRecording: true })
            {
                _recAudioBuf[0] = (byte)left;  _recAudioBuf[1] = (byte)(left >> 8);
                _recAudioBuf[2] = (byte)right; _recAudioBuf[3] = (byte)(right >> 8);
                _recording.QueueAudioSamples(_recAudioBuf, 4);
            }
        }
        private UIntPtr AudioBatch_cb(IntPtr data, UIntPtr frames)
        {
            _audio?.QueueBatch(data, (int)frames);
            if (_recording is { IsRecording: true })
            {
                int bytes = (int)frames * 4;   // S16LE stereo
                if (_recAudioBuf.Length < bytes) _recAudioBuf = new byte[bytes];
                Marshal.Copy(data, _recAudioBuf, 0, bytes);
                _recording.QueueAudioSamples(_recAudioBuf, bytes);
            }
            return frames;
        }
        private void InputPoll_cb() { /* SdlInput.Poll already called at top of the loop */ }

        // Number of live emulator sessions. The Controls-panel ControllerManager checks this so it
        // doesn't call SDL_PumpEvents concurrently with the emu loop (SDL pumping isn't multi-thread safe).
        private static int _activeCount;
        public static bool AnyActive => System.Threading.Volatile.Read(ref _activeCount) > 0;

        /// <summary>In-flight recording encode, if any — the game-host waits on this before
        /// process exit so closing the game mid-recording still produces the final .mp4.</summary>
        public System.Threading.Tasks.Task? PendingRecordingEncode => _recording?.EncodeTask;

        // ── RetroAchievements runtime (A8c) — lives HERE in the host process where the
        //    core's memory is. Login/identify run on the RaInit worker; rc_client_do_frame
        //    runs on the emu thread after every retro_run; the unlock toast renders in
        //    GlOsd (Skia). The host never writes config/DB — results ride GameHostResult. ──
        private Services.RetroAchievementsClient? _raClient;
        private volatile bool _raReady;            // game identified → DoFrame is live
        private bool _raHardcoreActive;            // snapshot at launch (upstream semantics)
        private Services.RetroAchievementsClient.MemoryRegion[]? _raPendingMemoryRegions;

        /// <summary>True while a hardcore RA session is active — gates cheats + state loads.</summary>
        public bool RaHardcoreActive => _raHardcoreActive && _raClient != null;

        // Session results for the parent's DB/config ingest (host writes neither).
        public int RaGameIdResult { get; private set; }
        public string RaOutcomeResult { get; private set; } = "";
        public string? RaNewTokenResult { get; private set; }
        public string? RaLiveProgressJsonResult { get; private set; }

        // Unlock toast state — written by RA events (emu thread) + the badge fetch task,
        // read by the present thread. Guarded by _raToastLock.
        private readonly object _raToastLock = new();
        private (string Header, string Title, string Desc, string Points, SkiaSharp.SKBitmap? Badge, DateTime ShownAt)? _raToast;
        private readonly Dictionary<string, SkiaSharp.SKBitmap?> _raBadgeCache = new();
        private static readonly System.Net.Http.HttpClient _raBadgeHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

        /// <summary>Library-decided leaderboard toast (stdin "show-ra-toast"):
        /// header-less two-liner, mirroring upstream's ShowAchievementToast(headline, subline, 0).</summary>
        public void ShowRaToastFromParent(string headline, string subline)
            => SetRaToast("", headline, subline, "", null);

        private void RaDoFrame()
        {
            if (!_raReady) return;
            try { _raClient?.DoFrame(); }
            catch (Exception ex) { Trace.WriteLine($"[RA] DoFrame error: {ex.Message}"); }
        }

        private void RaIdle()
        {
            if (!_raReady) return;
            try { _raClient?.Idle(); }
            catch (Exception ex) { Trace.WriteLine($"[RA] Idle error: {ex.Message}"); }
        }

        // Toast envelope: 250ms fade-in → 4s hold → 400ms fade-out (upstream's timings,
        // default style duration). Returns null once expired (and clears the state).
        private ((string, string, string, string, SkiaSharp.SKBitmap?)? toast, float alpha) RaToastForPresent()
        {
            lock (_raToastLock)
            {
                if (_raToast == null) return (null, 0f);
                var t = _raToast.Value;
                double el = (DateTime.UtcNow - t.ShownAt).TotalSeconds;
                // Hold time follows the user's ToastStyle.DurationSec (default 4s).
                double hold = Services.ToastStyleRenderer.Duration(
                    App.Configuration?.GetRetroAchievementsConfiguration()?.ToastStyle).TotalSeconds;
                float a;
                if (el < 0.25) a = (float)(el / 0.25);
                else if (el < 0.25 + hold) a = 1f;
                else if (el < 0.65 + hold) a = (float)((0.65 + hold - el) / 0.40);
                else { _raToast = null; return (null, 0f); }
                return ((t.Header, t.Title, t.Desc, t.Points, t.Badge), a);
            }
        }

        private DateTime _lastRaSoundUtc = DateTime.MinValue;

        private void SetRaToast(string header, string title, string desc, string points, string? badgeUrl)
        {
            SkiaSharp.SKBitmap? badge = null;
            bool needFetch = false;
            lock (_raToastLock)
            {
                if (badgeUrl != null && _raBadgeCache.TryGetValue(badgeUrl, out var cached)) badge = cached;
                else if (badgeUrl != null) needFetch = true;
                _raToast = (header, title, desc, points, badge, DateTime.UtcNow);
            }
            // Toast sound (upstream plays FriendNotificationSound for both the unlock toast and
            // the parent-routed LB toast, gated by the Friends sound toggle + cooldown). The
            // service shells out to ffplay/pw-play, so it works from the host process too —
            // Notification1.mp3 ships beside the binary.
            try
            {
                var fcfg = App.Configuration?.GetFriendsConfiguration();
                if (fcfg is { LbToastSoundEnabled: true }
                    && (DateTime.UtcNow - _lastRaSoundUtc).TotalSeconds >= fcfg.LbToastCooldownSec)
                {
                    Services.FriendNotificationSound.Play(App.Configuration);
                    _lastRaSoundUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex) { Trace.WriteLine($"[RA] toast sound failed: {ex.Message}"); }
            if (!needFetch || badgeUrl == null) return;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                SkiaSharp.SKBitmap? bmp = null;
                try
                {
                    byte[] png = await _raBadgeHttp.GetByteArrayAsync(badgeUrl);
                    bmp = SkiaSharp.SKBitmap.Decode(png);
                }
                catch (Exception ex) { Trace.WriteLine($"[RA] badge fetch failed ({badgeUrl}): {ex.Message}"); }
                lock (_raToastLock)
                {
                    _raBadgeCache[badgeUrl] = bmp;
                    // Late badge joins the toast mid-display if it's still the same unlock.
                    if (_raToast is { } cur && cur.Title == title && cur.Badge == null)
                        _raToast = (cur.Header, cur.Title, cur.Desc, cur.Points, bmp, cur.ShownAt);
                }
            });
        }

        // ── RA challenge + progress indicators (rcheevos CHALLENGE_/PROGRESS_INDICATOR events).
        // Upstream defines the events but never rendered them — this follows RetroArch's standard
        // presentation: primed challenge badges bottom-right, a transient measured-progress pill
        // top-right (rcheevos drives show/update/hide). Badge bitmaps share the toast cache.
        private readonly Dictionary<uint, SkiaSharp.SKBitmap?> _raChallenges = new();   // under _raToastLock
        private (string Text, SkiaSharp.SKBitmap? Badge)? _raProgress;                  // under _raToastLock
        private int _raIndicatorVersion;   // bumped on every change → the OSD signature cache

        private void SetRaChallenge(AchievementInfo info, bool shown)
        {
            lock (_raToastLock)
            {
                if (!shown) _raChallenges.Remove(info.Id);
                else
                {
                    _raChallenges[info.Id] =
                        info.BadgeUrl != null && _raBadgeCache.TryGetValue(info.BadgeUrl, out var b) ? b : null;
                    if (info.BadgeUrl != null && !_raBadgeCache.ContainsKey(info.BadgeUrl))
                        FetchBadgeThen(info.BadgeUrl, bmp =>
                        { if (_raChallenges.ContainsKey(info.Id)) { _raChallenges[info.Id] = bmp; _raIndicatorVersion++; } });
                }
                _raIndicatorVersion++;
            }
        }

        private void SetRaProgress(AchievementInfo? info, bool shown)
        {
            lock (_raToastLock)
            {
                if (!shown || info == null) _raProgress = null;
                else
                {
                    string text = info.MeasuredProgress.Length > 0
                        ? info.MeasuredProgress
                        : $"{info.MeasuredPercent:0}%";
                    var badge = info.BadgeUrl != null && _raBadgeCache.TryGetValue(info.BadgeUrl, out var b) ? b : null;
                    _raProgress = (text, badge);
                    if (info.BadgeUrl != null && !_raBadgeCache.ContainsKey(info.BadgeUrl))
                        FetchBadgeThen(info.BadgeUrl, bmp =>
                        { if (_raProgress is { } cur) { _raProgress = (cur.Text, bmp); _raIndicatorVersion++; } });
                }
                _raIndicatorVersion++;
            }
        }

        // Fetch a badge into the shared cache, then run apply under the toast lock (no-op if the
        // indicator moved on meanwhile). Mirrors the toast's late-badge join.
        private void FetchBadgeThen(string badgeUrl, Action<SkiaSharp.SKBitmap?> apply)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                SkiaSharp.SKBitmap? bmp = null;
                try
                {
                    byte[] png = await _raBadgeHttp.GetByteArrayAsync(badgeUrl);
                    bmp = SkiaSharp.SKBitmap.Decode(png);
                }
                catch (Exception ex) { Trace.WriteLine($"[RA] badge fetch failed ({badgeUrl}): {ex.Message}"); }
                lock (_raToastLock)
                {
                    _raBadgeCache[badgeUrl] = bmp;
                    apply(bmp);
                }
            });
        }

        /// <summary>Snapshot for the present thread: primed challenge badges (stable order) +
        /// the progress pill, plus a version for the OSD's dirty-signature cache.</summary>
        private (List<SkiaSharp.SKBitmap?>? challenges, (string Text, SkiaSharp.SKBitmap? Badge)? progress, int version)
            RaIndicatorsForPresent()
        {
            lock (_raToastLock)
            {
                List<SkiaSharp.SKBitmap?>? ch = null;
                if (_raChallenges.Count > 0)
                    ch = _raChallenges.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                return (ch, _raProgress, _raIndicatorVersion);
            }
        }

        /// <summary>Login + identify (RaInit worker thread). Port of upstream's
        /// InitRetroAchievements; config writes become RaNewTokenResult for the parent.</summary>
        private void InitRetroAchievements()
        {
            try
            {
                var raConfig = App.Configuration?.GetRetroAchievementsConfiguration();
                if (raConfig == null || !raConfig.IsConfigured)
                {
                    Trace.WriteLine("[RA] Not signed in — skipping.");
                    return;
                }

                string consoleName = HandlerConsoleName;
                uint consoleId = Services.RetroAchievementsClient.GetConsoleId(consoleName);
                if (consoleId == 0)
                {
                    Trace.WriteLine($"[RA] No RA console ID for '{consoleName}' — skipping.");
                    ShowDiskMessage($"RetroAchievements: {consoleName} not supported", 5);
                    return;
                }

                // RA hardcore-compliance carve-out for PSP: PPSSPP reads cheats from
                // cheats/<DiscID>.ini directly, bypassing the retro_cheat_set gate we
                // control — so hardcore can't be honestly enforced there. Drop to
                // softcore for the session and say so (upstream's posture).
                bool effectiveHardcore = raConfig.HardcoreMode;
                if (effectiveHardcore && string.Equals(consoleName, "PSP", StringComparison.Ordinal))
                {
                    effectiveHardcore = false;
                    Trace.WriteLine("[RA] Hardcore refused for PSP — PPSSPP cheat path not gateable; softcore this session.");
                    ShowDiskMessage("Hardcore Mode is disabled for PSP titles — achievements still track", 6);
                }

                // Stamp the core into the rcheevos UA BEFORE login/identify HTTP fires.
                Services.RetroAchievementsClient.SetCoreContext(_core?.CoreName, _core?.CoreVersion);

                var client = new Services.RetroAchievementsClient();
                client.Initialize(_core, effectiveHardcore, consoleName);
                _raHardcoreActive = effectiveHardcore;

                // Replay descriptors the core published during retro_load_game (before
                // the client existed) — without this the descriptor path is dead code.
                if (_raPendingMemoryRegions != null)
                    client.SetMemoryDescriptors(_raPendingMemoryRegions);

                client.AchievementTriggered += info => SetRaToast(
                    "ACHIEVEMENT UNLOCKED", info.Title, info.Description,
                    info.Points > 0 ? $"{info.Points} points" : "", info.BadgeUrl);
                client.GameCompleted += () => SetRaToast(
                    "GAME COMPLETE", "Mastery!", "All achievements earned!", "", null);
                // In-game indicators (emu thread → state behind the toast lock; the present
                // thread snapshots via RaIndicatorsForPresent).
                client.ChallengeIndicatorChanged += SetRaChallenge;
                client.ProgressIndicatorChanged += SetRaProgress;
                // Leaderboard SCOREBOARD: the triumph/proximity decision needs the
                // friend-rank cache, which lives in the LIBRARY (host has no DB and
                // never writes config). Ship the event up; the library decides and
                // sends a "show-ra-toast" line back down stdin for in-game display.
                client.LeaderboardScoreboardReceived += info =>
                {
                    try
                    {
                        string json = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            lb = info.LeaderboardId, rank = info.NewRank,
                            submitted = info.SubmittedScore, best = info.BestScore,
                            title = info.LbTitle, lib = info.LowerIsBetter,
                            hc = RaHardcoreActive,
                        });
                        EmitHostCommand?.Invoke($"lb-scoreboard {json}");
                    }
                    catch (Exception ex) { Trace.WriteLine($"[RA] lb-scoreboard emit failed: {ex.Message}"); }
                };
                client.ResetRequested += () =>
                {
                    // rcheevos demands a reset (e.g. hardcore being enabled): reset the GAME too,
                    // not just the achievement runtime — RA Section B requires switching into
                    // hardcore to come with a full game reset (auto-fail otherwise). Stricter
                    // than upstream, which resets only the client.
                    Trace.WriteLine("[RA] Reset requested by rcheevos — resetting game + runtime.");
                    try { client.Reset(); } catch { }
                    _resetRequested = true;   // emu loop performs retro_reset before the next frame
                };

                // Token login first, password fallback (new token rides the results file —
                // the host never writes config; the parent saves it).
                Trace.WriteLine($"[RA] Logging in as {raConfig.Username}...");
                bool loginOk = false; string? loginErr = null; string? newToken = null;
                if (!string.IsNullOrWhiteSpace(raConfig.Token))
                    (loginOk, loginErr, newToken) = client.LoginWithToken(raConfig.Username, raConfig.Token);
                if (!loginOk && !string.IsNullOrWhiteSpace(raConfig.Password))
                {
                    (loginOk, loginErr, newToken) = client.LoginWithPassword(raConfig.Username, raConfig.Password);
                    if (loginOk && !string.IsNullOrWhiteSpace(newToken))
                        RaNewTokenResult = newToken;
                }
                if (!loginOk)
                {
                    Trace.WriteLine($"[RA] Login failed: {loginErr}");
                    ShowDiskMessage("RetroAchievements: login failed", 5);
                    client.Dispose();
                    return;
                }
                Trace.WriteLine("[RA] Login OK");

                string romPath = _romPath;
                Trace.WriteLine($"[RA] Loading game: {romPath} (console {consoleId})");
                _raClient = client;   // descriptors arriving mid-load can now reach the client
                var (loadOk, loadErr) = client.LoadGame(romPath, consoleId);
                if (!loadOk)
                {
                    Trace.WriteLine($"[RA] Game load failed: {loadErr}");
                    ShowDiskMessage("RetroAchievements: game not in database", 5);
                    // rcheevos's two "no playable achievements" strings ("Unknown game" /
                    // "Response contained no sets") both read as not_in_database; anything
                    // else (network, timeout, credential reject) is a generic load_failed.
                    string err = loadErr ?? "";
                    bool noAchievements =
                           err.IndexOf("unknown game",       StringComparison.OrdinalIgnoreCase) >= 0
                        || err.IndexOf("no sets",            StringComparison.OrdinalIgnoreCase) >= 0
                        || err.IndexOf("response contained", StringComparison.OrdinalIgnoreCase) >= 0;
                    RaOutcomeResult = noAchievements ? "not_in_database" : "load_failed";
                    Services.RaLog.Write($"launch identify failed: console={consoleName} rom=\"{romPath}\" outcome={RaOutcomeResult} err=\"{err}\"");
                    _raClient = null;
                    client.Dispose();
                    return;
                }

                RaGameIdResult = client.GetGameId();
                RaOutcomeResult = "identified";
                _raReady = true;
                Services.RaLog.Write($"launch identified: console={consoleName} raGameId={RaGameIdResult} raTitle=\"{client.GetGameTitle()}\"");
                Trace.WriteLine($"[RA] Game identified: {client.GetGameTitle()} (id={RaGameIdResult})");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RA] InitRetroAchievements exception: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            if (_running) System.Threading.Interlocked.Decrement(ref _activeCount);
            _running = false;
            // RetroAchievements teardown: stop the per-frame ticks, snapshot live progress
            // for the parent's DB ingest, then destroy the native client.
            _raReady = false;
            try
            {
                if (_raClient != null)
                {
                    var snap = _raClient.GetLiveProgressSnapshot();
                    if (snap.Count > 0 && RaGameIdResult > 0)
                    {
                        var payload = new Models.RALiveProgress { Hardcore = _raHardcoreActive };
                        foreach (var kvp in snap.OrderByDescending(k => k.Value.MeasuredPercent).Take(50))
                            payload.Achievements[kvp.Key] = new Models.RALiveAchievementProgress
                            { Percent = kvp.Value.MeasuredPercent, ProgressText = kvp.Value.MeasuredProgress ?? "" };
                        RaLiveProgressJsonResult = System.Text.Json.JsonSerializer.Serialize(payload);
                    }
                    _raClient.Dispose();
                    _raClient = null;
                }
            }
            catch (Exception ex) { Trace.WriteLine($"[RA] teardown: {ex.Message}"); }

            // Quitting mid-recording (ANY teardown path funnels through here): stop + encode
            // rather than losing the capture. Stop() never blocks — the encode runs in the
            // background and is exposed via PendingRecordingEncode for hosts to wait on.
            try { if (_recording is { IsRecording: true }) _recording.Stop(); } catch { }
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
