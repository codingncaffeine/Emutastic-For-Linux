using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Emutastic.Services;

namespace Emutastic.Converters
{
    // Linux/Avalonia translation of the upstream WPF converters. WPF Visibility is replaced by
    // bool (Avalonia controls bind IsVisible); WPF BitmapImage by Avalonia Bitmap; pack:// by
    // avares://; ColorConverter by Color.Parse. The WPF Freezable BindingProxy is dropped (unused
    // in the window — card width self-binds to the Border, with a resource fallback in the converter).

    // value (bool) -> IsVisible. Identity passthrough kept for symmetry / explicit bindings.
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true;
    }

    public class InverseBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not true;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not true;
    }

    public class StringNotEmptyToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => !string.IsNullOrEmpty(value as string);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // true when the bound int count is zero (drives "empty library" placeholders).
    public class CountIsZeroToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int count && count == 0;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class NullOrEmptyToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value?.ToString());
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class NotNullOrEmptyToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => !string.IsNullOrWhiteSpace(value?.ToString());
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class StringToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            try
            {
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    return Color.Parse(s);
            }
            catch { }
            return Colors.Transparent;
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Path -> decoded Avalonia Bitmap with a two-tier cache (weak + strong LRU). Avalonia Bitmap is
    /// immutable/thread-safe, so no Freeze is needed. Convert() is synchronous and returns a cached
    /// bitmap (Avalonia bindings have no IsAsync); off-thread warming is done by PreloadAsync, which
    /// MainViewModel already calls for visible/console artwork so Convert() hits a hot cache.
    /// </summary>
    public class PathToImageConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _weak =
            new(StringComparer.OrdinalIgnoreCase);

        private const int StrongCapacity = 1500;
        private static readonly object _strongLock = new();
        private static readonly Dictionary<string, LinkedListNode<string>> _strongIndex =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _strongOrder = new();
        private static readonly Dictionary<string, Bitmap> _strong =
            new(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            _weak.Clear();
            lock (_strongLock) { _strong.Clear(); _strongIndex.Clear(); _strongOrder.Clear(); }
        }

        public static void Evict(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _weak.TryRemove(path, out _);
            lock (_strongLock)
            {
                if (_strongIndex.TryGetValue(path, out var node))
                {
                    _strongOrder.Remove(node);
                    _strongIndex.Remove(path);
                }
                _strong.Remove(path);
            }
        }

        private static void Promote(string path, Bitmap bitmap)
        {
            lock (_strongLock)
            {
                if (_strongIndex.TryGetValue(path, out var existing))
                {
                    _strongOrder.Remove(existing);
                    _strongOrder.AddFirst(existing);
                    _strong[path] = bitmap;
                    return;
                }
                var node = _strongOrder.AddFirst(path);
                _strongIndex[path] = node;
                _strong[path] = bitmap;
                while (_strongOrder.Count > StrongCapacity)
                {
                    var last = _strongOrder.Last!;
                    _strongOrder.RemoveLast();
                    _strongIndex.Remove(last.Value);
                    _strong.Remove(last.Value);
                }
            }
        }

        private static Bitmap? Decode(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                // Cards are ≤148px; decode to 300 covers 2x DPI scaling. DecodeToWidth keeps memory low.
                return Bitmap.DecodeToWidth(fs, 300);
            }
            catch { return null; }
        }

        public static bool IsCached(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            lock (_strongLock) { if (_strong.ContainsKey(path)) return true; }
            return _weak.TryGetValue(path, out var w) && w.TryGetTarget(out _);
        }

        public static Task PreloadAsync(IEnumerable<string?> paths)
        {
            var copy = new List<string>();
            foreach (var p in paths)
                if (!string.IsNullOrWhiteSpace(p)) copy.Add(p!);
            if (copy.Count == 0) return Task.CompletedTask;

            return Task.Run(() =>
            {
                foreach (var path in copy)
                {
                    Bitmap? hit = null;
                    lock (_strongLock) { if (_strong.TryGetValue(path, out var s)) hit = s; }
                    if (hit != null) { Promote(path, hit); continue; }

                    if (_weak.TryGetValue(path, out var weakRef) && weakRef.TryGetTarget(out var alive))
                    {
                        Promote(path, alive);
                        continue;
                    }

                    var bmp = Decode(path);
                    if (bmp != null)
                    {
                        _weak[path] = new WeakReference<Bitmap>(bmp);
                        Promote(path, bmp);
                    }
                }
            });
        }

        /// <summary>Cache-only lookup (no decode). Returns a warm bitmap or null. Used by the
        /// async grid-image loader so realization never blocks the UI thread on a JPEG decode.</summary>
        public static Bitmap? GetCached(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (_strongLock)
            {
                if (_strong.TryGetValue(path!, out var s) && _strongIndex.TryGetValue(path!, out var node))
                {
                    _strongOrder.Remove(node);
                    _strongOrder.AddFirst(node);
                    return s;
                }
            }
            if (_weak.TryGetValue(path!, out var w) && w.TryGetTarget(out var cached))
            {
                Promote(path!, cached);
                return cached;
            }
            return null;
        }

        /// <summary>Return the cached bitmap, or decode it off the UI thread and cache it.</summary>
        public static Task<Bitmap?> LoadAsync(string path)
        {
            var cached = GetCached(path);
            if (cached != null) return Task.FromResult<Bitmap?>(cached);
            return Task.Run(() =>
            {
                var bmp = Decode(path);
                if (bmp != null)
                {
                    _weak[path] = new WeakReference<Bitmap>(bmp);
                    Promote(path, bmp);
                }
                return bmp;
            });
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            try
            {
                if (value is not string path || string.IsNullOrWhiteSpace(path))
                    return null;

                Bitmap? strongHit = null;
                lock (_strongLock)
                {
                    if (_strong.TryGetValue(path, out strongHit) &&
                        _strongIndex.TryGetValue(path, out var node))
                    {
                        _strongOrder.Remove(node);
                        _strongOrder.AddFirst(node);
                    }
                }
                if (strongHit != null) return strongHit;

                if (_weak.TryGetValue(path, out var weakRef))
                {
                    if (weakRef.TryGetTarget(out var cached)) { Promote(path, cached); return cached; }
                    _weak.TryRemove(path, out _);
                }

                var bitmap = Decode(path);
                if (bitmap == null) return null;
                _weak[path] = new WeakReference<Bitmap>(bitmap);
                Promote(path, bitmap);
                return bitmap;
            }
            catch { }
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Display-time title normalizer (only re-cases uniformly lower/upper titles).</summary>
    public class SmartTitleCaseConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var s = value as string ?? "";
            if (s.Length == 0) return s;
            bool allLower = true, allUpper = true;
            foreach (char c in s)
            {
                if (char.IsLetter(c))
                {
                    if (!char.IsLower(c)) allLower = false;
                    if (!char.IsUpper(c)) allUpper = false;
                }
            }
            if (allLower || allUpper)
                return culture.TextInfo.ToTitleCase(s.ToLowerInvariant());
            return s;
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>(Console, CardWidth, IsMixedView) -> card art height preserving box-art aspect ratio.</summary>
    public class ConsoleToArtHeightConverter : IMultiValueConverter
    {
        private const double MixedViewRatio = 0.73; // DVD keepcase

        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            string console = values.Count > 0 ? (values[0] as string ?? "") : "";
            double cardWidth = values.Count > 1 && values[1] is double d ? d : 0.0;
            bool isMixed = values.Count > 2 && values[2] is bool b && b;

            if (!(cardWidth > 0.0) || double.IsNaN(cardWidth))
            {
                if (Application.Current?.Resources.TryGetResource("LibraryCardWidth", null, out var r) == true
                    && r is double rw && rw > 0.0)
                    cardWidth = rw;
                else
                    cardWidth = 148.0;
            }

            double ratio = isMixed ? MixedViewRatio : RomService.GetBoxRatio(console);
            if (!(ratio > 0.0) || double.IsNaN(ratio)) ratio = 0.73;
            return Math.Max(1.0, Math.Round(cardWidth / ratio));
        }
    }

    /// <summary>Same inputs as art height + the caption area, so the grid cell has a deterministic size.</summary>
    public class ConsoleToCardHeightConverter : IMultiValueConverter
    {
        private const double TitleArea = 64.0;
        private static readonly ConsoleToArtHeightConverter _artHeight = new();

        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var art = _artHeight.Convert(values, targetType, parameter, culture);
            double h = art is double d ? d : 200.0;
            return h + TitleArea;
        }
    }

    /// <summary>Game.Console tag -> small system-icon Bitmap from avares://Assets/system_icons.</summary>
    public class ConsoleTagToIconConverter : IValueConverter
    {
        private static readonly Dictionary<string, string> _map = new()
        {
            ["Atari2600"] = "atari2600.jpg", ["Atari7800"] = "atari7800.jpg", ["Jaguar"] = "systemicons1_13.jpg",
            ["NES"] = "nes_icon.jpg", ["FDS"] = "famicon disk system.jpg", ["SNES"] = "snes.jpg",
            ["N64"] = "n64.jpg", ["GameCube"] = "gamecube.jpg", ["GB"] = "gameboy.jpg", ["GBC"] = "gameboy.jpg",
            ["GBA"] = "gba.jpg", ["3DS"] = "3ds_icon.jpg", ["NDS"] = "nds.jpg", ["VirtualBoy"] = "virtualboy.jpg",
            ["SMS"] = "sms.jpg", ["Genesis"] = "genesis.jpg", ["SegaCD"] = "genesis.jpg", ["Sega32X"] = "32x.jpg",
            ["Saturn"] = "saturn.jpg", ["GameGear"] = "sms.jpg", ["SG1000"] = "sms.jpg", ["Dreamcast"] = "dreamcast.jpg",
            ["PS1"] = "ps1.jpg", ["PSP"] = "psp.jpg", ["TG16"] = "TG16.jpg", ["TGCD"] = "TG16.jpg",
            ["NeoGeo"] = "neogeo.jpg", ["NeoCD"] = "neogeo_cd.png", ["NGP"] = "neo geo pocket.jpg",
            ["NGPC"] = "neo geo pocket.jpg", ["3DO"] = "3d0.jpg", ["CDi"] = "cdi_icon.jpg",
            ["ColecoVision"] = "coleco.jpg", ["Vectrex"] = "vectrex.jpg",
        };

        private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string tag || !_map.TryGetValue(tag, out var file)) return null;
            return _cache.GetOrAdd(file, f =>
            {
                try
                {
                    var uri = new Uri($"avares://Emutastic/Assets/system_icons/{f}");
                    using var s = AssetLoader.Open(uri);
                    // Sidebar icons render at 20×20; decode them small instead of at full JPEG
                    // resolution (some sources are 500px+/280KB). Decoding 30+ full-res images during
                    // MainWindow XAML inflation was the dominant startup cost. 48px covers 2× HiDPI.
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    return Bitmap.DecodeToWidth(ms, 48);
                }
                catch { return null; }
            });
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Rating int (0–5) -> 5-glyph ★/☆ string.</summary>
    public class RatingToStarsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int r = value is int n ? Math.Clamp(n, 0, 5) : 0;
            return new string('★', r) + new string('☆', 5 - r);
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Rating int (0–5) -> N filled ★ glyphs only.</summary>
    public class RatingToFilledStarsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int r = value is int n ? Math.Clamp(n, 0, 5) : 0;
            return new string('★', r);
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>DateTime? -> "MMM d, yyyy" or empty.</summary>
    public class LastPlayedToMediumDateConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is DateTime dt ? dt.ToString("MMM d, yyyy", culture) : string.Empty;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Integer -> string, 0 rendered blank.</summary>
    public class ZeroToBlankConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int n && n != 0 ? n.ToString("N0", culture) : string.Empty;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
