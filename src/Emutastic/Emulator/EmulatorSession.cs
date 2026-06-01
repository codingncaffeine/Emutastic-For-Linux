using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Emutastic.Platform;
using Emutastic.Services;

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
        const uint ENV_GET_OVERSCAN = 2;
        const uint ENV_GET_CAN_DUPE = 3;
        const uint ENV_SET_PERFORMANCE_LEVEL = 8;
        const uint ENV_GET_SYSTEM_DIRECTORY = 9;
        const uint ENV_SET_PIXEL_FORMAT = 10;
        const uint ENV_GET_VARIABLE = 15;
        const uint ENV_GET_VARIABLE_UPDATE = 17;
        const uint ENV_GET_LOG_INTERFACE = 27;
        const uint ENV_GET_CORE_ASSETS_DIRECTORY = 30;
        const uint ENV_GET_SAVE_DIRECTORY = 31;
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

        private Thread? _thread;
        private volatile bool _running;

        // latest converted frame (BGRA8888), guarded by _frameLock
        private readonly object _frameLock = new();
        private byte[]? _frame;
        private int _frameW, _frameH;
        private long _frameSeq;

        public string CoreName => _core?.CoreName ?? "?";
        public SdlInput Input => _input;

        public EmulatorSession(string corePath, string romPath)
        {
            _corePath = corePath;
            _romPath = romPath;
            _input = new SdlInput();

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
                _core = new LibretroCore(_corePath);
                // System (BIOS) and save dirs follow XDG/portable layout (AppPaths creates them);
                // core-assets default to the core's own folder.
                _systemDirPtr = Marshal.StringToHGlobalAnsi(AppPaths.GetFolder("System"));
                _saveDirPtr = Marshal.StringToHGlobalAnsi(AppPaths.GetFolder("Saves"));
                _coreAssetsDirPtr = Marshal.StringToHGlobalAnsi(System.IO.Path.GetDirectoryName(_corePath));
                _core.SetCallbacks(_envCb, _videoCb, _audioCb, _audioBatchCb, _inputPollCb, _inputStateCb);
                _core.Init();

                if (!_core.LoadGame(_romPath))
                {
                    error = _core.LastError ?? "retro_load_game failed (the core rejected the ROM).";
                    return false;
                }

                _fps = _core.AvInfo.timing.fps > 0 ? _core.AvInfo.timing.fps : 60.0;
                _sampleRate = _core.AvInfo.timing.sample_rate > 0 ? _core.AvInfo.timing.sample_rate : 44100;
                _audio = new SdlAudio((int)Math.Round(_sampleRate));

                _running = true;
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
            double frameMs = 1000.0 / _fps;
            var sw = Stopwatch.StartNew();
            double next = sw.Elapsed.TotalMilliseconds;
            while (_running)
            {
                _input.Poll();
                try { _core!.Run(); } catch (Exception ex) { Trace.WriteLine($"[Emu] retro_run threw: {ex}"); break; }

                // Pace to the core's frame interval, but yield if the audio buffer is backed up
                // (mirrors upstream AudioPlayer.GetBufferedMs backpressure so A/V stay in sync).
                next += frameMs;
                double now = sw.Elapsed.TotalMilliseconds;
                double sleep = next - now;
                if (_audio != null && _audio.QueuedMs > 100) sleep = Math.Max(sleep, _audio.QueuedMs - 100);
                if (sleep > 0) Thread.Sleep((int)Math.Min(sleep, 100));
                else if (sleep < -250) next = now; // fell far behind; resync rather than spiral
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
                    if (data != IntPtr.Zero) Marshal.WriteByte(data, 0); // no core-option changes pending
                    return true;
                case ENV_GET_OVERSCAN:
                case ENV_GET_VARIABLE:
                default:
                    return false; // unsupported / use core defaults — cores cope (incl. SET_HW_RENDER → SW)
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
            var bgra = new byte[w * h * 4];
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
            lock (_frameLock) { _frame = bgra; _frameW = w; _frameH = h; _frameSeq++; }
        }

        /// <summary>
        /// Hands the UI the latest frame if it's newer than <paramref name="lastSeq"/>.
        /// Returns false when no new frame is available. The returned buffer is immutable
        /// (the emu thread allocates a fresh one per frame), so it's safe to read off-lock.
        /// </summary>
        public bool TrySnapshot(ref long lastSeq, out byte[]? buf, out int w, out int h)
        {
            lock (_frameLock)
            {
                if (_frame == null || _frameSeq == lastSeq) { buf = null; w = h = 0; return false; }
                lastSeq = _frameSeq; buf = _frame; w = _frameW; h = _frameH; return true;
            }
        }

        private void Audio_cb(short left, short right) => _audio?.QueueSample(left, right);
        private UIntPtr AudioBatch_cb(IntPtr data, UIntPtr frames) { _audio?.QueueBatch(data, (int)frames); return frames; }
        private void InputPoll_cb() { /* SdlInput.Poll already called at top of the loop */ }

        public void Dispose()
        {
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
        }
    }
}
