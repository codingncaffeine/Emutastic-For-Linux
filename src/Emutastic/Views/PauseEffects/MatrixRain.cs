using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>Falling green katakana columns à la The Matrix. Each column has its own fall rate and a
    /// brighter "head" character. FormattedText is cached per (glyph, brush-bucket) to avoid per-frame
    /// shaping churn; alpha is baked into the brush so there's no PushOpacity cost.</summary>
    public sealed class MatrixRain : IPauseEffect
    {
        public string Id => "matrix";
        public string DisplayName => "Matrix Rain";

        private struct Column { public double X, Head, VelocityY; public int Length; public char[] Chars; public double GlyphPhase; }

        private const double GlyphSize = 14;
        private Column[] _cols = Array.Empty<Column>();
        private Size _canvas;
        private readonly Random _rng = new();
        // Consolas isn't present on Linux — fall back through common monospaces.
        private readonly Typeface _face = new(new FontFamily("Consolas, Liberation Mono, DejaVu Sans Mono, monospace"),
            FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
        private readonly IBrush _bright = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xFF, 0xC8));
        private readonly IBrush[] _trailBrushes;
        private const int TrailBuckets = 8;
        private readonly Dictionary<int, FormattedText> _glyphCache = new();
        private static readonly char[] Glyphs;

        static MatrixRain()
        {
            var list = new List<char>();
            for (char c = 'ｦ'; c <= 'ﾝ'; c++) list.Add(c);
            for (char c = '0'; c <= '9'; c++) list.Add(c);
            for (char c = 'A'; c <= 'Z'; c++) list.Add(c);
            Glyphs = list.ToArray();
        }

        public MatrixRain()
        {
            _trailBrushes = new IBrush[TrailBuckets];
            for (int i = 0; i < TrailBuckets; i++)
            {
                byte a = (byte)((i + 1) * 0xFF / TrailBuckets);
                _trailBrushes[i] = new SolidColorBrush(Color.FromArgb(a, 0x36, 0xC0, 0x42));
            }
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            _glyphCache.Clear();
            int colCount = Math.Max(8, (int)(_canvas.Width / GlyphSize));
            _cols = new Column[colCount];
            for (int i = 0; i < colCount; i++) _cols[i] = NewColumn(i, intensity, randomStartY: true);
        }

        private Column NewColumn(int index, double intensity, bool randomStartY)
        {
            int len = 6 + _rng.Next(20);
            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = Glyphs[_rng.Next(Glyphs.Length)];
            return new Column
            {
                X = index * GlyphSize + 2,
                Head = randomStartY ? _rng.NextDouble() * _canvas.Height : -GlyphSize,
                VelocityY = (60 + _rng.NextDouble() * 140) * (0.6 + intensity * 0.5),
                Length = len,
                Chars = chars,
                GlyphPhase = 0,
            };
        }

        public void Tick(double dt, DrawingContext dc)
        {
            for (int i = 0; i < _cols.Length; i++)
            {
                ref var col = ref _cols[i];
                col.Head += col.VelocityY * dt;
                col.GlyphPhase += dt;
                if (col.GlyphPhase > 0.08)
                {
                    col.GlyphPhase = 0;
                    col.Chars[_rng.Next(col.Chars.Length)] = Glyphs[_rng.Next(Glyphs.Length)];
                }
                if (col.Head - col.Length * GlyphSize > _canvas.Height)
                    col = NewColumn(i, 1.0, randomStartY: false);

                for (int j = 0; j < col.Length; j++)
                {
                    double y = col.Head - j * GlyphSize;
                    if (y < -GlyphSize || y > _canvas.Height) continue;
                    bool isHead = j == 0;
                    IBrush brush;
                    int brushBucket;
                    if (isHead)
                    {
                        brush = _bright;
                        brushBucket = TrailBuckets;
                    }
                    else
                    {
                        double t = 1.0 - (double)j / col.Length;
                        int bk = (int)(t * 0.9 * TrailBuckets);
                        if (bk < 0) bk = 0; else if (bk >= TrailBuckets) bk = TrailBuckets - 1;
                        brushBucket = bk;
                        brush = _trailBrushes[bk];
                    }
                    int key = (col.Chars[j] << 4) | brushBucket;
                    if (!_glyphCache.TryGetValue(key, out var ft))
                    {
                        ft = new FormattedText(col.Chars[j].ToString(), CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, _face, GlyphSize, brush);
                        _glyphCache[key] = ft;
                    }
                    dc.DrawText(ft, new Point(col.X, y));
                }
            }
        }

        public void Dispose() { _cols = Array.Empty<Column>(); _glyphCache.Clear(); }
    }
}
