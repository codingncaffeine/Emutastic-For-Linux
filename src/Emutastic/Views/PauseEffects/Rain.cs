using System;
using SkiaSharp;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>Vertical rain streaks with slight wind shear — faster, more aggressive than Snow.</summary>
    public sealed class Rain : IPauseEffect
    {
        public string Id => "rain";
        public string DisplayName => "Rain";

        private struct Drop { public double X, Y, Length, VelocityY, Lean, Opacity; }
        private Drop[] _drops = Array.Empty<Drop>();
        private SKSize _canvas;
        private readonly Random _rng = new();
        private readonly SKPaint _pen = new() { Color = new SKColor(0xCB, 0xD8, 0xEA, 0xC0), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };

        public void Init(SKSize canvasSize, double intensity)
        {
            _canvas = canvasSize;
            int count = (int)(280 * intensity * (_canvas.Width * _canvas.Height) / (1920.0 * 1080.0));
            count = Math.Max(60, count);
            _drops = new Drop[count];
            for (int i = 0; i < count; i++) _drops[i] = NewDrop(_rng.NextDouble() * _canvas.Height);
        }

        private Drop NewDrop(double y) => new()
        {
            X = _rng.NextDouble() * _canvas.Width,
            Y = y,
            Length = 8 + _rng.NextDouble() * 18,
            VelocityY = 380 + _rng.NextDouble() * 320,
            Lean = -2 + _rng.NextDouble() * 4,
            Opacity = 0.35 + _rng.NextDouble() * 0.55,
        };

        public void Tick(double dt, SKCanvas canvas)
        {
            for (int i = 0; i < _drops.Length; i++)
            {
                ref var d = ref _drops[i];
                d.Y += d.VelocityY * dt;
                if (d.Y - d.Length > _canvas.Height) d = NewDrop(-d.Length);
                // Per-drop opacity = the pen alpha scaled by the drop's opacity (replaces PushOpacity).
                _pen.Color = _pen.Color.WithAlpha((byte)(0xC0 * Math.Clamp(d.Opacity, 0, 1)));
                canvas.DrawLine((float)d.X, (float)d.Y, (float)(d.X + d.Lean), (float)(d.Y + d.Length), _pen);
            }
        }

        public void Dispose() { _drops = Array.Empty<Drop>(); _pen.Dispose(); }
    }
}
