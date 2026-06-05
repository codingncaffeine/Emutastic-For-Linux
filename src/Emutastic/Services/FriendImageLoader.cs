using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Emutastic.Services
{
    /// <summary>
    /// RA/Friends image loader (port of upstream FriendImageLoader). Avalonia's
    /// Bitmap has no URL auto-download (unlike WPF BitmapImage), so this owns the
    /// async fetch + an in-memory cache, and logs every stage to ra.log under the
    /// given label so users can diagnose from a release build. All Achievements-tab
    /// and Friends images route through here so future tweaks stay centralized.
    /// </summary>
    public static class FriendImageLoader
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly Dictionary<string, Bitmap?> _cache = new();
        private static readonly object _gate = new();

        /// <summary>Loads <paramref name="url"/> into <paramref name="target"/> (async; cached).</summary>
        public static void Load(Image target, string? url, string label, string? context = null)
        {
            if (target == null) return;
            if (string.IsNullOrWhiteSpace(url))
            {
                RaLog.Write($"[FriendImg:{label}] url empty {(context ?? "")}".TrimEnd());
                return;
            }
            lock (_gate)
            {
                if (_cache.TryGetValue(url, out var cached))
                {
                    if (cached != null) target.Source = cached;
                    return;
                }
            }
            string urlSnap = url, labelSnap = label, ctxSnap = context ?? "";
            RaLog.Write($"[FriendImg:{labelSnap}] init {ctxSnap} url=[{urlSnap}]");
            _ = Task.Run(async () =>
            {
                Bitmap? bmp = null;
                try
                {
                    byte[] bytes = await _http.GetByteArrayAsync(urlSnap).ConfigureAwait(false);
                    using var ms = new MemoryStream(bytes);
                    bmp = new Bitmap(ms);
                    RaLog.Write($"[FriendImg:{labelSnap}] DownloadCompleted {ctxSnap} url=[{urlSnap}]");
                }
                catch (Exception ex)
                {
                    RaLog.Write($"[FriendImg:{labelSnap}] DownloadFailed {ctxSnap} url=[{urlSnap}]: {ex.Message}");
                }
                lock (_gate) { _cache[urlSnap] = bmp; }
                if (bmp != null)
                    Dispatcher.UIThread.Post(() => target.Source = bmp);
            });
        }
    }
}
