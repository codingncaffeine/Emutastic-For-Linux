using System;
using System.Diagnostics;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace Emutastic.Services
{
    /// <summary>
    /// Owns the process-wide <see cref="LibVLC"/> instance used by snap-video previews
    /// in the game detail window. First construction is multi-second (native init:
    /// libvlc.so load + plugin scan), so this service kicks the warm-up off the UI
    /// thread at app startup; subsequent callers await an already-completed Task.
    /// Port of the upstream service — Core.Initialize() is added for Linux; LibVLCSharp
    /// resolves the system libvlc (libvlc.so.5 from the libvlc5 package). The .deb must
    /// therefore Depend on libvlc5 + vlc-plugin-base (MP4/h264 demux+decode for snaps).
    /// </summary>
    internal sealed class VideoPlaybackService
    {
        public static VideoPlaybackService Instance { get; } = new();

        private Task<LibVLC>? _warmup;
        private readonly object _gate = new();

        private VideoPlaybackService() { }

        public void StartWarmup() => _ = GetLibVLCAsync();

        public Task<LibVLC> GetLibVLCAsync()
        {
            if (_warmup != null) return _warmup;
            lock (_gate)
            {
                _warmup ??= Task.Run(CreateLibVLC);
            }
            return _warmup;
        }

        private static LibVLC CreateLibVLC()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Core.Initialize();   // resolves the native libvlc (no-op past the first call)
                // --avcodec-hw=none: force software decode. VLC's default ("any") picks
                // VAAPI on AMD, which submits to the GPU's UVD block — on this machine the
                // first UVD job hung the ring and the wedged SMU made the GPU reset
                // unrecoverable (hard OS freeze, 2026-06-05). Decode-side twin of the
                // h264_vaapi encode freeze (40100d4). Snaps are tiny clips; CPU decode is free.
                // --quiet: snaps stopped mid-decode (hover moves off a card) spam h264 'no frame!' +
                // 'Failed to create video converter' to stderr — harmless, but it buries real output
                // when running from a terminal.
                LibVLC lib;
                try
                {
                    lib = new LibVLC("--no-audio", "--no-osd", "--no-snapshot-preview", "--avcodec-hw=none", "--quiet");
                }
                catch (VLCException)
                {
                    // --avcodec-hw belongs to the avcodec *plugin*, and an unknown option fails
                    // the whole libvlc_new. Distros that split VLC into per-plugin packages
                    // (Arch: vlc-plugin-ffmpeg) may not have it installed — retry without the
                    // flag so snaps still work for whatever codecs are present. h264 snaps need
                    // the avcodec plugin anyway, so dropping its option loses nothing here; if
                    // the plugin appears later, the no-flag instance risks VAAPI decode (see
                    // hang note above) — hence the loud log.
                    Trace.WriteLine("[VideoPlayback] LibVLC rejected --avcodec-hw=none (avcodec " +
                                    "plugin missing? Arch/Fedora: install vlc-plugin-ffmpeg) — retrying without it");
                    lib = new LibVLC("--no-audio", "--no-osd", "--no-snapshot-preview", "--quiet");
                }
                sw.Stop();
                Trace.WriteLine($"[VideoPlayback] LibVLC warmed in {sw.ElapsedMilliseconds}ms");
                return lib;
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Surface failures — StartWarmup is fire-and-forget, so the faulted Task
                // is otherwise invisible; snap callers swallow their await in LoadSnapAsync.
                Trace.WriteLine($"[VideoPlayback] LibVLC init FAILED after {sw.ElapsedMilliseconds}ms: {ex.Message}");
                throw;
            }
        }
    }
}
