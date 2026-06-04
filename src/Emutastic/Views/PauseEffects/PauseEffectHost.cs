using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Avalonia surface for the active pause effect: blits the WriteableBitmap the runner fills
    /// from PauseFx's Skia frame (effects render Skia-side now so the game-host GL window can
    /// show the identical animation; the dark wash + fade are baked into the frame). Never
    /// hit-tests — the overlay is decorative.
    /// </summary>
    public sealed class PauseEffectHost : Control
    {
        /// <summary>Frame to show (filled by the runner each tick); null = nothing.</summary>
        public WriteableBitmap? Frame { get; set; }

        public PauseEffectHost()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0 || Frame == null) return;
            ctx.DrawImage(Frame, new Rect(Frame.Size), new Rect(size));
        }
    }
}
