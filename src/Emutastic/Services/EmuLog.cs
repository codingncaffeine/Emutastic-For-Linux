using System;
using System.Diagnostics;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Attaches a rotating file <see cref="TraceListener"/> so all <c>Trace.WriteLine</c> output
    /// (<c>[Emu]</c> / <c>[Gl]</c> / <c>[core:]</c> …) lands in Logs/. The in-app path sets this up in
    /// EmulatorWindow; the <c>--game-host</c> child has no Avalonia window, so it calls this directly.
    /// Give the child its own file so the parent and child don't interleave the same log.
    /// </summary>
    public static class EmuLog
    {
        /// <summary>Attach the listener for <paramref name="fileName"/> under Logs/ (rotates at 5 MB).
        /// Returns the listener so the caller can remove/flush it on shutdown, or null on failure.</summary>
        public static TextWriterTraceListener? Setup(string fileName)
        {
            try
            {
                string logDir = AppPaths.GetFolder("Logs");
                string logPath = Path.Combine(logDir, fileName);
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                    File.Move(logPath, Path.Combine(logDir, Path.GetFileNameWithoutExtension(fileName) + ".old.log"), overwrite: true);

                var listener = new TextWriterTraceListener(logPath, "EmuLog")
                {
                    TraceOutputOptions = TraceOptions.DateTime,
                };
                Trace.Listeners.Add(listener);
                Trace.AutoFlush = true;
                return listener;
            }
            catch { return null; }   // logging is best-effort
        }
    }
}
