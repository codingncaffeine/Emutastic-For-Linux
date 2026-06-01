using System;
using Avalonia;
using Avalonia.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>Synthwave / Tron-style perspective neon grid receding to a horizon. Horizontal lines
    /// scroll toward the viewer; vertical lines fan out from the vanishing point.</summary>
    public sealed class SynthwaveGrid : IPauseEffect
    {
        public string Id => "synthwave";
        public string DisplayName => "Synthwave Grid";

        private Size _canvas;
        private double _scroll;
        private double _scrollSpeed = 0.3;
        private readonly IPen _gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x4B, 0xCB)), 1.2);
        private readonly IBrush _horizonGlow;

        public SynthwaveGrid()
        {
            _horizonGlow = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x99, 0x66, 0x22, 0x99), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1.0),
                },
            };
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            _scrollSpeed = 0.2 + 0.4 * intensity;
        }

        public void Tick(double dt, DrawingContext dc)
        {
            _scroll = (_scroll + _scrollSpeed * dt) % 1.0;

            double w = _canvas.Width, h = _canvas.Height;
            double horizonY = h * 0.55, cx = w / 2.0;

            dc.DrawRectangle(_horizonGlow, null, new Rect(0, horizonY - 12, w, h * 0.45));

            int rows = 14;
            for (int i = 0; i < rows; i++)
            {
                double t = (i + _scroll) / rows;
                t = t * t * t;
                double y = horizonY + (h - horizonY) * t;
                if (y < horizonY || y > h) continue;
                double opacity = (1.0 - t) * 0.9 + 0.1;
                using (dc.PushOpacity(opacity))
                    dc.DrawLine(_gridPen, new Point(0, y), new Point(w, y));
            }

            int cols = 22;
            for (int i = -cols; i <= cols; i++)
            {
                double bx = cx + (i / (double)cols) * (w * 1.5);
                dc.DrawLine(_gridPen, new Point(cx, horizonY), new Point(bx, h));
            }
        }

        public void Dispose() { }
    }
}
