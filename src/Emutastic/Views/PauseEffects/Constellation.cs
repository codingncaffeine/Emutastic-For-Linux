using System;
using SkiaSharp;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>"Geometric network": nodes drift and bounce off edges; lines connect any two within
    /// a threshold distance, line opacity falling off with distance.</summary>
    public sealed class Constellation : IPauseEffect
    {
        public string Id => "constellation";
        public string DisplayName => "Constellation";

        private struct Node { public double X, Y, Vx, Vy; }
        private Node[] _nodes = Array.Empty<Node>();
        private SKSize _canvas;
        private readonly Random _rng = new();
        private readonly SKPaint _nodeBrush = new() { Color = new SKColor(0xCC, 0xE0, 0xFF, 0xE6), Style = SKPaintStyle.Fill, IsAntialias = true };
        private static readonly SKColor _lineColor = new(0x9C, 0xCB, 0xFF, 0xFF);
        private const int AlphaBuckets = 16;
        private readonly SKPaint[] _pensByAlpha = new SKPaint[AlphaBuckets];
        private const double ConnectDistance = 130.0;

        public Constellation()
        {
            for (int i = 0; i < AlphaBuckets; i++)
            {
                byte a = (byte)((i + 1) * 0xC0 / AlphaBuckets);
                var c = new SKColor(_lineColor.Red, _lineColor.Green, _lineColor.Blue, a);
                _pensByAlpha[i] = new SKPaint { Color = c, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
            }
        }

        public void Init(SKSize canvasSize, double intensity)
        {
            _canvas = canvasSize;
            int count = (int)(70 * intensity * Math.Sqrt(_canvas.Width * _canvas.Height) / Math.Sqrt(1920 * 1080));
            count = Math.Max(20, Math.Min(180, count));
            _nodes = new Node[count];
            for (int i = 0; i < count; i++) _nodes[i] = NewNode();
        }

        private Node NewNode() => new()
        {
            X = _rng.NextDouble() * _canvas.Width,
            Y = _rng.NextDouble() * _canvas.Height,
            Vx = (-1 + _rng.NextDouble() * 2) * 35,
            Vy = (-1 + _rng.NextDouble() * 2) * 35,
        };

        public void Tick(double dt, SKCanvas canvas)
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                ref var n = ref _nodes[i];
                n.X += n.Vx * dt;
                n.Y += n.Vy * dt;
                if (n.X < 0)              { n.X = 0; n.Vx = -n.Vx; }
                if (n.X > _canvas.Width)  { n.X = _canvas.Width; n.Vx = -n.Vx; }
                if (n.Y < 0)              { n.Y = 0; n.Vy = -n.Vy; }
                if (n.Y > _canvas.Height) { n.Y = _canvas.Height; n.Vy = -n.Vy; }
            }

            double connectSq = ConnectDistance * ConnectDistance;
            for (int i = 0; i < _nodes.Length; i++)
                for (int j = i + 1; j < _nodes.Length; j++)
                {
                    double dx = _nodes[i].X - _nodes[j].X;
                    double dy = _nodes[i].Y - _nodes[j].Y;
                    double dsq = dx * dx + dy * dy;
                    if (dsq > connectSq) continue;
                    double a = 1.0 - Math.Sqrt(dsq) / ConnectDistance;
                    int bucket = Math.Clamp((int)(a * AlphaBuckets), 0, AlphaBuckets - 1);
                    canvas.DrawLine((float)_nodes[i].X, (float)_nodes[i].Y, (float)_nodes[j].X, (float)_nodes[j].Y, _pensByAlpha[bucket]);
                }

            for (int i = 0; i < _nodes.Length; i++)
                canvas.DrawOval((float)_nodes[i].X, (float)_nodes[i].Y, 1.6f, 1.6f, _nodeBrush);
        }

        public void Dispose()
        {
            _nodes = Array.Empty<Node>();
            _nodeBrush.Dispose();
            foreach (var p in _pensByAlpha) p?.Dispose();
        }
    }
}
