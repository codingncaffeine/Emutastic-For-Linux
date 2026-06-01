using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Drives a pause effect: a ~60Hz <see cref="DispatcherTimer"/> computes the frame delta, ticks
    /// pixel effects into their bitmap, and invalidates the host (vector effects draw in the host's
    /// Render). Also runs a manual opacity fade in/out so the effect doesn't pop on/off. Avalonia
    /// replacement for the WPF CompositionTarget.Rendering + DoubleAnimation driver.
    /// </summary>
    public sealed class PauseEffectRunner : IDisposable
    {
        private const int PixelW = 320, PixelH = 240;     // coarse internal res; host upscales
        private const double FadeSeconds = 0.28;

        private readonly PauseEffectHost _host;
        private readonly DispatcherTimer _timer;
        private IPauseEffect? _vector;
        private IPixelPauseEffect? _pixel;
        private WriteableBitmap? _pixelBmp;
        private double _intensity = 1.0;
        private Size _lastInitSize;
        private long _lastTicks;
        private double _fadeTarget;      // 1 = fading/holding in, 0 = fading out
        private bool _stopping;

        public PauseEffectRunner(PauseEffectHost host)
        {
            _host = host;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;
        }

        public void Start(IPauseEffect vector, double intensity)
        {
            Stop(immediate: true);
            _pixel = null;
            _vector = vector;
            _intensity = intensity;
            _host.PixelBitmap = null;
            _host.Vector = vector;
            _lastInitSize = CanvasSize();
            vector.Init(_lastInitSize, intensity);
            BeginFadeIn();
        }

        public void Start(IPixelPauseEffect pixel, double intensity)
        {
            Stop(immediate: true);
            _vector = null;
            _pixel = pixel;
            _intensity = intensity;
            _pixelBmp = new WriteableBitmap(new PixelSize(PixelW, PixelH), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            _host.Vector = null;
            _host.PixelBitmap = _pixelBmp;
            pixel.Init(PixelW, PixelH, intensity);
            BeginFadeIn();
        }

        private void BeginFadeIn()
        {
            _stopping = false;
            _fadeTarget = 1.0;
            _host.Opacity = 0;
            _host.IsVisible = true;
            _lastTicks = 0;
            if (!_timer.IsEnabled) _timer.Start();
        }

        /// <summary>Fade out and tear down (or immediately, e.g. before starting a new effect).</summary>
        public void Stop(bool immediate = false)
        {
            if (immediate)
            {
                _timer.Stop();
                DisposeEffects();
                _host.Vector = null; _host.PixelBitmap = null;
                _host.Opacity = 0; _host.IsVisible = false;
                _stopping = false;
                return;
            }
            if (_vector == null && _pixel == null) return;
            _stopping = true;
            _fadeTarget = 0.0;
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            long now = Environment.TickCount64;
            double dt = _lastTicks == 0 ? 1.0 / 60.0 : (now - _lastTicks) / 1000.0;
            if (dt > 0.1) dt = 0.1;                 // clamp so a stall doesn't fast-forward physics
            _lastTicks = now;

            // Opacity fade.
            double step = dt / FadeSeconds;
            if (_host.Opacity < _fadeTarget) _host.Opacity = Math.Min(_fadeTarget, _host.Opacity + step);
            else if (_host.Opacity > _fadeTarget) _host.Opacity = Math.Max(_fadeTarget, _host.Opacity - step);

            if (_stopping && _host.Opacity <= 0.001)
            {
                _timer.Stop();
                DisposeEffects();
                _host.Vector = null; _host.PixelBitmap = null;
                _host.IsVisible = false;
                _stopping = false;
                return;
            }

            try
            {
                if (_vector != null)
                {
                    var size = CanvasSize();
                    if (Math.Abs(size.Width - _lastInitSize.Width) > 2 || Math.Abs(size.Height - _lastInitSize.Height) > 2)
                    {
                        _lastInitSize = size;
                        _vector.Init(size, _intensity);
                    }
                    _host.Delta = dt;
                    _host.InvalidateVisual();          // vector draws in Render
                }
                else if (_pixel != null && _pixelBmp != null)
                {
                    _pixel.Tick(dt, _pixelBmp);        // write the bitmap
                    _host.InvalidateVisual();          // host blits it
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"PauseEffect tick failed: {ex.Message}");
                Stop(immediate: true);
            }
        }

        private Size CanvasSize()
        {
            double w = _host.Bounds.Width  > 0 ? _host.Bounds.Width  : 800;
            double h = _host.Bounds.Height > 0 ? _host.Bounds.Height : 600;
            return new Size(w, h);
        }

        private void DisposeEffects()
        {
            try { _vector?.Dispose(); } catch { }
            try { _pixel?.Dispose(); } catch { }
            _vector = null; _pixel = null; _pixelBmp = null;
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            DisposeEffects();
        }
    }
}
