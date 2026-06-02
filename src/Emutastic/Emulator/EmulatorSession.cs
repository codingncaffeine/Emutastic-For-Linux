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

                _fps = _core.AvInfo.timing.fps > 0 ? _core.AvInfo.timing.fps : 60.0;
                double hwFps = _handler.HardwareTargetFps;   // console-forced rate (e.g. Dreamcast 60); -1 = use core
                if (hwFps > 0) _fps = hwFps;

                // Only a deliberate per-console AR override (e.g. TG16 → 4:3) changes the display; 0
                // keeps the current pixel-ratio rendering for everything else (incl. rotated games).
                var geo = _core.AvInfo.geometry;
                DisplayAspectRatio = _handler.GetDisplayAspectRatio(geo.base_width, geo.base_height, geo.aspect_ratio);
                _sampleRate = _core.AvInfo.timing.sample_rate > 0 ? _core.AvInfo.timing.sample_rate : 44100;
                _audio = new SdlAudio((int)Math.Round(_sampleRate));

                _running = true;
                System.Threading.Interlocked.Increment(ref _activeCount);
                _thread = new Thread(RunLoop) { IsBackground = true, Name = "EmuLoop" };
                _thread.Start();
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

            var frameTimer = Stopwatch.StartNew();
            while (_running)
            {
                // Reset is honored even while paused (so the pill's Reset isn't dead when paused).
                if (_resetRequested) { _resetRequested = false; try { _core!.Reset(); } catch (Exception ex) { Trace.WriteLine($"[Emu] reset threw: {ex}"); } }

                // Paused: stop advancing the core (frame stays frozen) but keep the thread responsive.
                if (_paused) { Thread.Sleep(16); frameTimer.Restart(); continue; }

                _input.Poll();

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
                    if (_running && _audio != null && _audio.QueuedMs < lowWatermark)
                    {
                        _input.Poll();
                        long t2 = frameTimer.ElapsedTicks;
                        try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }
                        System.Threading.Interlocked.Add(ref _coreRunTicks, frameTimer.ElapsedTicks - t2);
                        System.Threading.Interlocked.Increment(ref _coreRunCalls);
                    }
                }

                // Stopwatch pacing: sleep most of the remaining budget, then SPIN the last ~1ms for
                // sub-millisecond accuracy → steady frame production (the fix for chunky 60fps).
                double remaining = targetFrameMs - frameTimer.Elapsed.TotalMilliseconds;
                if (remaining > 1.5) Thread.Sleep((int)(remaining - 1.0));
                while (_running && frameTimer.Elapsed.TotalMilliseconds < targetFrameMs) Thread.SpinWait(10);
                frameTimer.Restart();
            }
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
                    "[Emu] emulation thread did not exit; leaking core/SDL handles to avoid use-after-free.");
                return;
            }

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
