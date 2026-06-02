using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Emutastic.Converters;

namespace Emutastic.Controls
{
    /// <summary>
    /// Attached property that loads an <see cref="Image"/>'s source from a file path WITHOUT ever
    /// decoding on the UI thread. A warm cache hit (via <see cref="PathToImageConverter"/>) is applied
    /// synchronously; a miss leaves the image blank and decodes off-thread, applying the bitmap when
    /// ready — but only if the Image still wants that path (so recycled grid containers don't show a
    /// stale cover). This is what keeps the virtualized library grid from freezing while realizing the
    /// visible cards, at any library size.
    ///
    /// Usage: <c>&lt;Image ctl:AsyncImage.SourcePath="{Binding DisplayArtPath}"/&gt;</c>
    /// </summary>
    public static class AsyncImage
    {
        public static readonly AttachedProperty<string?> SourcePathProperty =
            AvaloniaProperty.RegisterAttached<Image, string?>("SourcePath", typeof(AsyncImage));

        public static void SetSourcePath(Image image, string? value) => image.SetValue(SourcePathProperty, value);
        public static string? GetSourcePath(Image image) => image.GetValue(SourcePathProperty);

        static AsyncImage()
        {
            SourcePathProperty.Changed.AddClassHandler<Image>((img, e) =>
                OnSourcePathChanged(img, e.GetNewValue<string?>()));
        }

        private static void OnSourcePathChanged(Image img, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                img.Source = null;
                return;
            }

            // Cache hit: apply now (cheap, no decode).
            var cached = PathToImageConverter.GetCached(path);
            if (cached != null)
            {
                img.Source = cached;
                return;
            }

            // Miss: blank now, decode off the UI thread, apply when ready (if still current).
            img.Source = null;
            _ = PathToImageConverter.LoadAsync(path!).ContinueWith(t =>
            {
                if (t.IsFaulted || t.Result is not { } bmp) return;
                Dispatcher.UIThread.Post(() =>
                {
                    // Apply only if this Image still wants this exact path (recycled containers change it).
                    if (GetSourcePath(img) == path)
                        img.Source = bmp;
                });
            }, TaskScheduler.Default);
        }
    }
}
