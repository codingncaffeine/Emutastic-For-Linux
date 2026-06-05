using System;

namespace Emutastic.Services
{
    /// <summary>
    /// Dedicated controller-diagnostic log (upstream parity: controller-diag.log —
    /// detection + hot-plug events and what each device identified as). Upstream
    /// hardcodes it next to the exe; on Linux that's read-only for .deb installs
    /// (/usr/lib), so it lives with the other logs in [DataRoot]/Logs/.
    /// Direct File.AppendAllText — no TraceListener indirection, nothing to
    /// misconfigure or fail silently. Two writer processes (the library's
    /// ControllerManager and the game-host's SdlInput) — the lock covers
    /// in-process concurrency; a rare cross-process open collision just drops
    /// that line. Never throws.
    /// </summary>
    public static class ControllerDiagLog
    {
        private static readonly object _gate = new();

        public static void Write(string msg)
        {
            try
            {
                lock (_gate)
                {
                    string path = System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "controller-diag.log");
                    LogRotation.RotateIfLarge(path);
                    System.IO.File.AppendAllText(path,
                        $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { /* never throw from logging */ }
        }
    }
}
