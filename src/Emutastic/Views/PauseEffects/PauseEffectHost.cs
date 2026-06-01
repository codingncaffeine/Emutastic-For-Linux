using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Rendering surface for the active pause effect (Avalonia port of the WPF DrawingVisual host).
    /// Vector effects are ticked inside <see cref="Render"/> with the live <see cref="DrawingContext"/>;
    /// pixel effects are ticked by the runner into a <see cref="WriteableBitmap"/> which this control
    /// just blits. A subtle dark wash sits behind the animation so light particles pop against bright
    /// paused frames. Never hit-tests — the overlay is decorative.
    /// </summary>
    public sealed class PauseEffectHost : Control
    {
        private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));

        /// <summary>Active vector effect (ticked in Render); null when a pixel effect or nothing is active.</summary>
        public IPauseEffect? Vector { get; set; }

        /// <summary>Bitmap for the active pixel effect (drawn in Render); null otherwise.</summary>
        public WriteableBitmap? PixelBitmap { get; set; }

        /// <summary>Elapsed seconds for the current frame (set by the runner before InvalidateVisual).</summary>
        public double Delta { get; set; }

        public PauseEffectHost()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0) return;

            ctx.FillRectangle(Shade, new Rect(size));

            if (Vector != null)
            {
                try { Vector.Tick(Delta, ctx); } catch { /* a misbehaving effect must not crash the window */ }
            }
            else if (PixelBitmap != null)
            {
                ctx.DrawImage(PixelBitmap, new Rect(PixelBitmap.Size), new Rect(size));
            }
        }
    }
}
