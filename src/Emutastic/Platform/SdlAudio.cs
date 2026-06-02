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

        /// <summary>Queue a batch of interleaved S16 stereo samples (libretro audio_sample_batch).</summary>
        public void QueueBatch(IntPtr data, int frames)
        {
            if (_stream == IntPtr.Zero || data == IntPtr.Zero || frames <= 0) return;
            SDL_PutAudioStreamData(_stream, data, frames * 4); // 2 channels * 2 bytes
            if (!_playClock.IsRunning) _playClock.Start();     // playback clock starts at first audio
            _framesQueued += frames;
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
        }

        public void Clear()
        {
            if (_stream != IntPtr.Zero) SDL_ClearAudioStream(_stream);
            _framesQueued = 0;
            _playClock.Reset();   // re-baseline the estimate after a flush
        }

        public void Dispose()
        {
            if (_stream != IntPtr.Zero) { SDL_DestroyAudioStream(_stream); _stream = IntPtr.Zero; }
            SDL_QuitSubSystem(SDL_INIT_AUDIO);
        }
    }
}
