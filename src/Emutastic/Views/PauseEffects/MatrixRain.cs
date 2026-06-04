using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>Falling green katakana columns à la The Matrix. Each column has its own fall rate and a
    /// brighter "head" character. A paint is cached per brush-bucket so alpha is baked in (no per-draw
    /// opacity scope); SkiaSharp shapes glyphs per DrawText.</summary>
    public sealed class MatrixRain : IPauseEffect
    {
        public string Id => "matrix";
        public string DisplayName => "Matrix Rain";

        private struct Column { public double X, Head, VelocityY; public int Length; public char[] Chars; public double GlyphPhase; }

        private const double GlyphSize = 14;
        private Column[] _cols = Array.Empty<Column>();
        private SKSize _canvas;
        private readonly Random _rng = new();
        // Consolas isn't present on Linux — fall back through common monospaces.
        private readonly SKTypeface _face = SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;
        private readonly SKFont _font;
        private readonly float _ascent;
        private readonly SKPaint _bright;
        private readonly SKPaint[] _trailPaints;
        private const int TrailBuckets = 8;
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
            _font = new SKFont(_face, (float)GlyphSize);
            // Avalonia DrawText positions by TOP-left of the text box; SkiaSharp by baseline —
            // offset by -ascent so glyph positions match the original exactly.
            _ascent = -_font.Metrics.Ascent;
            _bright = new SKPaint { Color = new SKColor(0xC8, 0xFF, 0xC8, 0xFF), Style = SKPaintStyle.Fill, IsAntialias = true };
            _trailPaints = new SKPaint[TrailBuckets];
            for (int i = 0; i < TrailBuckets; i++)
            {
                byte a = (byte)((i + 1) * 0xFF / TrailBuckets);
                _trailPaints[i] = new SKPaint { Color = new SKColor(0x36, 0xC0, 0x42, a), Style = SKPaintStyle.Fill, IsAntialias = true };
            }
        }

        public void Init(SKSize canvasSize, double intensity)
        {
            _canvas = canvasSize;
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

        public void Tick(double dt, SKCanvas canvas)
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
                    SKPaint paint;
                    if (isHead)
                    {
                        paint = _bright;
                    }
                    else
                    {
                        double t = 1.0 - (double)j / col.Length;
                        int bk = (int)(t * 0.9 * TrailBuckets);
                        if (bk < 0) bk = 0; else if (bk >= TrailBuckets) bk = TrailBuckets - 1;
                        paint = _trailPaints[bk];
                    }
                    canvas.DrawText(col.Chars[j].ToString(), (float)col.X, (float)y + _ascent, SKTextAlign.Left, _font, paint);
                }
            }
        }

        public void Dispose()
        {
            _cols = Array.Empty<Column>();
            _font.Dispose();
            _face.Dispose();
            _bright.Dispose();
            foreach (var p in _trailPaints) p.Dispose();
        }
    }
}
