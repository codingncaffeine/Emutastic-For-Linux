using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>Firework bursts: at random intervals a ring of colored particles spawns, decelerates
    /// under gravity/drag, and fades. Multiple bursts coexist.</summary>
    public sealed class Fireworks : IPauseEffect
    {
        public string Id => "fireworks";
        public string DisplayName => "Fireworks";

        private struct Particle { public double X, Y, Vx, Vy; public int PaletteIndex; public double Life, InitialLife; }

        private readonly List<Particle> _particles = new();
        private Size _canvas;
        private readonly Random _rng = new();
        private double _spawnTimer;
        private double _spawnInterval = 1.2;
        private double _intensity = 1.0;

        private static readonly Color[] _palette =
        {
            Color.FromRgb(0xFF, 0x4B, 0x4B), Color.FromRgb(0x4B, 0xC8, 0xFF), Color.FromRgb(0xFF, 0xD8, 0x4B),
            Color.FromRgb(0x68, 0xFF, 0x9C), Color.FromRgb(0xC2, 0x6B, 0xFF), Color.FromRgb(0xFF, 0xA0, 0x4B),
        };
        private const int AlphaBuckets = 16;
        private static readonly IBrush[,] _brushes = BuildBrushTable();

        private static IBrush[,] BuildBrushTable()
        {
            var arr = new IBrush[_palette.Length, AlphaBuckets];
            for (int i = 0; i < _palette.Length; i++)
                for (int a = 0; a < AlphaBuckets; a++)
                {
                    byte alpha = (byte)((a + 1) * 0xFF / AlphaBuckets);
                    arr[i, a] = new SolidColorBrush(Color.FromArgb(alpha, _palette[i].R, _palette[i].G, _palette[i].B));
                }
            return arr;
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            _particles.Clear();
            _intensity = intensity;
            _spawnInterval = 1.4 / Math.Max(0.5, intensity);
            _spawnTimer = _spawnInterval * 0.3;
        }

        private void SpawnBurst()
        {
            double cx = _rng.NextDouble() * (_canvas.Width - 80) + 40;
            double cy = _rng.NextDouble() * (_canvas.Height * 0.55) + 40;
            int baseCount = 35 + _rng.Next(35);
            int count = Math.Max(8, (int)(baseCount * Math.Sqrt(_intensity)));
            int paletteIdx = _rng.Next(_palette.Length);
            for (int i = 0; i < count; i++)
            {
                double angle = (i / (double)count) * Math.PI * 2 + _rng.NextDouble() * 0.2;
                double speed = 80 + _rng.NextDouble() * 110;
                double life = 0.9 + _rng.NextDouble() * 0.7;
                _particles.Add(new Particle
                {
                    X = cx, Y = cy,
                    Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed,
                    PaletteIndex = paletteIdx, Life = life, InitialLife = life,
                });
            }
        }

        public void Tick(double dt, DrawingContext dc)
        {
            _spawnTimer -= dt;
            if (_spawnTimer <= 0)
            {
                SpawnBurst();
                _spawnTimer = _spawnInterval * (0.6 + _rng.NextDouble() * 0.8);
            }

            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Life -= dt;
                if (p.Life <= 0) { _particles.RemoveAt(i); continue; }
                p.Vy += 70 * dt;
                p.Vx *= 1.0 - 0.35 * dt;
                p.Vy *= 1.0 - 0.05 * dt;
                p.X += p.Vx * dt;
                p.Y += p.Vy * dt;
                _particles[i] = p;

                double alpha = Math.Max(0, p.Life / p.InitialLife);
                int bucket = Math.Clamp((int)(alpha * AlphaBuckets), 0, AlphaBuckets - 1);
                dc.DrawEllipse(_brushes[p.PaletteIndex, bucket], null, new Point(p.X, p.Y), 1.6, 1.6);
            }
        }

        public void Dispose() { _particles.Clear(); }
    }
}
