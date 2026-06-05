using System.Diagnostics;

namespace Emutastic.Services
{
    /// <summary>
    /// Opens a file, folder, or URL with the desktop's default handler (xdg-open).
    /// Always pass the target via ArgumentList — the Process.Start(file, arguments)
    /// overload treats the second string as a RAW argument line, so a path with spaces
    /// ("Manuals/NES/Super Mario Bros [811b027e]/manual.pdf") got split into four
    /// arguments and xdg-open silently opened nothing.
    /// </summary>
    public static class ShellOpen
    {
        public static void Open(string target)
        {
            try
            {
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(target);
                Process.Start(psi);
            }
            catch (System.Exception ex) { Trace.WriteLine($"[ShellOpen] {target}: {ex.Message}"); }
        }
    }
}
