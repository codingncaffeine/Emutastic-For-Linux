using Emutastic.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emutastic.Services
{
    // Port of the upstream Services/ScreenshotService.cs. The listing side (GetAll +
    // filename parsing) is verbatim; the WPF capture/encode path (Save, which took a
    // BitmapSource + PngBitmapEncoder) folds in with the emulator framebuffer capture
    // in the HW-rendering/emulator splinter — the Screenshots tab only needs GetAll().
    public class ScreenshotService
    {
        private readonly string _folder;

        public ScreenshotService()
        {
            _folder = AppPaths.GetFolder("Screenshots");
        }

        /// <summary>
        /// Returns all saved screenshots, newest first. Parses metadata from the filename.
        /// </summary>
        public List<Screenshot> GetAll()
        {
            var results = new List<Screenshot>();

            if (!Directory.Exists(_folder)) return results;

            foreach (string file in Directory.EnumerateFiles(_folder, "*.png", SearchOption.AllDirectories)
                                             .OrderByDescending(f => f))
            {
                var ss = ParseFileName(file);
                if (ss != null) results.Add(ss);
            }

            return results;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static Screenshot? ParseFileName(string filePath)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                // Expected: "{yyyyMMdd_HHmmss} {title} ({console})"
                if (name.Length < 16) return null;

                string timestampStr = name[..15];  // "yyyyMMdd_HHmmss"
                if (!DateTime.TryParseExact(timestampStr, "yyyyMMdd_HHmmss",
                        null, System.Globalization.DateTimeStyles.None, out DateTime takenAt))
                    return null;

                string rest = name[16..]; // "{title} ({console})"

                // Extract console from last "(…)"
                int parenOpen  = rest.LastIndexOf('(');
                int parenClose = rest.LastIndexOf(')');
                if (parenOpen < 0 || parenClose < parenOpen) return null;

                string console    = rest[(parenOpen + 1)..parenClose].Trim();
                string gameTitle  = rest[..parenOpen].Trim();

                return new Screenshot
                {
                    FilePath  = filePath,
                    GameTitle = gameTitle,
                    Console   = console,
                    TakenAt   = takenAt,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
