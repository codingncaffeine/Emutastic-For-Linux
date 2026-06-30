using System;

namespace Emutastic.Services
{
    /// <summary>
    /// Dedicated save-state diagnostic log → [DataRoot]/Logs/savestate-diag.log. Traces the EmuTV
    /// save-state overlay path (load list from DB, enter overlay/reparent, load-on-accept) so a
    /// "states don't show / won't load" report can be diagnosed from the file alone. Mirrors
    /// ControllerDiagLog: direct File.AppendAllText, locked, never throws.
    /// </summary>
    public static class SaveStateDiagLog
    {
        private static readonly object _gate = new();

        public static void Write(string msg)
        {
            try
            {
                lock (_gate)
                {
                    string path = System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "savestate-diag.log");
                    LogRotation.RotateIfLarge(path);
                    System.IO.File.AppendAllText(path,
                        $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { /* never throw from logging */ }
        }
    }
}
