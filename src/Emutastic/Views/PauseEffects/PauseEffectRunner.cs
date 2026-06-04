using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Avalonia adapter over <see cref="PauseFx"/> (the UI-agnostic Skia driver): a ~60Hz
    /// DispatcherTimer ticks the effect, then copies the composed Skia frame into a
    /// WriteableBitmap the host control blits. The game-host GL window uses PauseFx directly —
    /// same pixels on every surface. Public API unchanged from the original runner (the
    /// Preferences preview and the legacy EmulatorWindow construct effects from the registry).
    /// </summary>
    public sealed class PauseEffectRunner : IDisposable
    {
        private readonly PauseEffectHost _host;
        private readonly PauseFx _fx = new();
        private readonly DispatcherTimer _timer;
        private WriteableBitmap? _bmp;
        private long _lastTicks;

        public PauseEffectRunner(PauseEffectHost host)
        {
            _host = host;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;
        }

        public void Start(IPauseEffect vector, double intensity) => StartCore(vector, intensity);
        public void Start(IPixelPauseEffect pixel, double intensity) => StartCore(pixel, intensity);

        private void StartCore(object fx, double intensity)
        {
            var size = CanvasSize();
            _fx.StartInstance(fx, intensity, (int)size.Width, (int)size.Height);
            _host.IsVisible = true;
            _host.Opacity = 1;          // fade is baked into the frame now
            _lastTicks = 0;
            if (!_timer.IsEnabled) _timer.Start();
        }

        /// <summary>Change intensity in place (re-seeds the active effect) — no fade/realloc,
        /// avoids the preview flicker when dragging the intensity slider.</summary>
        public void SetIntensity(double intensity) => _fx.SetIntensity(intensity);

        public bool HasActiveEffect => _fx.HasActiveEffect;

        /// <summary>Fade out and tear down (or immediately, e.g. before starting a new effect).</summary>
        public void Stop(bool immediate = false)
        {
            _fx.Stop(immediate);
            if (immediate)
            {
                _timer.Stop();
                _host.Frame = null;
                _host.IsVisible = false;
            }
            else if (_fx.HasActiveEffect && !_timer.IsEnabled) _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            long now = Environment.TickCount64;
            double dt = _lastTicks == 0 ? 1.0 / 60.0 : (now - _lastTicks) / 1000.0;
            _lastTicks = now;

            var size = CanvasSize();
            _fx.Resize((int)size.Width, (int)size.Height);

            if (!_fx.TickInto(dt))
            {
                if (!_fx.Active)        // fade-out finished
                {
                    _timer.Stop();
                    _host.Frame = null;
                    _host.IsVisible = false;
                    _host.InvalidateVisual();
                }
                return;
            }

            // Copy the Skia frame into the Avalonia bitmap (sizes match — both follow CanvasSize).
            var src = _fx.Frame!;
            if (_bmp == null || _bmp.PixelSize.Width != src.Width || _bmp.PixelSize.Height != src.Height)
                _bmp = new WriteableBitmap(new PixelSize(src.Width, src.Height), new Vector(96, 96),
                    PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using (var fb = _bmp.Lock())
            {
                int rowBytes = src.Width * 4;
                if (fb.RowBytes == rowBytes)
                {
                    unsafe { Buffer.MemoryCopy((void*)src.GetPixels(), (void*)fb.Address, (long)rowBytes * src.Height, (long)rowBytes * src.Height); }
                }
                else
                {
                    for (int y = 0; y < src.Height; y++)
                        unsafe { Buffer.MemoryCopy((void*)(src.GetPixels() + y * rowBytes), (void*)(fb.Address + y * fb.RowBytes), rowBytes, rowBytes); }
                }
            }
            _host.Frame = _bmp;
            _host.InvalidateVisual();
        }

        private Size CanvasSize()
        {
            double w = _host.Bounds.Width  > 0 ? _host.Bounds.Width  : 800;
            double h = _host.Bounds.Height > 0 ? _host.Bounds.Height : 600;
            return new Size(w, h);
        }

        public void Dispose()
        {
            _timer.Stop();
            _fx.Dispose();
        }
    }
}
