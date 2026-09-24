using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Append-only diagnostic log for the in-app updater, the CloudSyncLog pattern:
    /// [DataRoot]/Logs/update.log records every check (install kind, endpoint, the
    /// version comparison, which asset was picked or why none was), every apply step
    /// (download size, digest verification, tar or dpkg exit codes with their output)
    /// and every failure with its exception. A user whose update "just failed" can
    /// read what happened, and a report can quote it.
    ///
    /// Each write also echoes to Trace with an [Update] prefix. Lock guards the UI
    /// thread and the startup probe writing at once. Never throws.
    /// </summary>
    public static class UpdateLog
    {
        private static readonly object _gate = new();
        private static string? _path;

        public static string Path
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    string dir = AppPaths.GetFolder("Logs");
                    _path = System.IO.Path.Combine(dir, "update.log");
                }
                catch { _path = ""; }
                return _path!;
            }
        }

        /// <summary>The log's location as a message to the user names it.</summary>
        public static string PathForDisplay => string.IsNullOrEmpty(Path) ? "Logs/update.log" : Path;

        public static void Write(string message)
        {
            try
            {
                System.Diagnostics.Trace.WriteLine($"[Update] {message}");
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                lock (_gate)
                {
                    if (string.IsNullOrEmpty(Path)) return;
                    LogRotation.RotateIfLarge(Path);
                    File.AppendAllText(Path, line);
                }
            }
            catch { /* never throw from logging */ }
        }
    }
}
