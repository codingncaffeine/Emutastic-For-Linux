using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Animated overlay drawn on top of the paused game. Two flavors:
    ///   - <see cref="IPauseEffect"/>: vector — draws with a <see cref="DrawingContext"/>
    ///     (ticked inside the host's Render).
    ///   - <see cref="IPixelPauseEffect"/>: per-pixel via a <see cref="WriteableBitmap"/>
    ///     (plasma / aurora style), ticked by the runner and blitted by the host.
    /// Avalonia port of the upstream WPF subsystem (DrawingVisual/CompositionTarget →
    /// Control.Render / DispatcherTimer).
    /// </summary>
    public interface IPauseEffect : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }

        /// <summary>Initialize for a canvas size + intensity multiplier (0.5–2.0). Called on
        /// start and whenever the canvas size changes.</summary>
        void Init(Size canvasSize, double intensity);

        /// <summary>Per-frame draw. Called from the host's Render with the elapsed seconds.</summary>
        void Tick(double deltaSeconds, DrawingContext dc);
    }

    /// <summary>
    /// Pixel-bitmap variant. Implementers write into the supplied <see cref="WriteableBitmap"/>
    /// each frame (via Lock()). The host shows the bitmap stretched to fill.
    /// </summary>
    public interface IPixelPauseEffect : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }
        void Init(int width, int height, double intensity);
        void Tick(double deltaSeconds, WriteableBitmap target);
    }
}
