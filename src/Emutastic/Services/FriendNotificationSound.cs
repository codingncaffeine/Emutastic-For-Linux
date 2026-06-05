using System;
using System.Diagnostics;
using System.IO;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    /// <summary>
    /// Plays the friend-notification sound (Assets/Sounds/Notification1.mp3)
    /// as a fire-and-forget one-shot (port of upstream FriendNotificationSound).
    /// Used for LB triumph toasts where the emotional weight warrants audio.
    ///
    /// Upstream used WPF MediaPlayer; on Linux we shell out to the first
    /// available player — ffplay (ships with the ffmpeg package the recording
    /// feature already requires), then pw-play / paplay (PipeWire / Pulse).
    /// Latest-wins: a new toast kills any overlapping playback, preventing
    /// pile-up, matching upstream's single-MediaPlayer semantics. The mp3 is
    /// shipped as a loose file next to the binary — external players can't
    /// read embedded AvaloniaResources.
    /// </summary>
    public static class FriendNotificationSound
    {
        private static readonly string _soundPath =
            Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "Notification1.mp3");

        private static Process? _activeSound;
        private static readonly object _gate = new();

        private static string? FindPlayer(out string argsTemplate)
        {
            foreach (var (exe, args) in new[]
            {
                ("ffplay", "-nodisp -autoexit -loglevel quiet \"{0}\""),
                ("pw-play", "\"{0}\""),
                ("paplay", "\"{0}\""),
            })
            {
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
                    if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, exe)))
                    { argsTemplate = args; return exe; }
            }
            argsTemplate = "";
            return null;
        }

        public static void Play(IConfigurationService? config)
        {
            try
            {
                var cfg = config?.GetFriendsConfiguration();
                if (cfg == null || !cfg.LbToastSoundEnabled)
                {
                    RaLog.Write($"[ToastSound] Play SKIPPED (cfgNull={cfg == null}, enabled={cfg?.LbToastSoundEnabled})");
                    return;
                }
                bool exists = File.Exists(_soundPath);
                string? player = FindPlayer(out string argsTemplate);
                RaLog.Write($"[ToastSound] Play path=[{_soundPath}] exists={exists} player={player ?? "<none>"}");
                if (!exists || player == null) return;

                lock (_gate)
                {
                    try { if (_activeSound is { HasExited: false }) _activeSound.Kill(); } catch { }
                    var psi = new ProcessStartInfo(player)
                    { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                    foreach (var part in string.Format(argsTemplate, _soundPath)
                                 .Split('"', StringSplitOptions.RemoveEmptyEntries))
                    {
                        foreach (var token in part == _soundPath ? new[] { part } : part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            psi.ArgumentList.Add(token);
                    }
                    _activeSound = Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                RaLog.Write($"[ToastSound] play failed: {ex.Message}");
            }
        }
    }
}
