using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Append-only crash/exception log → <c>crash.log</c>. Fed by the global handlers in
    /// <c>App.OnFrameworkInitializationCompleted</c> (background <see cref="AppDomain.UnhandledException"/>,
    /// unobserved <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>, and the
    /// Avalonia <c>Dispatcher.UnhandledException</c>). Upstream routed these through an ILogger; we keep
    /// the same file-based posture as <see cref="StartupTrace"/> so there are no extra dependencies.
    /// Locked and never throws — a logging failure must not mask the original crash.
    /// </summary>
    internal static class CrashLog
    {
        private static string? _logPath;
        private static readonly object _gate = new();

        public static void Write(string source, Exception? ex)
            => Write(source, ex?.ToString() ?? "(no exception object)");

        public static void Write(string source, string detail)
        {
            lock (_gate)
            {
                try
                {
                    _logPath ??= Path.Combine(AppPaths.GetFolder("Logs"), "crash.log");
                    LogRotation.RotateIfLarge(_logPath);
                    File.AppendAllText(_logPath,
                        $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{source}] ==={Environment.NewLine}" +
                        $"{detail}{Environment.NewLine}{Environment.NewLine}");
                }
                catch { /* diagnostic — never throw */ }

                // Also surface to the Trace listeners (stderr) so it's visible when run from a terminal.
                try { System.Diagnostics.Trace.WriteLine($"CRASH [{source}]: {detail}"); } catch { }
            }
        }
    }
}
