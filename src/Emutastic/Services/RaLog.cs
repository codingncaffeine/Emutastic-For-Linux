using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Append-only diagnostic log for the RetroAchievements data path (port of
    /// upstream RaLog). Routes to [DataRoot]/Logs/ra.log so users running the
    /// release build can still see what RA is doing when something looks wrong.
    /// Lock guards concurrent writes from UI thread + background fetch tasks.
    /// Never throws.
    /// </summary>
    public static class RaLog
    {
        private static readonly object _gate = new();
        private static string? _path;

        private static string Path
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    string dir = AppPaths.GetFolder("Logs");
                    _path = System.IO.Path.Combine(dir, "ra.log");
                }
                catch { _path = ""; }
                return _path!;
            }
        }

        public static void Write(string message)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
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
