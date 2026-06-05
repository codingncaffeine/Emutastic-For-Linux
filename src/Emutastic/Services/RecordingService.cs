using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>Encode-time settings snapshot (from RecordingConfiguration at Start).</summary>
    public sealed class RecordingEncodeSettings
    {
        public string Quality { get; set; } = "High";        // Low / Medium / High / Lossless
        public int OutputScale { get; set; } = 2;            // 1..4 (neighbor upscale at encode)
        /// <summary>"Auto"/"x264" → libx264. "VAAPI" → hardware encode, explicit opt-in ONLY —
        /// never auto-selected: a wedged GPU video-encode ring hangs ffmpeg unkillably and can
        /// take the whole OS down (seen on the dev iMac, 2026-06-04). "NVENC" accepted, unavailable here.</summary>
        public string Encoder { get; set; } = "Auto";
        public bool HighChroma { get; set; } = false;        // yuv422p instead of yuv420p
        public int AudioBitrateKbps { get; set; } = 192;     // AAC, clamped 64..320
        public float DisplayAspectRatio { get; set; } = 0f;  // >0 → aspect-correct (CD-i half-height interlace)
    }

    /// <summary>
    /// Gameplay video capture — Linux port of upstream's ffmpeg RecordingService (the Windows
    /// build also had a WGC/MediaFoundation path; on Linux every core's frames already arrive
    /// as tightly-packed BGRA in the session, so the ffmpeg path covers everything).
    ///
    /// Two-phase design (upstream's): during play, raw frames + S16LE audio are appended to temp
    /// files by background writer threads — bounded queues that DROP rather than block, so the
    /// emu thread never waits on disk. On Stop, a background two-step ffmpeg pass encodes the
    /// video (quality/scale/encoder/chroma) then muxes AAC audio into the final .mp4.
    /// ffmpeg comes from PATH (no bundled binary on Linux; Debian ships 7.x).
    ///
    /// A .meta.json sidecar is written at Start and removed when the encode concludes, so a
    /// crash or hard power-off mid-recording/mid-encode leaves enough on disk for
    /// RecoverInterrupted() to finish the job at next launch.
    /// </summary>
    public sealed class RecordingService
    {
        // Dedicated recording-lifecycle log (upstream parity: Logs/recording_debug.log —
        // capture setup, encoding, teardown, recovery). Each write echoes to Trace with
        // the [Rec] prefix so the lines still interleave with core events in
        // emulator-host.log. Writers live in two processes (game-host records, the
        // library runs RecoverInterrupted) — the lock covers in-process concurrency,
        // a rare cross-process open collision just drops the file line (Trace still has it).
        private static readonly object _recLogGate = new();
        private static void RecLog(string msg)
        {
            try
            {
                Trace.WriteLine($"[Rec] {msg}");
                string path = Path.Combine(AppPaths.GetFolder("Logs"), "recording_debug.log");
                lock (_recLogGate)
                {
                    LogRotation.RotateIfLarge(path);
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { /* never throw from logging */ }
        }

        private const int FramePoolSize = 6;

        private string _outputPath = "";
        private string _videoRawPath = "", _audioRawPath = "";
        private FileStream? _videoTemp, _audioTemp;
        private BlockingCollection<(byte[] buf, int len)>? _videoQueue;
        private BlockingCollection<(byte[] buf, int len, bool rented)>? _audioQueue;
        private Thread? _videoWriter, _audioWriter;
        private ConcurrentBag<byte[]>? _framePool;
        private int _frameBytes;
        private int _width, _height, _sampleRate;
        private double _fps;
        private RecordingEncodeSettings _settings = new();
        private Action<string?>? _onComplete;
        private readonly Stopwatch _elapsed = new();
        private long _framesWritten, _framesDropped;

        public bool IsRecording { get; private set; }
        public TimeSpan Elapsed => _elapsed.Elapsed;

        /// <summary>The in-flight background encode once Stop() has run. The game-host waits on
        /// this before process exit so closing the game mid-recording still produces the .mp4.</summary>
        public Task? EncodeTask { get; private set; }

        /// <summary>Everything RecoverInterrupted needs to finish an orphaned encode.</summary>
        private sealed class RecordingSidecar
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public double Fps { get; set; }
            public int SampleRate { get; set; }
            public RecordingEncodeSettings Settings { get; set; } = new();
        }

        // A VAAPI submission to a broken GPU hangs in the KERNEL — no userspace timeout or
        // Kill() can save us, and amdgpu's eventual reset attempt can freeze the whole OS
        // (it did, twice, on the dev iMac's SMU-wedged card — see gpu-clocks-frozen notes).
        // So: the marker is written before every VAAPI attempt and removed only on success.
        // A hard freeze leaves it behind, permanently blacklisting VAAPI on this machine.
        private static string VaapiMarkerPath => Path.Combine(AppPaths.DataRoot, "vaapi-encode-unsafe.marker");

        public static string? FindFfmpeg()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string p = Path.Combine(dir, "ffmpeg");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>Begin capturing. Returns null on success, else a user-facing error string.</summary>
        public string? Start(string outputPath, int width, int height, double fps, int sampleRate,
                             Action<string?> onEncodeComplete, RecordingEncodeSettings settings)
        {
            if (IsRecording) return "Already recording";
            if (FindFfmpeg() == null) return "ffmpeg not found — install it (e.g. apt install ffmpeg)";
            if (width <= 0 || height <= 0) return "No frame yet — try again once the game is rendering";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                _outputPath = outputPath;
                _videoRawPath = outputPath + ".video.raw";
                _audioRawPath = outputPath + ".audio.raw";
                _videoTemp = new FileStream(_videoRawPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 << 20);
                _audioTemp = new FileStream(_audioRawPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 << 10);
            }
            catch (Exception ex) { Cleanup(); return $"Could not create recording files: {ex.Message}"; }

            _width = width; _height = height; _fps = fps; _sampleRate = sampleRate;
            _settings = settings; _onComplete = onEncodeComplete;

            // Sidecar so RecoverInterrupted can finish this encode if we die mid-way. Best-effort.
            try
            {
                var sc = new RecordingSidecar { Width = width, Height = height, Fps = fps, SampleRate = sampleRate, Settings = settings };
                File.WriteAllText(outputPath + ".meta.json", JsonSerializer.Serialize(sc));
            }
            catch (Exception ex) { RecLog($"sidecar write failed: {ex.Message}"); }

            _frameBytes = width * height * 4;   // session frames are always tightly-packed BGRA
            _framePool = new ConcurrentBag<byte[]>();
            for (int i = 0; i < FramePoolSize; i++) _framePool.Add(new byte[_frameBytes]);
            _videoQueue = new BlockingCollection<(byte[], int)>(FramePoolSize);
            _audioQueue = new BlockingCollection<(byte[], int, bool)>(500);
            _framesWritten = 0; _framesDropped = 0;

            _videoWriter = new Thread(VideoWriterLoop) { IsBackground = true, Name = "RecVideoWriter" };
            _audioWriter = new Thread(AudioWriterLoop) { IsBackground = true, Name = "RecAudioWriter" };
            _videoWriter.Start(); _audioWriter.Start();
            _elapsed.Restart();
            IsRecording = true;
            RecLog($"started {width}x{height}@{fps:F2} sr={sampleRate} → {outputPath}");
            return null;
        }

        /// <summary>Emu thread: queue one finished BGRA frame. Never blocks; drops when behind
        /// or when the frame size changed mid-recording (prevents desync, upstream behavior).</summary>
        public void QueueVideoFrame(byte[] bgra, int length)
        {
            var q = _videoQueue;
            var pool = _framePool;
            if (!IsRecording || q == null || pool == null) return;
            if (length != _frameBytes) { _framesDropped++; return; }   // resolution changed mid-record
            if (!pool.TryTake(out var buf)) { _framesDropped++; return; }  // writers behind — drop
            Buffer.BlockCopy(bgra, 0, buf, 0, length);
            if (!q.TryAdd((buf, length))) { pool.Add(buf); _framesDropped++; }
        }

        /// <summary>Audio thread/callback: queue raw S16LE stereo bytes (the core's true rate,
        /// pre-DRC — DRC only nudges the playback stream). Never blocks.</summary>
        public void QueueAudioSamples(byte[] samples, int length)
        {
            var q = _audioQueue;
            if (!IsRecording || q == null || length <= 0) return;
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(samples, 0, rented, 0, length);
            if (!q.TryAdd((rented, length, true))) ArrayPool<byte>.Shared.Return(rented);
        }

        private void VideoWriterLoop()
        {
            try
            {
                foreach (var (buf, len) in _videoQueue!.GetConsumingEnumerable())
                {
                    _videoTemp!.Write(buf, 0, len);
                    Interlocked.Increment(ref _framesWritten);
                    _framePool!.Add(buf);
                }
            }
            catch (Exception ex) { RecLog($"video writer ended: {ex.Message}"); }
        }

        private void AudioWriterLoop()
        {
            try
            {
                foreach (var (buf, len, rented) in _audioQueue!.GetConsumingEnumerable())
                {
                    _audioTemp!.Write(buf, 0, len);
                    if (rented) ArrayPool<byte>.Shared.Return(buf);
                }
            }
            catch (Exception ex) { RecLog($"audio writer ended: {ex.Message}"); }
        }

        /// <summary>Stop capturing and kick off the background encode (onComplete fires when done).</summary>
        public void Stop()
        {
            if (!IsRecording) return;
            IsRecording = false;
            _elapsed.Stop();
            _videoQueue?.CompleteAdding();
            _audioQueue?.CompleteAdding();
            _videoWriter?.Join(5000);
            _audioWriter?.Join(5000);
            try { _videoTemp?.Flush(); _videoTemp?.Dispose(); } catch { }
            try { _audioTemp?.Flush(); _audioTemp?.Dispose(); } catch { }
            _videoTemp = null; _audioTemp = null;
            RecLog($"stopped after {_elapsed.Elapsed:mm\\:ss} frames={_framesWritten} dropped={_framesDropped}");
            EncodeTask = Task.Run(EncodeAndMux);
        }

        private void EncodeAndMux()
        {
            string? error = EncodeAndMuxCore(_outputPath, _videoRawPath, _audioRawPath,
                                             _width, _height, _fps, _sampleRate, _settings);
            Cleanup();
            _onComplete?.Invoke(error);
        }

        /// <summary>
        /// Finish any encode that a crash or hard power-off interrupted: every *.meta.json left
        /// in the recordings tree whose raw video still exists gets encoded now — forced x264,
        /// since the interruption may well have BEEN a hardware-encoder wedge. Called at app
        /// startup AND after every game-host exit (so a host that died mid-recording still
        /// yields its .mp4 immediately, not at next launch). Off the UI thread.
        /// </summary>
        private static int _sweeping;
        public static void RecoverInterrupted()
        {
            if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;   // one sweep at a time
            try
            {
                if (FindFfmpeg() == null) return;
                string root = AppPaths.GetFolder("Recordings");
                foreach (string metaPath in Directory.EnumerateFiles(root, "*.meta.json", SearchOption.AllDirectories))
                {
                    string outputPath = metaPath.Substring(0, metaPath.Length - ".meta.json".Length);
                    string videoRaw = outputPath + ".video.raw";
                    string audioRaw = outputPath + ".audio.raw";
                    try
                    {
                        // A fresh .enc.mp4 means an encode is running on these files RIGHT NOW
                        // (e.g. an ffmpeg orphaned by a killed host, or another process's sweep) —
                        // leave it alone; a later sweep mops up if it never finishes.
                        string tempMp4 = outputPath + ".enc.mp4";
                        if (File.Exists(tempMp4) &&
                            DateTime.UtcNow - File.GetLastWriteTimeUtc(tempMp4) < TimeSpan.FromSeconds(60))
                        {
                            RecLog($"recovery skipped (encode appears active): {outputPath}");
                            continue;
                        }
                        if (!File.Exists(videoRaw) || new FileInfo(videoRaw).Length == 0)
                        {
                            // Nothing salvageable — clear the leftovers.
                            File.Delete(metaPath);
                            try { File.Delete(videoRaw); } catch { }
                            try { File.Delete(audioRaw); } catch { }
                            continue;
                        }
                        var sc = JsonSerializer.Deserialize<RecordingSidecar>(File.ReadAllText(metaPath));
                        if (sc == null || sc.Width <= 0 || sc.Height <= 0) { File.Delete(metaPath); continue; }
                        sc.Settings.Encoder = "x264";
                        RecLog($"recovering interrupted recording: {outputPath}");
                        string? err = EncodeAndMuxCore(outputPath, videoRaw, audioRaw,
                                                       sc.Width, sc.Height, sc.Fps, sc.SampleRate, sc.Settings);
                        RecLog(err == null ? $"recovered {outputPath}" : $"recovery failed: {err}");
                    }
                    catch (Exception ex) { RecLog($"recovery skipped {metaPath}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { RecLog($"recovery sweep failed: {ex.Message}"); }
            finally { Volatile.Write(ref _sweeping, 0); }
        }

        private static string? EncodeAndMuxCore(string outputPath, string videoRawPath, string audioRawPath,
                                                int width, int height, double fps, int sampleRate,
                                                RecordingEncodeSettings settings)
        {
            string tempMp4 = outputPath + ".enc.mp4";
            string sidecar = outputPath + ".meta.json";
            string? error = null;
            try
            {
                string ffmpeg = FindFfmpeg() ?? "ffmpeg";

                // Lossless / HighChroma force software x264 — VAAPI can't do yuv444p/yuv422p export
                // (same rule as upstream forcing x264 over NVENC for these).
                bool lossless = settings.Quality.Equals("Lossless", StringComparison.OrdinalIgnoreCase);

                // VAAPI is explicit opt-in ONLY ("Auto" = x264): the encoders probe can't tell us
                // whether the GPU's encode ring actually works, and a submission to a broken one
                // hangs in the kernel where no timeout reaches it. The marker blacklists VAAPI
                // forever after a single mid-encode death (including death-by-OS-freeze).
                bool vaapiRequested = settings.Encoder.Equals("VAAPI", StringComparison.OrdinalIgnoreCase);
                bool vaapiBlocked = vaapiRequested && File.Exists(VaapiMarkerPath);
                if (vaapiBlocked)
                    RecLog("VAAPI requested but blacklisted by an earlier mid-encode failure — using x264");
                bool useVaapi = vaapiRequested && !vaapiBlocked && !lossless && !settings.HighChroma
                    && File.Exists("/dev/dri/renderD128") && ProbeEncoder(ffmpeg, "h264_vaapi");

                // Quality table — upstream's CRF/QP/preset mapping verbatim.
                (int crf, int qp, string x264Preset) = settings.Quality switch
                {
                    "Low"      => (23, 26, "veryfast"),
                    "Medium"   => (20, 22, "fast"),
                    "Lossless" => (0, 0, "veryslow"),
                    _          => (16, 19, "medium"),   // High
                };
                string pixFmtOut = lossless ? "yuv444p" : settings.HighChroma ? "yuv422p" : "yuv420p";

                // Target size: uniform integer scale, except aspect-correct when the reported display
                // aspect diverges >40% from the buffer aspect (CD-i half-height interlace).
                int scale = Math.Clamp(settings.OutputScale, 1, 4);
                int targetH = height * scale;
                int targetW = width * scale;
                if (settings.DisplayAspectRatio > 0)
                {
                    float bufAspect = width / (float)height;
                    float ratio = settings.DisplayAspectRatio / bufAspect;
                    if (ratio > 1.4f || ratio < 1f / 1.4f)
                        targetW = (int)Math.Round(targetH * settings.DisplayAspectRatio);
                }
                targetW &= ~1; targetH &= ~1;   // H.264 wants even dimensions

                // STEP 1: encode raw video → temp mp4 (no audio).
                string fpsArg = fps.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                string vf = useVaapi
                    ? $"scale={targetW}:{targetH}:flags=neighbor,format=nv12,hwupload"
                    : $"scale={targetW}:{targetH}:flags=neighbor";
                string codec = useVaapi
                    ? $"-c:v h264_vaapi -qp {qp}"
                    : $"-c:v libx264 -preset {x264Preset} " + (lossless ? "-qp 0" : $"-crf {crf}") + $" -pix_fmt {pixFmtOut}";
                string vaapiDev = useVaapi ? "-vaapi_device /dev/dri/renderD128 " : "";
                string step1 = $"-y {vaapiDev}-f rawvideo -pixel_format bgra -video_size {width}x{height} " +
                               $"-framerate {fpsArg} -i \"{videoRawPath}\" -sws_flags neighbor -vf \"{vf}\" {codec} -an \"{tempMp4}\"";
                RecLog($"encode ({(useVaapi ? "VAAPI" : "x264")}): ffmpeg {step1}");

                bool step1Ok;
                if (useVaapi)
                {
                    try { File.WriteAllText(VaapiMarkerPath, "VAAPI encode in flight — if this file survives, the attempt died mid-run.\n"); } catch { }
                    step1Ok = RunFfmpeg(ffmpeg, step1, 300_000);
                    if (step1Ok) { try { File.Delete(VaapiMarkerPath); } catch { } }
                }
                else
                {
                    step1Ok = RunFfmpeg(ffmpeg, step1, 300_000);
                }

                if (!step1Ok)
                {
                    if (useVaapi)
                    {
                        // VAAPI can fail driver-side — retry on x264 rather than losing the recording.
                        RecLog("VAAPI encode failed — retrying with libx264 (VAAPI now blacklisted)");
                        string sw = $"-y -f rawvideo -pixel_format bgra -video_size {width}x{height} " +
                                    $"-framerate {fpsArg} -i \"{videoRawPath}\" -sws_flags neighbor " +
                                    $"-vf \"scale={targetW}:{targetH}:flags=neighbor\" -c:v libx264 -preset {x264Preset} -crf {crf} -pix_fmt {pixFmtOut} -an \"{tempMp4}\"";
                        if (!RunFfmpeg(ffmpeg, sw, 300_000)) { error = "Video encode failed (see emulator-host.log)"; return error; }
                    }
                    else { error = "Video encode failed (see emulator-host.log)"; return error; }
                }

                // STEP 2: mux video + AAC audio (explicit -map keeps audio on exotic H.264 profiles).
                if (sampleRate > 0 && File.Exists(audioRawPath) && new FileInfo(audioRawPath).Length > 0)
                {
                    int kbps = Math.Clamp(settings.AudioBitrateKbps, 64, 320);
                    string step2 = $"-y -i \"{tempMp4}\" -f s16le -ar {sampleRate} -ac 2 -i \"{audioRawPath}\" " +
                                   $"-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a {kbps}k -shortest \"{outputPath}\"";
                    if (!RunFfmpeg(ffmpeg, step2, 60_000)) { error = "Audio mux failed (see emulator-host.log)"; return error; }
                }
                else
                {
                    File.Move(tempMp4, outputPath, overwrite: true);
                }
                RecLog($"saved {outputPath}");
            }
            catch (Exception ex)
            {
                error = $"Encode failed: {ex.Message}";
                RecLog($"EncodeAndMux: {ex}");
            }
            finally
            {
                try { File.Delete(videoRawPath); } catch { }
                try { File.Delete(audioRawPath); } catch { }
                try { File.Delete(sidecar); } catch { }
                try { if (File.Exists(tempMp4)) File.Delete(tempMp4); } catch { }
            }
            return error;
        }

        private static bool ProbeEncoder(string ffmpeg, string name)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -encoders")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10_000);
                return output.Contains(name);
            }
            catch { return false; }
        }

        private static bool RunFfmpeg(string ffmpeg, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpeg, args)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                // Encodes can run while gameplay continues — keep them off the emu thread's cores.
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                // Drain stderr so a chatty encode can't fill the pipe and deadlock; keep the tail for logs.
                string tail = "";
                var errTask = Task.Run(() =>
                {
                    string? line;
                    while ((line = p.StandardError.ReadLine()) != null) tail = line;
                });
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } RecLog("ffmpeg timed out"); return false; }
                errTask.Wait(2000);
                if (p.ExitCode != 0) RecLog($"ffmpeg exit={p.ExitCode}: {tail}");
                return p.ExitCode == 0;
            }
            catch (Exception ex) { RecLog($"ffmpeg launch failed: {ex.Message}"); return false; }
        }

        private void Cleanup()
        {
            try { _videoTemp?.Dispose(); } catch { }
            try { _audioTemp?.Dispose(); } catch { }
            _videoTemp = null; _audioTemp = null;
            _videoQueue?.Dispose(); _audioQueue?.Dispose();
            _videoQueue = null; _audioQueue = null;
            _framePool = null;
        }
    }
}
