using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    /// <summary>
    /// Single source of truth for achievement-toast appearance (port of upstream
    /// ToastStyleRenderer). On Linux it styles the PREVIEW replica in Preferences;
    /// the live in-game toast renders in Skia (GlOsd.DrawRaToast) from the same
    /// AchievementToastStyle fields, so both surfaces stay in lockstep.
    /// </summary>
    public static class ToastStyleRenderer
    {
        private const double EdgeMargin = 20;  // gap from chosen screen corner/edge

        public static void ApplyTo(
            Border root, Border badge, TextBlock header, TextBlock title,
            TextBlock desc, TextBlock points, AchievementToastStyle? style,
            Func<string, Avalonia.Media.Imaging.Bitmap?>? imageLoader)
        {
            // Null-guard: a hand-edited "toastStyle": null must never throw.
            var s = style ?? new AchievementToastStyle();

            root.Background = BuildBackground(s, imageLoader);
            root.BorderBrush = ResolveBrush(s.BorderColor, "#E03535");
            root.BorderThickness = new Thickness(s.BorderThickness);
            root.CornerRadius = new CornerRadius(ResolveRadius(s));

            // Drop shadow — direction fixed downward, like the shipped toast.
            root.BoxShadow = s.ShadowEnabled
                ? new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = s.ShadowDepth,
                    Blur = s.ShadowBlur,
                    Color = WithOpacity(ParseColor(s.ShadowColor, Colors.Black), Clamp01(s.ShadowOpacity / 100.0)),
                })
                : default;

            ApplyPosition(root, s.Position);

            badge.IsVisible = s.ShowBadge;
            badge.BorderBrush = ResolveBrush(s.BadgeFrameColor, "#FFD700");

            header.IsVisible = s.ShowHeader;
            header.Foreground = ResolveBrush(s.HeaderColor, "#FFD700");
            header.FontSize = s.HeaderSize;

            title.Foreground = ResolveBrush(s.TitleColor, "#FFFFFF");
            title.FontFamily = ResolveFont(s.TitleFont);
            title.FontSize = s.TitleSize;

            desc.Foreground = ResolveBrush(s.DescColor, "#CCFFFFFF");
            desc.FontFamily = ResolveFont(s.DescFont);
            desc.FontSize = s.DescSize;

            points.Foreground = ResolveBrush(s.PointsColor, "#FFD700");
            points.FontSize = s.PointsSize;
        }

        public static TimeSpan Duration(AchievementToastStyle? style)
        {
            var s = style ?? new AchievementToastStyle();
            double secs = s.DurationSec > 0.5 ? s.DurationSec : 4;
            return TimeSpan.FromSeconds(secs);
        }

        private static IBrush BuildBackground(AchievementToastStyle s, Func<string, Avalonia.Media.Imaging.Bitmap?>? imageLoader)
        {
            double bgOpacity = Clamp01(s.BackgroundOpacity / 100.0);

            // Image wins when set and loadable.
            if (!string.IsNullOrWhiteSpace(s.BackgroundImage) && imageLoader != null)
            {
                var img = imageLoader(s.BackgroundImage);
                if (img != null)
                    return new ImageBrush(img) { Stretch = Stretch.UniformToFill, Opacity = bgOpacity };
            }

            // Gradient (default — the shipped 2-stop horizontal gradient).
            if (s.UseGradient)
            {
                return new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    Opacity = bgOpacity,
                    GradientStops =
                    {
                        new GradientStop(ParseColor(s.GradientStart, Color.FromArgb(0xF2, 0x1A, 0x1A, 0x2E)), 0),
                        new GradientStop(ParseColor(s.GradientEnd,   Color.FromArgb(0xC8, 0x1A, 0x1A, 0x2E)), 1),
                    },
                };
            }

            return new SolidColorBrush(ParseColor(s.BackgroundColor, Color.FromRgb(0x1A, 0x1A, 0x2E)))
            { Opacity = bgOpacity };
        }

        public static double ResolveRadius(AchievementToastStyle s)
        {
            switch ((s.Shape ?? "Card").Trim().ToLowerInvariant())
            {
                case "sharp":   return 0;
                case "rounded": return 20;
                // A huge radius clamps to the box → stadium/pill silhouette.
                case "pill":    return 1000;
                case "custom":  return Math.Max(0, s.CornerRadius);
                case "card":
                default:        return 12;
            }
        }

        private static void ApplyPosition(Border root, string? position)
        {
            HorizontalAlignment h; VerticalAlignment v;
            double l = 0, t = 0, r = 0, b = 0;
            switch ((position ?? "TopCenter").Trim().ToLowerInvariant())
            {
                case "topleft":      h = HorizontalAlignment.Left;   v = VerticalAlignment.Top;    l = EdgeMargin; t = EdgeMargin; break;
                case "topright":     h = HorizontalAlignment.Right;  v = VerticalAlignment.Top;    r = EdgeMargin; t = EdgeMargin; break;
                case "bottomleft":   h = HorizontalAlignment.Left;   v = VerticalAlignment.Bottom; l = EdgeMargin; b = EdgeMargin; break;
                case "bottomcenter": h = HorizontalAlignment.Center; v = VerticalAlignment.Bottom; b = EdgeMargin; break;
                case "bottomright":  h = HorizontalAlignment.Right;  v = VerticalAlignment.Bottom; r = EdgeMargin; b = EdgeMargin; break;
                case "topcenter":
                default:             h = HorizontalAlignment.Center; v = VerticalAlignment.Top;    t = EdgeMargin; break;
            }
            root.HorizontalAlignment = h;
            root.VerticalAlignment = v;
            root.Margin = new Thickness(l, t, r, b);
        }

        public static Color ParseColor(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try { return Color.Parse(hex.Trim()); } catch { return fallback; }
        }

        private static IBrush ResolveBrush(string? hex, string fallbackHex)
            => new SolidColorBrush(ParseColor(hex, Color.Parse(fallbackHex)));

        private static Color WithOpacity(Color c, double opacity)
            => new Color((byte)(c.A * opacity), c.R, c.G, c.B);

        private static FontFamily ResolveFont(string? name)
            => string.IsNullOrWhiteSpace(name) ? FontFamily.Default : new FontFamily(name);

        private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
    }
}
