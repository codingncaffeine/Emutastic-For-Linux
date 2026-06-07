using System;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// SDL3-backed audio output. Replaces the upstream Windows AudioPlayer (NAudio/WASAPI).
    ///
    /// libretro cores emit signed-16 stereo PCM at the core's native sample rate
    /// (retro_system_av_info.timing.sample_rate). SDL3 audio streams resample internally to the
    /// device rate, so we open the stream at the core's rate and just push samples — this also
    /// replaces NAudio's WdlResamplingSampleProvider, which did not port to Linux.
    ///
    /// Backpressure: the emulation loop reads <see cref="QueuedBytes"/> (mirrors upstream's
    /// AudioPlayer.GetBufferedMs contract) to pace itself so audio/video stay in sync.
    /// </summary>
    public sealed class SdlAudio : IDisposable
    {
        const uint SDL_INIT_AUDIO = 0x00000010;
        const uint SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK = 0xFFFFFFFF;
        const int SDL_AUDIO_S16LE = 0x8010; // SDL_AudioFormat: signed 16-bit little-endian

        [StructLayout(LayoutKind.Sequential)]
        struct SDL_AudioSpec
        {
            public int format;   // SDL_AudioFormat
            public int channels;
            public int freq;
        }

        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_InitSubSystem(uint flags);
        [DllImport("SDL3")] static extern void SDL_QuitSubSystem(uint flags);
        [DllImport("SDL3")] static extern IntPtr SDL_OpenAudioDeviceStream(uint devid, in SDL_AudioSpec spec, IntPtr cb, IntPtr userdata);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_ResumeAudioStreamDevice(IntPtr stream);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_PauseAudioStreamDevice(IntPtr stream);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_PutAudioStreamData(IntPtr stream, IntPtr buf, int len);
        [DllImport("SDL3")] static extern int SDL_GetAudioStreamQueued(IntPtr stream);
        // Dynamic rate control: scales the rate at which the stream consumes its INPUT (a speed ratio,
        // 0.01..100, default 1.0). >1 = consume faster (drain the input queue) + slightly higher pitch.
        // This is the INVERSE of RetroArch's src_ratio (out/in); we account for that in the servo sign.
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_SetAudioStreamFrequencyRatio(IntPtr stream, float ratio);
        [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SDL_ClearAudioStream(IntPtr stream);
        [DllImport("SDL3")] static extern void SDL_DestroyAudioStream(IntPtr stream);
        [DllImport("SDL3")] static extern IntPtr SDL_GetError();

        private IntPtr _stream;
        private readonly int _sampleRate;
        // Time-based queued estimate (frames submitted minus playback time elapsed). The raw byte
        // count from SDL drops in device-buffer chunks (stair-steps), which makes the emu loop's
        // backpressure/catch-up guards fire erratically → judder; a time estimate is smooth.
        private long _framesQueued;
        private readonly System.Diagnostics.Stopwatch _playClock = new();

        // ---- Dynamic Rate Control (RetroArch-style) ----
        // Servo the resampler ratio by <=0.5% to hold the input queue at a target cushion, so audio
        // neither underruns nor runs away. Replaces the coarse whole-frame catch-up/skip with a smooth,
        // inaudible nudge. Controls on the REAL input-side queue (SDL_GetAudioStreamQueued), not the
        // synthetic wall-clock estimate and not the output side (which sits near zero for a device-pulled
        // stream). See docs/frame-pacing-and-vsync.md.
        const double DrcTargetMs    = 150.0;   // cushion setpoint (= prefill level ≈ "half full")
        const double DrcKp          = 5e-5;    // ratio per ms of error; ~100ms error -> ~0.5%
        const double DrcMaxDelta    = 0.005;   // ±0.5% clamp (RetroArch audio_rate_control_delta)
        const double DrcSlewPerCall = 3e-4;    // max ratio change per call -> gradual, inaudible
        const double DrcSmoothing   = 0.10;    // EMA factor for the occupancy signal (rejects chunking)
        private double _drcQueuedMsAvg;        // low-passed input-queue occupancy (ms)
        private float  _drcRatio = 1.0f;       // currently applied frequency ratio
        private bool   _drcStarted;
        private bool   _drcLastAboveZero = true; // edge tracking so one stall counts as one underrun
        private long   _underruns;             // count of distinct underrun events (instrumentation)

        public SdlAudio(int sampleRate)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
            SDL_InitSubSystem(SDL_INIT_AUDIO);

            var spec = new SDL_AudioSpec { format = SDL_AUDIO_S16LE, channels = 2, freq = _sampleRate };
            _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, in spec, IntPtr.Zero, IntPtr.Zero);
            if (_stream == IntPtr.Zero)
            {
                string err = Marshal.PtrToStringUTF8(SDL_GetError()) ?? "unknown";
                System.Diagnostics.Trace.WriteLine($"[SdlAudio] SDL_OpenAudioDeviceStream failed: {err}");
                return;
            }
            // Streams created by SDL_OpenAudioDeviceStream start paused.
            SDL_ResumeAudioStreamDevice(_stream);
        }

        public bool IsOpen => _stream != IntPtr.Zero;

        /// <summary>Bytes currently queued/un-played (raw SDL value).</summary>
        public int QueuedBytes => _stream != IntPtr.Zero ? SDL_GetAudioStreamQueued(_stream) : 0;

        /// <summary>Smooth estimate of milliseconds of audio still queued = frames submitted minus
        /// playback time elapsed (the device consumes ~_sampleRate input-frames/sec). Used by the emu
        /// loop's pacing guards; smooth so they don't fire on the raw byte stair-step.</summary>
        public double QueuedMs
        {
            get
            {
                if (_stream == IntPtr.Zero || !_playClock.IsRunning) return 0;
                double producedMs = (double)_framesQueued / _sampleRate * 1000.0;
                double ms = producedMs - _playClock.Elapsed.TotalMilliseconds;
                return ms > 0 ? ms : 0;
            }
        }

        /// <summary>REAL input-side queued audio in ms — the standing backlog the device still has to
        /// pull and play. SDL_GetAudioStreamQueued returns bytes put in but not yet consumed (input
        /// format: S16 stereo = 4 bytes/frame). Unlike <see cref="QueuedMs"/> (a wall-clock estimate) this
        /// reflects the actual device drain, so it's the correct control signal for pacing + DRC.</summary>
        public double QueuedMsReal
        {
            get
            {
                if (_stream == IntPtr.Zero) return 0;
                int bytes = SDL_GetAudioStreamQueued(_stream);
                return bytes > 0 ? bytes / 4.0 / _sampleRate * 1000.0 : 0;
            }
        }

        /// <summary>The DRC-smoothed input-queue occupancy (ms) — the EMA the rate controller servos.
        /// The emu loop's coarse watermark guards use THIS, not raw <see cref="QueuedMsReal"/>: a single
        /// device gulp can dip the raw stair-stepped value under the watermark and fire a spurious
        /// whole-frame catch-up (the prior judder-regression mode). Updated by <see cref="ApplyDrc"/>.</summary>
        public double QueuedMsSmoothed => _drcStarted ? _drcQueuedMsAvg : QueuedMsReal;

        /// <summary>RetroArch-style dynamic rate control: nudge the resampler ratio by ≤0.5% to keep the
        /// input queue centered on <paramref name="targetMs"/>, so audio stays glued to the device clock
        /// without coarse whole-frame corrections. Call once per produced frame, after prefill. Sign:
        /// queue too FULL (error&gt;0) ⇒ consume input FASTER ⇒ ratio&gt;1 (SDL's ratio is a speed ratio, the
        /// inverse of RetroArch's src_ratio). Steady-state parks slightly off target (P-only) — harmless,
        /// stays well inside the watermark backstops.</summary>
        public void ApplyDrc(double targetMs = DrcTargetMs)
        {
            if (_stream == IntPtr.Zero || !_playClock.IsRunning) return; // still prefilling / no device
            double q = QueuedMsReal;
            if (q <= 0 && _drcLastAboveZero) _underruns++;   // count the edge, not every zero-frame
            _drcLastAboveZero = q > 0;
            // Low-pass the occupancy so resampler/device chunking can't drive audible ratio swings.
            _drcQueuedMsAvg = _drcStarted ? _drcQueuedMsAvg + DrcSmoothing * (q - _drcQueuedMsAvg) : q;
            _drcStarted = true;

            double error = _drcQueuedMsAvg - targetMs;  // >0 = too full => drain faster (ratio>1)
            float desired = (float)Math.Clamp(1.0 + DrcKp * error, 1.0 - DrcMaxDelta, 1.0 + DrcMaxDelta);
            // Slew-limit: bound per-call ratio change so corrections are gradual (the needed steady
            // correction is only ~0.2% ≈ 3.4 cents — inaudible when approached slowly).
            float step = Math.Clamp(desired - _drcRatio, (float)-DrcSlewPerCall, (float)DrcSlewPerCall);
            _drcRatio += step;
            SDL_SetAudioStreamFrequencyRatio(_stream, _drcRatio);
        }

        /// <summary>Sample DRC state for the status bar / logs (smooth queue ms, applied ratio, underruns).</summary>
        public void SampleDrc(out double queuedMs, out double ratio, out long underruns)
        {
            queuedMs = _drcStarted ? _drcQueuedMsAvg : QueuedMsReal;
            ratio = _drcRatio;
            underruns = _underruns;
        }

        // Monotonic total of sample-frames ever enqueued (never reset by Clear) — lets the
        // emu loop measure how much audio ONE retro_run produced: the game's own clock.
        // A 30fps-content Dreamcast title emits 2× a 60Hz frame's audio per run; pacing by
        // this delta (not the nominal console rate) is what keeps the cushion stable.
        private long _framesWrittenTotal;
        public long FramesWrittenTotal => _framesWrittenTotal;

        /// <summary>Queue a batch of interleaved S16 stereo samples (libretro audio_sample_batch).</summary>
        public void QueueBatch(IntPtr data, int frames)
        {
            if (_stream == IntPtr.Zero || data == IntPtr.Zero || frames <= 0) return;
            SDL_PutAudioStreamData(_stream, data, frames * 4); // 2 channels * 2 bytes
            if (!_playClock.IsRunning) _playClock.Start();     // playback clock starts at first audio
            _framesQueued += frames;
            _framesWrittenTotal += frames;
        }

        /// <summary>Queue a single stereo sample pair (libretro audio_sample).</summary>
        public unsafe void QueueSample(short left, short right)
        {
            if (_stream == IntPtr.Zero) return;
            short* pair = stackalloc short[2];
            pair[0] = left; pair[1] = right;
            SDL_PutAudioStreamData(_stream, (IntPtr)pair, 4);
            if (!_playClock.IsRunning) _playClock.Start();
            _framesQueued++;
            _framesWrittenTotal++;
        }

        public void Clear()
        {
            if (_stream != IntPtr.Zero) SDL_ClearAudioStream(_stream);
            _framesQueued = 0;
            _playClock.Reset();   // re-baseline the estimate after a flush
            _drcStarted = false; _drcQueuedMsAvg = 0; _drcRatio = 1.0f; _drcLastAboveZero = true;
            if (_stream != IntPtr.Zero) SDL_SetAudioStreamFrequencyRatio(_stream, 1.0f);
        }

        public void Dispose()
        {
            if (_stream != IntPtr.Zero) { SDL_DestroyAudioStream(_stream); _stream = IntPtr.Zero; }
            SDL_QuitSubSystem(SDL_INIT_AUDIO);
        }
    }
}
