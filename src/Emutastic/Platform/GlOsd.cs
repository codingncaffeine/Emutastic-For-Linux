using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Emutastic.Platform
{
    /// <summary>
    /// Renders the in-game OSD for the own-toplevel presenter — matching the Windows Emutastic design:
    /// the bottom status line (fps / target / core.Run avg) plus the hover HUD, a single rounded pill
    /// (#991C1C1E, r=28, h=56) holding Power · Pause · Reset · Save · Record · | · Cog with per-button
    /// hover highlight (#33FFFFFF) and a 150/300ms fade. Power uses the real powerbutton.png; the rest are
    /// vector glyphs. Save/Record/Cog are PLACEHOLDERS (drawn + clickable, no action wired yet). Skia draws
    /// into a window-sized straight-alpha RGBA8 buffer handed straight to the GL upload (no copy); rebuilds
    /// only when the content signature changes.
    /// </summary>
    public sealed class GlOsd : IDisposable
    {
        // Pill + button geometry (mirrors EmulatorWindow.xaml: pill h=56 r=28 pad 6,0; cell 54x56;
        // hover highlight inset 4,8 r=8; bottom margin 20; 1px separator before the cog).
        const float PillH = 56, PillRadius = 28, PillPadX = 6, CellW = 54, SepW = 9;
        const float StatusBarH = 24;                 // full-width bottom status bar (mirrors the Windows bar)
        const float BottomMargin = StatusBarH + 16;  // HUD pill sits this far up → clears the status bar
        public const float TitleBarHeight = 32f;     // top chrome (mirrors EmulatorWindow's 32px title bar)
        public const float StatusBarHeight = StatusBarH;
        const float CornerRadius = 10f;              // matches the main app's rounded window (CornerRadius=10)

        // Title-bar hit-test results.
        public const int TbMin = 0, TbMax = 1, TbClose = 2, TbDrag = 3;

        // Resize affordance: a thin grip on each edge + a generous square at each corner (so every corner
        // is easy to grab). Returns xdg_toplevel resize-edge bits (T=1,B=2,L=4,R=8; corners OR'd), or 0.
        public const int EdgeMargin = 7, CornerMargin = 18;
        public static int ResizeHitTest(int w, int h, int mx, int my)
        {
            bool ct = my < CornerMargin, cb = my >= h - CornerMargin, cl = mx < CornerMargin, cr = mx >= w - CornerMargin;
            if (ct && cl) return 5; if (ct && cr) return 9; if (cb && cl) return 6; if (cb && cr) return 10;
            if (my < EdgeMargin) return 1; if (my >= h - EdgeMargin) return 2;
            if (mx < EdgeMargin) return 4; if (mx >= w - EdgeMargin) return 8;
            return 0;
        }

        // wp_cursor_shape_device_v1 shape enum: the resize arrows + default.
        public const int CursorDefault = 1, CursorEw = 26, CursorNs = 27, CursorNesw = 28, CursorNwse = 29;
        public static int CursorShapeForEdge(int edge) => edge switch
        {
            4 or 8 => CursorEw,      // left / right
            1 or 2 => CursorNs,      // top / bottom
            5 or 10 => CursorNwse,   // top-left / bottom-right
            9 or 6 => CursorNesw,    // top-right / bottom-left
            _ => CursorDefault,
        };
        public const int BtnPower = 0, BtnPause = 1, BtnReset = 2, BtnSave = 3, BtnRecord = 4, BtnCog = 5;
        // Status-bar buttons (upstream's SaveStateBtn / LoadStateBtn — right-aligned in the status bar,
        // NOT in the HUD pill). Distinct id range so click dispatch can't confuse them with pill slots.
        public const int StatusBtnSave = 100, StatusBtnLoad = 101;

        // Layout slots, left→right. -1 = the non-clickable separator.
        private static readonly int[] Slots = { BtnPower, BtnPause, BtnReset, BtnSave, BtnRecord, -1, BtnCog };

        // Status-bar button metrics (mirrors SecondaryButtonStyle: Padding 10,4 / FontSize 11 / 6px gap).
        private const float SBtnH = StatusBarH - 8, SBtnGap = 6, SBtnRightPad = 12, SBtnTextSize = 12f, SBtnIconPad = 22f;
        private static void StatusButtonRects(int w, int h, out SKRect save, out SKRect load)
        {
            using var font = new SKFont { Size = SBtnTextSize };
            float saveW = SBtnIconPad + font.MeasureText("Save State") + 12;
            float loadW = SBtnIconPad + font.MeasureText("Load State") + 12;
            float top = h - StatusBarH + 4, bottom = top + SBtnH;
            load = new SKRect(w - SBtnRightPad - loadW, top, w - SBtnRightPad, bottom);
            save = new SKRect(load.Left - SBtnGap - saveW, top, load.Left - SBtnGap, bottom);
        }

        /// <summary>Status-bar button under (mx,my): StatusBtnSave / StatusBtnLoad / -1.</summary>
        public static int StatusHitTest(int w, int h, int mx, int my)
        {
            StatusButtonRects(w, h, out var save, out var load);
            if (save.Contains(mx, my)) return StatusBtnSave;
            if (load.Contains(mx, my)) return StatusBtnLoad;
            return -1;
        }

        // ── Save-state load picker (upstream's inline LoadPickerPanel: 5 most recent states) ──
        // Rows are (Name, RelativeTime); -2 in PickerHitTest = inside the panel but not on a row.
        public const int RowH = 26, PanelW = 280, PanelPad = 8;
        public static int PickerHitTest(int w, int h, int rowCount, int mx, int my)
        {
            if (rowCount < 0) return -1;                       // picker closed
            int rows = Math.Max(rowCount, 1);                  // empty shows one "no states" row
            float px, py, pw, ph;
            PickerRect(w, h, rows, out px, out py, out pw, out ph);
            if (mx < px || mx >= px + pw || my < py || my >= py + ph) return -1;
            int row = (int)((my - (py + PanelPad)) / RowH);
            return rowCount > 0 && row >= 0 && row < rowCount ? row : -2;
        }
        private static void PickerRect(int w, int h, int rows, out float x, out float y, out float pw, out float ph)
        {
            pw = PanelW;
            ph = rows * RowH + 2 * PanelPad;
            x = w - SBtnRightPad - pw;                         // right-aligned, like upstream's panel
            y = h - StatusBarH - 6 - ph;                       // floats just above the status bar buttons
        }

        private SKBitmap? _bmp;
        private SKCanvas? _canvas;
        private int _w, _h;
        private string _sig = "";
        private SKImage? _powerImg;
        private bool _powerTried;

        public IntPtr Pixels { get; private set; }
        public int Width => _w;
        public int Height => _h;

        private static float PillWidth()
        {
            float wsum = 2 * PillPadX;
            foreach (int s in Slots) wsum += s < 0 ? SepW : CellW;
            return wsum;
        }

        // Walk the slots, yielding each clickable button's cell rect (x,y,w=CellW,h=PillH) + its id.
        private static void ForEachButton(int w, int h, Action<int, float, float> visit)
        {
            float pillW = PillWidth();
            float x = (w - pillW) / 2f + PillPadX;
            float y = h - BottomMargin - PillH;
            foreach (int s in Slots)
            {
                if (s < 0) { x += SepW; continue; }
                visit(s, x, y);
                x += CellW;
            }
        }

        /// <summary>Which HUD button is under (mx,my), or -1. Caller gates on HUD visibility.</summary>
        public static int HitTest(int w, int h, int mx, int my)
        {
            int hit = -1;
            ForEachButton(w, h, (id, x, y) =>
            {
                if (mx >= x && mx < x + CellW && my >= y && my < y + PillH) hit = id;
            });
            return hit;
        }

        /// <summary>
        /// Render the OSD. <paramref name="hudAlpha"/> 0..1 fades the hover pill (status line is always
        /// shown). Returns true (and refreshes Pixels) only when the content changed since the last call.
        /// </summary>
        public bool Build(int w, int h, string status, string title, string winStyle, bool maximized,
                          int titleHover, float hudAlpha, int hoverBtn, bool paused,
                          IReadOnlyList<(string Name, string RelTime)>? picker = null, int pickerHover = -1,
                          int statusHover = -1)
        {
            if (w <= 0 || h <= 0) return false;
            int aq = (int)Math.Round(Math.Clamp(hudAlpha, 0f, 1f) * 16);   // quantize alpha → limit fade re-renders
            string pickSig = picker == null ? "" : $"{string.Join("\x1f", picker.Select(p => p.Name + p.RelTime))}|{pickerHover}";
            string sig = $"{w}x{h}|{status}|{title}|{winStyle}|{(maximized ? 1 : 0)}|{titleHover}|{aq}|{hoverBtn}|{(paused ? 1 : 0)}|{pickSig}|{statusHover}";
            if (sig == _sig && _bmp != null) return false;
            _sig = sig;

            if (_bmp == null || _w != w || _h != h)
            {
                _canvas?.Dispose(); _bmp?.Dispose();
                _bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                _canvas = new SKCanvas(_bmp);
                _w = w; _h = h; Pixels = _bmp.GetPixels();
            }

            var c = _canvas!;
            c.Clear(SKColors.Transparent);
            DrawTitleBar(c, w, title, winStyle, maximized, titleHover);
            DrawStatus(c, w, h, status, statusHover);
            if (aq > 0) DrawHud(c, w, h, hoverBtn, paused, aq / 16f);
            if (picker != null) DrawLoadPicker(c, w, h, picker, pickerHover);
            // Subtle rounded border at the window edge (the shim erases the corners to transparent so the
            // window reads as rounded; this traces the edge, matching the main app's 1px BorderSubtle).
            if (!maximized)
                using (var bp = new SKPaint { Color = new SKColor(0x2A, 0x2A, 0x2E, 0xFF), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
                    c.DrawRoundRect(new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f), CornerRadius, CornerRadius, bp);
            c.Flush();
            return true;
        }

        // Full-width bottom status bar (mirrors EmulatorWindow.xaml's status Border: BgSecondary fill, a
        // 1px top border, ~11px muted left-aligned text). Borderless own-toplevel has no chrome row, so the
        // bar overlays the very bottom edge of the game.
        private static void DrawStatus(SKCanvas c, int w, int h, string status, int statusHover)
        {
            float top = h - StatusBarH;
            using (var bar = new SKPaint { Color = new SKColor(0x16, 0x16, 0x19, 0xF0) })
                c.DrawRect(new SKRect(0, top, w, h), bar);
            using (var border = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x1F), StrokeWidth = 1f })
                c.DrawLine(0, top + 0.5f, w, top + 0.5f, border);

            // Right-aligned Save State / Load State buttons — upstream's status-bar pair
            // (SecondaryButtonStyle: subtle fill, 1px border, icon + 11pt label, hover lift).
            StatusButtonRects(w, h, out var saveR, out var loadR);
            DrawStatusBtn(c, saveR, "Save State", statusHover == StatusBtnSave, isLoad: false);
            DrawStatusBtn(c, loadR, "Load State", statusHover == StatusBtnLoad, isLoad: true);

            if (string.IsNullOrEmpty(status)) return;
            using var font = new SKFont { Size = 12.5f, Edging = SKFontEdging.Antialias };
            using var text = new SKPaint { Color = new SKColor(0xA8, 0xA8, 0xB2, 0xFF), IsAntialias = true };
            float baseY = top + StatusBarH / 2f + 4.5f;   // vertically centred in the bar
            // Clip the status text short of the buttons so a long line can't run beneath them.
            c.Save();
            c.ClipRect(new SKRect(0, top, saveR.Left - 10, h));
            c.DrawText(status, 12f, baseY, SKTextAlign.Left, font, text);
            c.Restore();
        }

        private static void DrawStatusBtn(SKCanvas c, SKRect r, string label, bool hot, bool isLoad)
        {
            using (var bg = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(hot ? 0x2E : 0x14)), IsAntialias = true })
                c.DrawRoundRect(r, 5f, 5f, bg);
            using (var bd = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x30), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
                c.DrawRoundRect(new SKRect(r.Left + .5f, r.Top + .5f, r.Right - .5f, r.Bottom - .5f), 5f, 5f, bd);
            float cy = (r.Top + r.Bottom) / 2f;
            using var g = new SKPaint { Color = new SKColor(0xE6, 0xE6, 0xEA, 0xFF), IsAntialias = true, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            float ix = r.Left + 12;
            if (isLoad) DrawLoadGlyph(c, ix, cy, 6f, g); else DrawSaveGlyph(c, ix, cy, 6f, g);
            using var font = new SKFont { Size = SBtnTextSize, Edging = SKFontEdging.Antialias };
            using var tp = new SKPaint { Color = new SKColor(0xE6, 0xE6, 0xEA, 0xFF), IsAntialias = true };
            c.DrawText(label, r.Left + SBtnIconPad, cy + 4.2f, SKTextAlign.Left, font, tp);
        }

        // Mini floppy (💾) for the Save State status button.
        private static void DrawSaveGlyph(SKCanvas c, float cx, float cy, float r, SKPaint p)
        {
            p.Style = SKPaintStyle.Stroke;
            using var body = new SKPath();
            body.MoveTo(cx - r, cy - r); body.LineTo(cx + r - 2.5f, cy - r); body.LineTo(cx + r, cy - r + 2.5f);
            body.LineTo(cx + r, cy + r); body.LineTo(cx - r, cy + r); body.Close();
            c.DrawPath(body, p);
            c.DrawRect(new SKRect(cx - r + 2, cy + 0.5f, cx + r - 2, cy + r - 1.5f), p);
        }

        // Mini open-folder (📂) for the Load State status button.
        private static void DrawLoadGlyph(SKCanvas c, float cx, float cy, float r, SKPaint p)
        {
            p.Style = SKPaintStyle.Stroke;
            using var f = new SKPath();
            f.MoveTo(cx - r, cy + r - 1); f.LineTo(cx - r, cy - r + 2); f.LineTo(cx - r + 4, cy - r + 2);
            f.LineTo(cx - r + 6, cy - r + 4.5f); f.LineTo(cx + r - 1, cy - r + 4.5f);
            c.DrawPath(f, p);
            using var flap = new SKPath();
            flap.MoveTo(cx - r, cy + r - 1); flap.LineTo(cx - r + 2.5f, cy - r + 6.5f); flap.LineTo(cx + r + 1, cy - r + 6.5f);
            flap.LineTo(cx + r - 1.5f, cy + r - 1); flap.Close();
            c.DrawPath(flap, p);
        }

        // ── Title bar (mirrors EmulatorWindow's CustomTitleBar + the WindowButtonStyle themes) ──
        private static bool IsWin11(string s) => string.Equals(s, "Windows11", StringComparison.OrdinalIgnoreCase);
        private static bool IsLinux(string s) => string.Equals(s, "Linux", StringComparison.OrdinalIgnoreCase);

        private static void TitleButtonRects(int w, string style, out SKRect min, out SKRect max, out SKRect close)
        {
            float cy = TitleBarHeight / 2f;
            if (IsWin11(style))
            {
                float bw = 46f;   // flush, top-right, no gap (Win11 caption buttons)
                close = new SKRect(w - bw, 0, w, TitleBarHeight);
                max = new SKRect(w - 2 * bw, 0, w - bw, TitleBarHeight);
                min = new SKRect(w - 3 * bw, 0, w - 2 * bw, TitleBarHeight);
            }
            else
            {
                float d = IsLinux(style) ? 24f : 13f, gap = 6f, right = w - 12f;
                close = new SKRect(right - d, cy - d / 2, right, cy + d / 2);
                max = new SKRect(right - 2 * d - gap, cy - d / 2, right - d - gap, cy + d / 2);
                min = new SKRect(right - 3 * d - 2 * gap, cy - d / 2, right - 2 * d - 2 * gap, cy + d / 2);
            }
        }

        /// <summary>Title-bar hit-test → TbMin/TbMax/TbClose (a control), TbDrag (draggable area), or -1.</summary>
        public static int TitleHitTest(int w, string style, int mx, int my)
        {
            if (my < 0 || my >= TitleBarHeight) return -1;
            TitleButtonRects(w, style, out var min, out var max, out var close);
            if (close.Contains(mx, my)) return TbClose;
            if (max.Contains(mx, my)) return TbMax;
            if (min.Contains(mx, my)) return TbMin;
            return TbDrag;
        }

        private static void DrawTitleBar(SKCanvas c, int w, string title, string style, bool maximized, int hover)
        {
            using (var bar = new SKPaint { Color = new SKColor(0x18, 0x18, 0x19, 0xF0) })
                c.DrawRect(new SKRect(0, 0, w, TitleBarHeight), bar);
            using (var border = new SKPaint { Color = new SKColor(0x1A, 0x1A, 0x1C, 0xFF), StrokeWidth = 1f })
                c.DrawLine(0, TitleBarHeight - 0.5f, w, TitleBarHeight - 0.5f, border);
            if (!string.IsNullOrEmpty(title))
            {
                using var font = new SKFont { Size = 12.5f, Edging = SKFontEdging.Antialias, Embolden = false };
                using var tp = new SKPaint { Color = new SKColor(0x8A, 0x8A, 0x90, 0xFF), IsAntialias = true };
                c.DrawText(title, 12f, TitleBarHeight / 2f + 4.5f, SKTextAlign.Left, font, tp);
            }

            TitleButtonRects(w, style, out var rMin, out var rMax, out var rClose);
            if (IsWin11(style))
            {
                DrawWin11Btn(c, rMin, TbMin, hover == TbMin, false);
                DrawWin11Btn(c, rMax, TbMax, hover == TbMax, maximized);
                DrawWin11Btn(c, rClose, TbClose, hover == TbClose, false);
            }
            else if (IsLinux(style))
            {
                DrawLinuxBtn(c, rMin, TbMin, hover == TbMin, false);
                DrawLinuxBtn(c, rMax, TbMax, hover == TbMax, maximized);
                DrawLinuxBtn(c, rClose, TbClose, hover == TbClose, false);
            }
            else   // macOS traffic-lights: yellow(min) green(max) red(close)
            {
                DrawMacDot(c, rMin, new SKColor(0xFE, 0xBC, 0x2E), hover == TbMin);
                DrawMacDot(c, rMax, new SKColor(0x28, 0xC8, 0x40), hover == TbMax);
                DrawMacDot(c, rClose, new SKColor(0xFF, 0x5F, 0x57), hover == TbClose);
            }
        }

        private static void DrawMacDot(SKCanvas c, SKRect r, SKColor col, bool hot)
        {
            using var p = new SKPaint { Color = hot ? col.WithAlpha(0xCC) : col, IsAntialias = true };
            c.DrawCircle(r.MidX, r.MidY, r.Width / 2f, p);
        }

        private static void DrawWin11Btn(SKCanvas c, SKRect r, int id, bool hot, bool maximized)
        {
            if (hot)
            {
                var bg = id == TbClose ? new SKColor(0xC4, 0x2B, 0x1C, 0xFF) : new SKColor(0xFF, 0xFF, 0xFF, 0x22);
                using var bp = new SKPaint { Color = bg };
                c.DrawRect(r, bp);
            }
            byte ga = (byte)((id == TbClose && hot) ? 0xFF : 0xF0);
            using var g = new SKPaint { Color = new SKColor(0xF0, 0xF0, 0xF0, ga), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, StrokeCap = SKStrokeCap.Butt };
            DrawWinControlGlyph(c, id, r.MidX, r.MidY, maximized, g);
        }

        private static void DrawLinuxBtn(SKCanvas c, SKRect r, int id, bool hot, bool maximized)
        {
            var bg = hot ? (id == TbClose ? new SKColor(0xE0, 0x4B, 0x4B, 0xFF) : new SKColor(0xFF, 0xFF, 0xFF, 0x40))
                         : new SKColor(0xFF, 0xFF, 0xFF, 0x26);
            using (var bp = new SKPaint { Color = bg, IsAntialias = true })
                c.DrawCircle(r.MidX, r.MidY, r.Width / 2f, bp);
            byte ga = (byte)((id == TbClose && hot) ? 0xFF : 0xF0);
            using var g = new SKPaint { Color = new SKColor(0xF0, 0xF0, 0xF0, ga), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };
            DrawWinControlGlyph(c, id, r.MidX, r.MidY, maximized, g);
        }

        // Crisp vector min / max(/restore) / close glyphs (avoids relying on box-drawing font glyphs).
        private static void DrawWinControlGlyph(SKCanvas c, int id, float cx, float cy, bool maximized, SKPaint p)
        {
            if (id == TbMin) { c.DrawLine(cx - 5.5f, cy, cx + 5.5f, cy, p); return; }
            if (id == TbClose) { c.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, p); c.DrawLine(cx - 5, cy + 5, cx + 5, cy - 5, p); return; }
            // max / restore
            if (maximized)
            {
                c.DrawRect(new SKRect(cx - 3, cy - 5, cx + 5, cy + 3), p);   // back square
                c.DrawRect(new SKRect(cx - 5, cy - 3, cx + 3, cy + 5), p);   // front square
            }
            else c.DrawRect(new SKRect(cx - 5, cy - 5, cx + 5, cy + 5), p);
        }

        private void DrawHud(SKCanvas c, int w, int h, int hoverBtn, bool paused, float fade)
        {
            byte A(byte a) => (byte)(a * fade);
            float pillW = PillWidth();
            float pillX = (w - pillW) / 2f, pillY = h - BottomMargin - PillH;

            // Pill background (matches #991C1C1E)
            using (var pill = new SKPaint { Color = new SKColor(0x1C, 0x1C, 0x1E, A(0x99)), IsAntialias = true })
                c.DrawRoundRect(new SKRect(pillX, pillY, pillX + pillW, pillY + PillH), PillRadius, PillRadius, pill);

            // Separator(s): a 1px vertical line, inset 14px top/bottom (matches the XAML Rectangle).
            float sx = pillX + PillPadX;
            foreach (int s in Slots)
            {
                if (s < 0)
                {
                    using var sep = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, A(0x44)), IsAntialias = true };
                    float lx = sx + SepW / 2f;
                    c.DrawLine(lx, pillY + 14, lx, pillY + PillH - 14, sep);
                    sx += SepW;
                }
                else sx += CellW;
            }

            ForEachButton(w, h, (id, x, y) =>
            {
                bool hot = id == hoverBtn;
                if (hot)
                    using (var hl = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, A(0x33)), IsAntialias = true })
                        c.DrawRoundRect(new SKRect(x + 4, y + 8, x + CellW - 4, y + PillH - 8), 8f, 8f, hl);

                float cx = x + CellW / 2f, cy = y + PillH / 2f;
                using var g = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, A(0xFF)), IsAntialias = true, StrokeWidth = 2.4f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
                switch (id)
                {
                    case BtnPower:  DrawPower(c, cx, cy, fade, g); break;
                    case BtnPause:  if (paused) DrawPlay(c, cx, cy, g); else DrawPauseBars(c, cx, cy, g); break;
                    case BtnReset:  DrawReset(c, cx, cy, g); break;
                    case BtnSave:   DrawSave(c, cx, cy, g); break;
                    case BtnRecord: DrawRecord(c, cx, cy, A(0xFF), g); break;
                    case BtnCog:    DrawCog(c, cx, cy, g); break;
                }
            });
        }

        // Power: the real powerbutton.png (≈44px), or a vector fallback (ring open at top + stem).
        private void DrawPower(SKCanvas c, float cx, float cy, float fade, SKPaint p)
        {
            var img = PowerImage();
            if (img != null)
            {
                // Fit within a 44x44 box preserving the PNG's aspect ratio (Windows used Stretch=Uniform).
                const float box = 44f;
                float scale = Math.Min(box / img.Width, box / img.Height);
                float dw = img.Width * scale, dh = img.Height * scale;
                var dest = new SKRect(cx - dw / 2, cy - dh / 2, cx + dw / 2, cy + dh / 2);
                using var ip = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * fade)), IsAntialias = true };
                c.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), ip);
                return;
            }
            float r = 11f;
            p.Style = SKPaintStyle.Stroke;
            using var path = new SKPath();
            path.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), -65, 290 + 130);
            c.DrawPath(path, p);
            c.DrawLine(cx, cy - r - 2f, cx, cy - 1f, p);
        }

        private static void DrawPauseBars(SKCanvas c, float cx, float cy, SKPaint p)
        {
            p.Style = SKPaintStyle.Fill;
            float bw = 4.2f, gap = 3.4f, bh = 20f;
            c.DrawRoundRect(new SKRect(cx - gap - bw, cy - bh / 2, cx - gap, cy + bh / 2), 1.6f, 1.6f, p);
            c.DrawRoundRect(new SKRect(cx + gap, cy - bh / 2, cx + gap + bw, cy + bh / 2), 1.6f, 1.6f, p);
        }

        private static void DrawPlay(SKCanvas c, float cx, float cy, SKPaint p)
        {
            p.Style = SKPaintStyle.Fill;
            using var path = new SKPath();
            path.MoveTo(cx - 7, cy - 10); path.LineTo(cx + 10, cy); path.LineTo(cx - 7, cy + 10); path.Close();
            c.DrawPath(path, p);
        }

        // Restart: a near-full ring with an arrowhead (Material "Restart").
        private static void DrawReset(SKCanvas c, float cx, float cy, SKPaint p)
        {
            float r = 10.5f;
            p.Style = SKPaintStyle.Stroke;
            using var path = new SKPath();
            path.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), -50, 280);
            c.DrawPath(path, p);
            double a = -50 * Math.PI / 180.0;
            float ax = cx + r * (float)Math.Cos(a), ay = cy + r * (float)Math.Sin(a);
            using var head = new SKPath();
            head.MoveTo(ax - 5f, ay - 1.5f); head.LineTo(ax, ay); head.LineTo(ax + 1f, ay - 6f);
            c.DrawPath(head, p);
        }

        // Load picker: a floating panel above the HUD pill — upstream's inline LoadPickerPanel
        // (name left, relative age right, hover row highlighted; "No save states yet" when empty).
        private void DrawLoadPicker(SKCanvas c, int w, int h, IReadOnlyList<(string Name, string RelTime)> items, int hoverRow)
        {
            int rows = Math.Max(items.Count, 1);
            PickerRect(w, h, rows, out float px, out float py, out float pw, out float ph);
            using (var bg = new SKPaint { Color = new SKColor(0x1C, 0x1C, 0x1E, 0xE6), IsAntialias = true })
                c.DrawRoundRect(new SKRect(px, py, px + pw, py + ph), 10f, 10f, bg);
            using (var bd = new SKPaint { Color = new SKColor(0x2A, 0x2A, 0x2E, 0xFF), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
                c.DrawRoundRect(new SKRect(px + .5f, py + .5f, px + pw - .5f, py + ph - .5f), 10f, 10f, bd);

            using var nameFont = new SKFont(SKTypeface.Default, 12f);
            using var timeFont = new SKFont(SKTypeface.Default, 11f);
            using var namePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var mutedPaint = new SKPaint { Color = new SKColor(0x9A, 0x9A, 0x9E, 0xFF), IsAntialias = true };

            if (items.Count == 0)
            {
                c.DrawText("No save states yet", px + PanelPad + 4, py + PanelPad + RowH - 9, SKTextAlign.Left, nameFont, mutedPaint);
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                float ry = py + PanelPad + i * RowH;
                if (i == hoverRow)
                    using (var hl = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x22), IsAntialias = true })
                        c.DrawRoundRect(new SKRect(px + 4, ry, px + pw - 4, ry + RowH), 6f, 6f, hl);

                string name = items[i].Name, rel = items[i].RelTime;
                float relW = timeFont.MeasureText(rel);
                float maxNameW = pw - 2 * PanelPad - relW - 16;
                while (name.Length > 1 && nameFont.MeasureText(name + "…") > maxNameW) name = name[..^1];
                if (name != items[i].Name) name += "…";
                c.DrawText(name, px + PanelPad + 4, ry + RowH - 8, SKTextAlign.Left, nameFont, namePaint);
                c.DrawText(rel, px + pw - PanelPad - 4 - relW, ry + RowH - 8, SKTextAlign.Left, timeFont, mutedPaint);
            }
        }

        // Save: a floppy disk (Material "ContentSave").
        private static void DrawSave(SKCanvas c, float cx, float cy, SKPaint p)
        {
            p.Style = SKPaintStyle.Stroke;
            float r = 10f;
            using var body = new SKPath();
            body.MoveTo(cx - r, cy - r); body.LineTo(cx + r - 4, cy - r); body.LineTo(cx + r, cy - r + 4);
            body.LineTo(cx + r, cy + r); body.LineTo(cx - r, cy + r); body.Close();
            c.DrawPath(body, p);
            p.Style = SKPaintStyle.Fill;
            c.DrawRect(new SKRect(cx - r + 3, cy - r, cx + r - 6, cy - r + 5), p);   // top shutter
            p.Style = SKPaintStyle.Stroke;
            c.DrawRect(new SKRect(cx - r + 3, cy + 1, cx + r - 3, cy + r - 2), p);    // label
        }

        // Record: a filled circle (Material "RecordCircle") — placeholder.
        private static void DrawRecord(SKCanvas c, float cx, float cy, byte a, SKPaint p)
        {
            p.Style = SKPaintStyle.Stroke;
            c.DrawCircle(cx, cy, 10f, p);
            p.Style = SKPaintStyle.Fill;
            c.DrawCircle(cx, cy, 5.5f, p);
        }

        // Cog: gear (Material "Cog"/"Settings") — placeholder.
        private static void DrawCog(SKCanvas c, float cx, float cy, SKPaint p)
        {
            p.Style = SKPaintStyle.Fill;
            float rOuter = 11f, rInner = 7.5f, toothW = 3.2f;
            for (int i = 0; i < 8; i++)
            {
                c.Save();
                c.RotateDegrees(i * 45f, cx, cy);
                c.DrawRoundRect(new SKRect(cx - toothW / 2, cy - rOuter, cx + toothW / 2, cy - rInner + 2.5f), 1f, 1f, p);
                c.Restore();
            }
            c.DrawCircle(cx, cy, rInner, p);
            // bore (punch a hole by clearing to transparent)
            using var clear = new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src, IsAntialias = true };
            c.DrawCircle(cx, cy, 3.2f, clear);
        }

        private SKImage? PowerImage()
        {
            if (_powerTried) return _powerImg;
            _powerTried = true;
            foreach (var cand in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "powerbutton.png"),
                "/home/eldritch/Projects/emutastic-linux/src/Emutastic/Assets/buttons/powerbutton.png",
            })
            {
                try
                {
                    if (!File.Exists(cand)) continue;
                    using var data = SKData.Create(cand);
                    _powerImg = SKImage.FromEncodedData(data);
                    if (_powerImg != null) break;
                }
                catch { }
            }
            return _powerImg;
        }

        public void Dispose()
        {
            _canvas?.Dispose(); _bmp?.Dispose(); _powerImg?.Dispose();
            _canvas = null; _bmp = null; _powerImg = null; Pixels = IntPtr.Zero;
        }
    }
}
