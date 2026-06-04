using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Emutastic.Configuration;
using Emutastic.Services.Archives;

namespace Emutastic.Services
{
    /// <summary>
    /// Extracts the inner ROM from a single-game .zip/.7z/etc. archive into the
    /// app's ExtractedRoms folder. Used by both the importer (so the DB stores the
    /// real path) and the launcher (defensive backstop for DB rows where the .zip
    /// path slipped through, e.g. when imported via the console-nav hint flow before
    /// the hint paths learned to extract).
    /// </summary>
    public static class ZipRomExtractor
    {
        // Consoles whose cores read the archive natively — never extract these.
        // Arcade/NeoGeo ROMs are multi-file chip dumps that must stay as zips.
        private static readonly HashSet<string> ArchiveNativeConsoles =
            new(StringComparer.OrdinalIgnoreCase) { "Arcade", "NeoGeo" };

        public static bool ConsoleNeedsExtraction(string console) =>
            !string.IsNullOrEmpty(console) && !ArchiveNativeConsoles.Contains(console);

        public static bool IsArchiveExtension(string ext) =>
            ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".7z",  StringComparison.OrdinalIgnoreCase);

        public static async Task<string?> ExtractAsync(string archivePath, string console)
        {
            try
            {
                string outputDir = AppPaths.GetFolder("ExtractedRoms", console);
                using var archive = RomArchive.Open(archivePath);

                IRomArchiveEntry? romEntry = null;
                int romCount = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    string innerExt = Path.GetExtension(entry.Key ?? string.Empty);
                    if (RomService.IsRomExtension(innerExt))
                    {
                        romCount++;
                        if (romCount == 1) romEntry = entry;
                    }
                }
                if (romCount != 1 || romEntry == null) return null;

                string outputPath = Path.Combine(outputDir, Path.GetFileName(romEntry.Key!));
                string tmpPath    = outputPath + ".tmp";

                // Fast-path: if the existing file matches the entry size, reuse it.
                // SevenZipExtractor reports Size <= 0 for some formats — skip fast-path then.
                if (romEntry.Size > 0
                    && File.Exists(outputPath)
                    && new FileInfo(outputPath).Length == romEntry.Size)
                    return outputPath;

                if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }

                // Native 7z first for .7z: SharpCompress's managed LZMA decoder is
                // single-threaded and several times slower than p7zip — on multi-GB
                // PSP/GC ISOs that's the difference between ~20 s and minutes per
                // game (upstream never sees this because SevenZipExtractor wraps the
                // native 7z.dll). Falls back to the managed path when the binary is
                // missing or the entry fails to stream.
                bool extracted = false;
                if (Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
                    extracted = await TryNative7zExtractAsync(archivePath, romEntry.Key!, tmpPath, romEntry.Size);

                if (!extracted)
                {
                    // Stream directly to disk via ExtractTo — avoids buffering large
                    // ROM ISOs (PSP/GC/Wii images can be multiple GB) in memory.
                    using var outputStream = File.Create(tmpPath);
                    romEntry.ExtractTo(outputStream);
                }

                if (File.Exists(outputPath)) try { File.Delete(outputPath); } catch { }
                File.Move(tmpPath, outputPath);

                return outputPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"ZipRomExtractor: extract failed for {archivePath}: {ex.Message}");
                return null;
            }
        }

        public static string? ExtractSync(string archivePath, string console)
            => ExtractAsync(archivePath, console).GetAwaiter().GetResult();

        // Resolved once: first available native 7z binary, or null → managed fallback.
        private static readonly string? _native7z = FindNative7z();
        private static string? FindNative7z()
        {
            foreach (string name in new[] { "7z", "7zz", "7zr" })
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        /// <summary>
        /// Extract a single entry with the system 7z binary, streaming stdout straight
        /// to <paramref name="tmpPath"/>. Returns false (after cleaning up) on any
        /// failure so the caller can fall back to the managed extractor.
        /// </summary>
        private static async Task<bool> TryNative7zExtractAsync(
            string archivePath, string entryKey, string tmpPath, long expectedSize)
        {
            if (_native7z == null) return false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _native7z,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("e");      // extract without paths
                psi.ArgumentList.Add("-so");    // stream entry to stdout
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(archivePath);
                psi.ArgumentList.Add(entryKey);

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                // Drain stderr concurrently so a chatty 7z can't deadlock the pipe.
                var errTask = proc.StandardError.ReadToEndAsync();
                await using (var output = File.Create(tmpPath))
                    await proc.StandardOutput.BaseStream.CopyToAsync(output);
                await proc.WaitForExitAsync();
                await errTask;

                long written = new FileInfo(tmpPath).Length;
                bool ok = proc.ExitCode == 0 && written > 0
                          && (expectedSize <= 0 || written == expectedSize);
                if (!ok)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"ZipRomExtractor: native 7z failed for {archivePath} (exit={proc.ExitCode}, wrote {written}/{expectedSize}) — falling back to managed");
                    try { File.Delete(tmpPath); } catch { }
                }
                return ok;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"ZipRomExtractor: native 7z unavailable ({ex.Message}) — falling back to managed");
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                return false;
            }
        }
    }
}
