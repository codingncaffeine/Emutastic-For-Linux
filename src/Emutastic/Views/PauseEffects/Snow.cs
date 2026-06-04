using System;
using SkiaSharp;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Classic ZSNES-style snowfall: white particles drift downward with a per-flake sine-wave
    /// horizontal sway, varied size and fall speed for depth.
    /// </summary>
    public sealed class Snow : IPauseEffect
    {
        public string Id => "snow";
        public string DisplayName => "Snow";

        private struct Flake
        {
            public double X, Y, VelocityY, DriftPhase, DriftRate, DriftAmplitude, Radius, Opacity;
        }

        private Flake[] _flakes = Array.Empty<Flake>();
        private SKSize _canvas;
        private readonly Random _rng = new();
        private readonly SKPaint _flakePaint = new() { Color = new SKColor(0xF0, 0xF6, 0xFF, 0xE0), IsAntialias = false, Style = SKPaintStyle.Fill };

        public void Init(SKSize canvasSize, double intensity)
        {
            _canvas = canvasSize;
            int baseCount = (int)(220 * (_canvas.Width * _canvas.Height) / (1920.0 * 1080.0));
            int count = Math.Max(40, (int)(baseCount * intensity));
            _flakes = new Flake[count];
            for (int i = 0; i < count; i++)
                _flakes[i] = SpawnFlake(initialY: _rng.NextDouble() * _canvas.Height);
        }

        private Flake SpawnFlake(double initialY)
        {
            int sizeBucket = _rng.Next(3);
            double size = sizeBucket == 0 ? 1.0 : sizeBucket == 1 ? 2.0 : 3.0;
            double depth = sizeBucket / 2.0;
            return new Flake
            {
                X = _rng.NextDouble() * _canvas.Width,
                Y = initialY,
                VelocityY = 25 + depth * 90,
                DriftPhase = _rng.NextDouble() * Math.PI * 2,
                DriftRate = 0.4 + _rng.NextDouble() * 1.2,
                DriftAmplitude = 8 + _rng.NextDouble() * 22,
                Radius = size,
                Opacity = 0.6 + depth * 0.4,
            };
        }

        public void Tick(double deltaSeconds, SKCanvas canvas)
        {
            for (int i = 0; i < _flakes.Length; i++)
            {
                ref var f = ref _flakes[i];
                f.Y += f.VelocityY * deltaSeconds;
                f.DriftPhase += f.DriftRate * deltaSeconds;
                if (f.Y - f.Radius > _canvas.Height)
                    f = SpawnFlake(initialY: -f.Radius);

                double drawX = f.X + Math.Sin(f.DriftPhase) * f.DriftAmplitude;
                if (drawX < -f.Radius) drawX += _canvas.Width + f.Radius * 2;
                else if (drawX > _canvas.Width + f.Radius) drawX -= _canvas.Width + f.Radius * 2;

                var rect = new SKRect((float)Math.Floor(drawX), (float)Math.Floor(f.Y),
                                      (float)(Math.Floor(drawX) + f.Radius), (float)(Math.Floor(f.Y) + f.Radius));
                // Per-flake opacity = the brush alpha scaled by the flake's depth opacity
                // (replaces Avalonia's PushOpacity scope).
                _flakePaint.Color = _flakePaint.Color.WithAlpha((byte)(0xE0 * Math.Clamp(f.Opacity, 0, 1)));
                canvas.DrawRect(rect, _flakePaint);
            }
        }

        public void Dispose() { _flakes = Array.Empty<Flake>(); _flakePaint.Dispose(); }
    }
}
