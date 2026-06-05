using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emutastic.Services
{
    /// <summary>One selectable downloaded shader preset (cog picker entry).</summary>
    public sealed class ShaderPresetItem
    {
        public string Display { get; init; } = "";
        /// <summary>Group header in the picker (e.g. "crt", "handheld").</summary>
        public string Category { get; init; } = "";
        /// <summary>Absolute path to the .glslp.</summary>
        public string AbsolutePath { get; init; } = "";
        /// <summary>Path relative to the glsl root, '/'-normalized — the persistence key.</summary>
        public string RelativePath { get; init; } = "";
    }

    /// <summary>
    /// Port of upstream Effects/Librashader/ShaderCatalog adapted to the GL pack: Windows runs the
    /// slang pack through librashader (D3D11), which ships no Linux binaries — so Linux uses
    /// libretro's GLSL pack (shaders_glsl, the same shader library in .glslp form, what RetroArch's
    /// own GL driver runs) through our wlpresent chain. Same category filtering and caching model;
    /// persistence prefix is "glsl:" (a Windows-saved "slang:" value degrades to None here).
    /// Call <see cref="GetDownloaded"/> off the UI thread (it walks the tree).
    /// </summary>
    public static class ShaderCatalog
    {
        /// <summary>Where the downloaded pack lives (Extras → Video Shaders).</summary>
        public static string GlslRoot => AppPaths.GetFolder("Shaders", "glsl");

        // Top-level folders that aren't standalone single-effect presets (decorative/shared/
        // auxiliary), mirroring upstream's exclusions; nes_raw_palette needs a special core
        // output mode we don't enable.
        private static readonly HashSet<string> ExcludedCategories =
            new(StringComparer.OrdinalIgnoreCase)
            { "bezel", "presets", "include", "test", "spec", "reshade", "hdr", "nes_raw_palette" };

        private static List<ShaderPresetItem>? _cache;
        private static long _cacheStamp = -1;
        private static readonly object _gate = new();

        public static bool IsInstalled()
        {
            try { return File.Exists(Path.Combine(GlslRoot, ".installed")); }
            catch { return false; }
        }

        /// <summary>Filtered, category-sorted downloaded presets; empty if the pack isn't
        /// installed. Cached until the pack is re-downloaded (.installed timestamp).</summary>
        public static IReadOnlyList<ShaderPresetItem> GetDownloaded()
        {
            try
            {
                string root = GlslRoot;
                string marker = Path.Combine(root, ".installed");
                if (!File.Exists(marker)) return Array.Empty<ShaderPresetItem>();
                long stamp = File.GetLastWriteTimeUtc(marker).Ticks;

                lock (_gate)
                    if (_cache != null && _cacheStamp == stamp) return _cache;

                var list = new List<ShaderPresetItem>();
                foreach (var file in Directory.EnumerateFiles(root, "*.glslp", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    int slash = rel.IndexOf('/');
                    string cat = slash > 0 ? rel[..slash] : "misc";
                    if (ExcludedCategories.Contains(cat)) continue;

                    list.Add(new ShaderPresetItem
                    {
                        Display      = Path.GetFileNameWithoutExtension(file),
                        Category     = cat,
                        AbsolutePath = file,
                        RelativePath = rel,
                    });
                }
                list.Sort((a, b) =>
                {
                    int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
                });

                lock (_gate) { _cache = list; _cacheStamp = stamp; }
                return list;
            }
            catch
            {
                return Array.Empty<ShaderPresetItem>();
            }
        }

        /// <summary>Resolve a persisted relative path (or bare filename) to an absolute .glslp;
        /// null when not found (e.g. the pack was removed).</summary>
        public static string? Resolve(string relativeOrName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relativeOrName)) return null;
                string root = GlslRoot;
                string direct = Path.GetFullPath(Path.Combine(root, relativeOrName));
                if (File.Exists(direct)) return direct;
                string name = Path.GetFileName(relativeOrName);
                return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                    .Where(p => !p.Replace('\\', '/').Contains("/bezel/", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Count(ch => ch == '/' || ch == '\\'))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
