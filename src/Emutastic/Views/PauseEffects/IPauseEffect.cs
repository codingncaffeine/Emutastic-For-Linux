using System;
using SkiaSharp;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Animated overlay drawn on top of the paused game. Two flavors:
    ///   - <see cref="IPauseEffect"/>: vector — draws on an <see cref="SKCanvas"/>.
    ///   - <see cref="IPixelPauseEffect"/>: per-pixel into a coarse BGRA buffer
    ///     (plasma / aurora style) that the host upscales.
    /// Effects render with SkiaSharp so the SAME implementation serves all three surfaces:
    /// the Preferences theme-tab preview and the legacy in-process EmulatorWindow (which
    /// blit the Skia pixels into an Avalonia WriteableBitmap), and the game-host GL window
    /// (which composites them into its native OSD overlay — that process has no Avalonia).
    /// Ported from the upstream WPF subsystem (DrawingVisual/CompositionTarget).
    /// </summary>
    public interface IPauseEffect : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }

        /// <summary>Initialize for a canvas size + intensity multiplier (0.5–2.0). Called on
        /// start and whenever the canvas size changes.</summary>
        void Init(SKSize canvasSize, double intensity);

        /// <summary>Per-frame draw with the elapsed seconds. The canvas is cleared by the driver.</summary>
        void Tick(double deltaSeconds, SKCanvas canvas);
    }

    /// <summary>
    /// Pixel-bitmap variant. Implementers write BGRA8888 into the supplied buffer each frame;
    /// the host shows it stretched to fill. Buffer layout: width*height*4, top-down.
    /// </summary>
    public interface IPixelPauseEffect : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }
        void Init(int width, int height, double intensity);
        void Tick(double deltaSeconds, IntPtr bgraBuffer, int width, int height);
    }
}
