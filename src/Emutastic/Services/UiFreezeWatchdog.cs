using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Emutastic.Services
{
    /// <summary>
    /// Diagnostic background watchdog that detects UI dispatcher stalls and logs each freeze
    /// (duration + active window at freeze start) to <c>ui_freezes.log</c>. Pair with
    /// <see cref="StartupTrace"/> to attribute a freeze to a specific startup phase.
    ///
    /// Mechanism: a background thread posts a <see cref="DispatcherPriority.Input"/> ping each
    /// iteration and waits for it to run on the UI thread. If the ping doesn't complete within
    /// <see cref="FreezeThresholdMs"/>, a freeze is in progress; the watchdog keeps polling until the
    /// ping drains, then logs the total stall. Input priority reflects "is the UI responsive to the
    /// user" — Background would sit behind legitimate layout/render and produce false positives.
    ///
    /// Ported from the upstream WPF build. WPF→Avalonia changes: Avalonia <see cref="Dispatcher"/>;
    /// the internal <c>HasShutdownStarted</c> is replaced by a try/catch around <c>Post</c> + an
    /// explicit <see cref="Stop"/> on app close. Diagnostic-only — must never itself stall the UI.
    /// </summary>
    internal sealed class UiFreezeWatchdog
    {
        private const int FreezeThresholdMs = 500;
        private const int PollIntervalMs    = 100;
        // Cap how long we wait for a stuck ping to drain (~60s). After a dispatcher shutdown the ping
        // can never complete (Post silently no-ops), so without this the thread would spin forever if
        // Stop() were ever missed.
        private const int MaxDrainPolls     = 600;

        public static UiFreezeWatchdog Instance { get; } = new();

        private Dispatcher? _dispatcher;
        private Thread?     _thread;
        private string?     _logPath;
        private volatile bool _stopRequested;

        private readonly Stopwatch _sw = Stopwatch.StartNew();

        // Set on the UI thread inside the ping callback; read by the watchdog when a freeze is
        // detected — at that moment it holds the title captured by the LAST successful ping, i.e.
        // "active window when the freeze began."
        private volatile string _currentWindowTitle = "(none)";

        // UI thread writes via Interlocked.Exchange; watchdog reads via Interlocked.Read. Stopwatch
        // elapsed-ms at the moment the most recent ping ran on the dispatcher.
        private long _lastPingCompletedMs;

        private UiFreezeWatchdog() { }

        public void Start(Dispatcher dispatcher)
        {
            if (_thread != null) return;
            _dispatcher = dispatcher;
            try
            {
                _logPath = Path.Combine(AppPaths.GetFolder("Logs"), "ui_freezes.log");
                LogRotation.RotateIfLarge(_logPath);
                File.AppendAllText(_logPath,
                    $"=== Watchdog start {DateTime.Now:yyyy-MM-dd HH:mm:ss} (threshold {FreezeThresholdMs}ms) ==={Environment.NewLine}");
            }
            catch { /* keep the thread alive even if the first write fails */ }

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "UiFreezeWatchdog",
                Priority = ThreadPriority.AboveNormal, // small bump so OS scheduling doesn't add fake latency
            };
            _thread.Start();
        }

        public void Stop() => _stopRequested = true;

        private void Loop()
        {
            while (!_stopRequested)
            {
                try { RunOneIteration(); }
                catch { /* never let this thread die — diagnostics are best-effort */ }
            }
        }

        private void RunOneIteration()
        {
            var disp = _dispatcher;
            if (disp == null)
            {
                Thread.Sleep(500);
                return;
            }

            long pingPostedMs = _sw.ElapsedMilliseconds;

            // Guard Post defensively. NOTE: in Avalonia, Post does NOT throw after shutdown — it
            // silently no-ops and the ping never runs (HasShutdownStarted is internal, so we can't
            // pre-check it). So _stopRequested (set via Stop() on app exit) plus the bounded drain
            // loop below — not this catch — are what keep this thread from spinning post-shutdown.
            try
            {
                disp.Post(() =>
                {
                    Interlocked.Exchange(ref _lastPingCompletedMs, _sw.ElapsedMilliseconds);
                    UpdateActiveWindowSnapshot();
                }, DispatcherPriority.Input);
            }
            catch { Thread.Sleep(500); return; }

            // Poll until the ping completes or the threshold expires.
            while (!_stopRequested)
            {
                Thread.Sleep(PollIntervalMs);

                long completedMs = Interlocked.Read(ref _lastPingCompletedMs);
                if (completedMs >= pingPostedMs) return;        // healthy round-trip

                long ageMs = _sw.ElapsedMilliseconds - pingPostedMs;
                if (ageMs < FreezeThresholdMs) continue;

                // ── Freeze detected ──
                string atWindow = _currentWindowTitle;
                long freezeStartMs = pingPostedMs;

                // Wait for the stuck ping to drain — don't spam extra pings into the queue. Bounded by
                // MaxDrainPolls so a never-arriving ping (dispatcher shut down) can't pin this thread.
                bool drained = false;
                for (int p = 0; !_stopRequested && p < MaxDrainPolls; p++)
                {
                    Thread.Sleep(PollIntervalMs);
                    completedMs = Interlocked.Read(ref _lastPingCompletedMs);
                    if (completedMs >= pingPostedMs) { drained = true; break; }
                }

                // Only log a real, completed freeze. Exiting because we were told to stop (app closing)
                // or because the cap expired (dispatcher gone) would log a bogus/negative duration.
                if (!drained) return;

                long durMs = completedMs - freezeStartMs;
                AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FREEZE {durMs}ms  window={atWindow}");
                return;
            }
        }

        private void UpdateActiveWindowSnapshot()
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life)
                    return;
                var active = life.Windows.FirstOrDefault(w => w.IsActive) ?? life.MainWindow;
                _currentWindowTitle = active != null
                    ? $"{active.GetType().Name}(\"{active.Title}\")"
                    : "(none)";
            }
            catch { }
        }

        private void AppendLog(string line)
        {
            try
            {
                _logPath ??= Path.Combine(AppPaths.GetFolder("Logs"), "ui_freezes.log");
                // Rotate here too, not just at Start(): a long session with a
                // freeze storm would otherwise grow the file unbounded until
                // the next launch.
                LogRotation.RotateIfLarge(_logPath);
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
