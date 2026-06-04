using System;
using SkiaSharp;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>Synthwave / Tron-style perspective neon grid receding to a horizon. Horizontal lines
    /// scroll toward the viewer; vertical lines fan out from the vanishing point.</summary>
    public sealed class SynthwaveGrid : IPauseEffect
    {
        public string Id => "synthwave";
        public string DisplayName => "Synthwave Grid";

        private SKSize _canvas;
        private double _scroll;
        private double _scrollSpeed = 0.3;
        private readonly SKPaint _gridPen = new() { Color = new SKColor(0xFF, 0x4B, 0xCB, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
        private readonly SKPaint _horizonGlow = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
        // Cached glow shader rebuilt only when the glow rect's vertical extent changes.
        private SKShader? _glowShader;
        private float _glowTop = float.NaN, _glowBottom = float.NaN;
        private static readonly SKColor[] _glowStops = { new SKColor(0x66, 0x22, 0x99, 0x99), new SKColor(0x00, 0x00, 0x00, 0x00) };
        private static readonly float[] _glowPositions = { 0.0f, 1.0f };

        public void Init(SKSize canvasSize, double intensity)
        {
            _canvas = canvasSize;
            _scrollSpeed = 0.2 + 0.4 * intensity;
        }

        public void Tick(double dt, SKCanvas canvas)
        {
            _scroll = (_scroll + _scrollSpeed * dt) % 1.0;

            double w = _canvas.Width, h = _canvas.Height;
            double horizonY = h * 0.55, cx = w / 2.0;

            // Rect(0, horizonY-12, w, h*0.45) → SKRect(left, top, right, bottom).
            float glowTop = (float)(horizonY - 12);
            float glowBottom = (float)(horizonY - 12 + h * 0.45);
            var glowRect = new SKRect(0, glowTop, (float)w, glowBottom);
            // Vertical gradient: top of rect = stop 0, bottom = stop 1 (matches the relative 0.5,0 → 0.5,1 brush).
            if (_glowShader == null || glowTop != _glowTop || glowBottom != _glowBottom)
            {
                _glowShader?.Dispose();
                _glowShader = SKShader.CreateLinearGradient(
                    new SKPoint(0, glowTop), new SKPoint(0, glowBottom),
                    _glowStops, _glowPositions, SKShaderTileMode.Clamp);
                _horizonGlow.Shader = _glowShader;
                _glowTop = glowTop;
                _glowBottom = glowBottom;
            }
            canvas.DrawRect(glowRect, _horizonGlow);

            int rows = 14;
            for (int i = 0; i < rows; i++)
            {
                double t = (i + _scroll) / rows;
                t = t * t * t;
                double y = horizonY + (h - horizonY) * t;
                if (y < horizonY || y > h) continue;
                double opacity = (1.0 - t) * 0.9 + 0.1;
                // Per-line opacity = pen alpha (0xFF) scaled by opacity (replaces PushOpacity).
                _gridPen.Color = _gridPen.Color.WithAlpha((byte)(0xFF * Math.Clamp(opacity, 0, 1)));
                canvas.DrawLine(0, (float)y, (float)w, (float)y, _gridPen);
            }

            // Vertical fan lines are drawn at full alpha (no PushOpacity in the original).
            _gridPen.Color = _gridPen.Color.WithAlpha(0xFF);
            int cols = 22;
            for (int i = -cols; i <= cols; i++)
            {
                double bx = cx + (i / (double)cols) * (w * 1.5);
                canvas.DrawLine((float)cx, (float)horizonY, (float)bx, (float)h, _gridPen);
            }
        }

        public void Dispose()
        {
            _gridPen.Dispose();
            _horizonGlow.Dispose();
            _glowShader?.Dispose();
        }
    }
}
