using System;
using SkiaSharp;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// UI-agnostic pause-effect driver: owns the active effect, the fade in/out envelope
    /// (0.28s, same as the WPF DoubleAnimation upstream) and a window-sized SKBitmap frame.
    /// Both surfaces consume it — the Avalonia preview/legacy window copy <see cref="Frame"/>
    /// into a WriteableBitmap on a DispatcherTimer; the game-host GL window composites it
    /// into its native OSD overlay each present-loop iteration. The subtle dark wash behind
    /// the animation (so light particles pop on bright paused frames) is part of the frame.
    /// </summary>
    public sealed class PauseFx : IDisposable
    {
        private const int PixelW = 320, PixelH = 240;     // coarse internal res; upscaled soft
        private const double FadeSeconds = 0.28;
        private static readonly SKColor Shade = new(0x00, 0x00, 0x00, 0x4D);

        private IPauseEffect? _vector;
        private IPixelPauseEffect? _pixel;
        private SKBitmap? _bmp;          // composed output (window-sized)
        private SKCanvas? _canvas;
        private SKBitmap? _pixelBmp;     // pixel effects' coarse target
        private double _intensity = 1.0;
        private double _fade;            // 0..1 current envelope
        private double _fadeTarget;
        private bool _stopping;
        private int _w, _h;

        /// <summary>True while there is anything to draw (including the fade-out tail).</summary>
        public bool Active => (_vector != null || _pixel != null) && !(_stopping && _fade <= 0.001);

        /// <summary>The composed frame for the current tick (premultiplied straight RGBA, window-sized).</summary>
        public SKBitmap? Frame => _bmp;

        /// <summary>Start an effect by registry id ("none"/unknown = no-op → inactive).</summary>
        public void Start(string effectId, double intensity, int width, int height)
        {
            var entry = PauseEffectRegistry.Find(effectId);
            if (entry == null || entry.Id == PauseEffectRegistry.NoneId) { Stop(immediate: true); return; }
            object fx;
            try { fx = entry.Factory(); } catch { return; }
            StartInstance(fx, intensity, width, height);
        }

        /// <summary>Start a pre-built effect instance (the Avalonia runner constructs from the registry itself).</summary>
        public void StartInstance(object fx, double intensity, int width, int height)
        {
            Stop(immediate: true);
            _intensity = intensity;
            Resize(width, height);
            if (fx is IPixelPauseEffect p)
            {
                _pixel = p;
                _pixelBmp = new SKBitmap(new SKImageInfo(PixelW, PixelH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                p.Init(PixelW, PixelH, intensity);
            }
            else if (fx is IPauseEffect v)
            {
                _vector = v;
                // _w/_h are the (possibly capped) render size set by Resize above — init the
                // effect to THAT, not the raw window size, or it lays out off the canvas.
                v.Init(new SKSize(_w, _h), intensity);
            }
            _fade = 0; _fadeTarget = 1; _stopping = false;
        }

        /// <summary>(Re)size the output frame; re-seeds vector effects like the upstream runner.</summary>
        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            // GlOsd composites this frame SCALED to the full window (DrawBitmap to the window
            // rect), so rendering the effect at native window resolution is wasted CPU. At
            // fullscreen (up to 4K) the window-sized SaveLayer + software raster per tick can't
            // keep up — ticks blow past the 0.1s clamp in TickInto and the animation crawls /
            // runs in slow motion. Cap the longest side and let GlOsd upscale; aspect ratio is
            // preserved so the stretch stays uniform, and the soft upscale is imperceptible for a
            // translucent overlay (the pixel effects already render at 320x240). Cost becomes
            // resolution-independent — the windowed default (≤1280) is unaffected.
            const int MaxDim = 1280;
            int rw = width, rh = height, longest = Math.Max(width, height);
            if (longest > MaxDim)
            {
                double s = (double)MaxDim / longest;
                rw = Math.Max(1, (int)Math.Round(width * s));
                rh = Math.Max(1, (int)Math.Round(height * s));
            }
            if (rw == _w && rh == _h && _bmp != null) return;
            _canvas?.Dispose(); _bmp?.Dispose();
            _bmp = new SKBitmap(new SKImageInfo(rw, rh, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            _canvas = new SKCanvas(_bmp);
            _w = rw; _h = rh;
            _vector?.Init(new SKSize(rw, rh), _intensity);
        }

        /// <summary>Begin the fade-out; the effect stays Active until the envelope reaches zero.</summary>
        public void Stop(bool immediate = false)
        {
            if (immediate)
            {
                _vector?.Dispose(); _pixel?.Dispose();
                _vector = null; _pixel = null;
                _pixelBmp?.Dispose(); _pixelBmp = null;
                _fade = 0; _stopping = false;
                return;
            }
            if (_vector == null && _pixel == null) return;
            _stopping = true;
            _fadeTarget = 0;
        }

        /// <summary>
        /// Advance the envelope + effect and compose into <see cref="Frame"/>.
        /// Returns false when there is nothing to draw (caller can skip compositing).
        /// </summary>
        public bool TickInto(double deltaSeconds)
        {
            if (_canvas == null || (_vector == null && _pixel == null)) return false;
            if (deltaSeconds > 0.1) deltaSeconds = 0.1;   // clamp so a stall doesn't fast-forward physics

            double step = deltaSeconds / FadeSeconds;
            _fade = _fade < _fadeTarget ? Math.Min(_fadeTarget, _fade + step)
                                        : Math.Max(_fadeTarget, _fade - step);
            if (_stopping && _fade <= 0.001) { Stop(immediate: true); return false; }

            _canvas.Clear(SKColors.Transparent);
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * _fade)) };
            _canvas.SaveLayer(layer);
            _canvas.DrawRect(new SKRect(0, 0, _w, _h), new SKPaint { Color = Shade });
            try
            {
                if (_vector != null) _vector.Tick(deltaSeconds, _canvas);
                else if (_pixel != null && _pixelBmp != null)
                {
                    _pixel.Tick(deltaSeconds, _pixelBmp.GetPixels(), PixelW, PixelH);
                    using var img = SKImage.FromPixels(_pixelBmp.PeekPixels());
                    _canvas.DrawImage(img, new SKRect(0, 0, _w, _h),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"PauseFx tick failed: {ex.Message}");
                Stop(immediate: true);
                _canvas.Restore();
                return false;
            }
            _canvas.Restore();
            return true;
        }

        /// <summary>Re-seed the active effect at a new intensity (slider preview, no fade/realloc).</summary>
        public void SetIntensity(double intensity)
        {
            _intensity = intensity;
            _vector?.Init(new SKSize(_w, _h), intensity);
            _pixel?.Init(PixelW, PixelH, intensity);
        }

        public bool HasActiveEffect => _vector != null || _pixel != null;

        public void Dispose()
        {
            Stop(immediate: true);
            _canvas?.Dispose(); _bmp?.Dispose();
            _canvas = null; _bmp = null;
        }
    }
}
