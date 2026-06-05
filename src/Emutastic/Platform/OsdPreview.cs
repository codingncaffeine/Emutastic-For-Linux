using System;
using System.IO;
using SkiaSharp;

namespace Emutastic.Platform
{
    /// <summary>Dev-only: renders the in-game OSD (GlOsd) over a representative game background to PNGs so
    /// the HUD/status aesthetics can be eyeballed against the Windows design. Invoked via --osd-preview.</summary>
    public static class OsdPreview
    {
        public static void Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            const int W = 940, H = 720;   // the real own-toplevel window size (4:3 game)

            void Render(string name, string status, string style, bool maximized, int titleHover,
                        float hudAlpha, int hover, bool paused,
                        (string, string, string, string, SKBitmap?)? raToast = null, bool hardcore = false)
            {
                using var surface = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
                using var c = new SKCanvas(surface);
                c.Clear(new SKColor(0x2E, 0x2E, 0x33));   // "desktop" behind, so the rounded corners read
                c.Save();
                // The shim erases corners to transparent on the real window; emulate that here by clipping
                // the game + overlay to the same rounded rect so the preview shows the rounding.
                if (!maximized)
                    c.ClipRoundRect(new SKRoundRect(new SKRect(0, 0, W, H), 10, 10), antialias: true);
                DrawFakeGame(c, W, H);

                var osd = new GlOsd();
                osd.Build(W, H, status, "Emutastic — Nestopia", style, maximized, titleHover, hudAlpha, hover, paused,
                          raToast: raToast, raToastAlpha: raToast != null ? 1f : 0f, hardcore: hardcore);
                var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using (var img = SKImage.FromPixelCopy(info, osd.Pixels, W * 4))
                    c.DrawImage(img, 0, 0);
                osd.Dispose();
                c.Restore();

                using var snap = SKImage.FromBitmap(surface);
                using var data = snap.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(Path.Combine(outDir, name + ".png"));
                data.SaveTo(fs);
                Console.WriteLine($"wrote {name}.png");
            }

            string running = "60 fps  (target 60)  core.Run avg 2.3ms";
            Render("01-hud-mac", running, "macOS", false, -1, 1f, -1, false);
            Render("02-hover-pause", running, "macOS", false, -1, 1f, GlOsd.BtnPause, false);
            Render("03-title-win11", running, "Windows11", false, -1, 1f, -1, false);
            Render("04-paused", "Paused  (target 60 fps)", "macOS", false, -1, 1f, -1, true);
            Render("05-title-linux", running, "Linux", false, -1, 1f, -1, false);
            Render("06-win11-closehover", running, "Windows11", false, GlOsd.TbClose, 1f, -1, false);
            // RA unlock toast (A8c, default style) + hardcore status treatment (A8d). The badge
            // here is a placeholder gold square — real ones come from media.retroachievements.org.
            using (var badge = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888)))
            {
                using (var bc = new SKCanvas(badge))
                {
                    bc.Clear(new SKColor(0x40, 0x30, 0x10));
                    using var star = new SKPaint { Color = new SKColor(0xFF, 0xD7, 0x00), IsAntialias = true };
                    bc.DrawCircle(32, 32, 20, star);
                }
                Render("07-ra-toast", running, "macOS", false, -1, 0f, -1, false,
                       ("ACHIEVEMENT UNLOCKED", "Ace Pilot", "Clear stage 1 without taking damage.", "10 points", badge));
            }
            Render("08-ra-hardcore", running, "macOS", false, -1, 1f, -1, false, null, hardcore: true);
        }

        // A representative NES-ish frame: gradient sky + a bright band near the bottom so we can judge the
        // HUD pill / status text contrast over both dark and bright content.
        private static void DrawFakeGame(SKCanvas c, int w, int h)
        {
            using (var sky = new SKPaint())
            {
                sky.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, h),
                    new[] { new SKColor(0x10, 0x20, 0x40), new SKColor(0x4A, 0x80, 0xC0) },
                    null, SKShaderTileMode.Clamp);
                c.DrawRect(new SKRect(0, 0, w, h), sky);
            }
            // bright ground strip (mimics a light platform under where the HUD sits)
            using (var ground = new SKPaint { Color = new SKColor(0xE0, 0xD0, 0x90) })
                c.DrawRect(new SKRect(0, h - 150, w, h), ground);
            using (var dirt = new SKPaint { Color = new SKColor(0xB0, 0x60, 0x30) })
                c.DrawRect(new SKRect(0, h - 110, w, h), dirt);
        }
    }
}
