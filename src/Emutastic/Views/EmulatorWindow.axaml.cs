using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Emutastic.Emulator;
using Emutastic.Platform;

namespace Emutastic.Views
{
    /// <summary>
    /// M2 vertical-slice emulator window: hosts an <see cref="EmulatorSession"/>, blits its
    /// software frames into a WriteableBitmap on a 60 Hz UI timer, and feeds keyboard input to
    /// player 1 (so a ROM is playable without a gamepad).
    /// </summary>
    public partial class EmulatorWindow : Window
    {
        private readonly EmulatorSession _session;
        private readonly Image _screen;
        private Panel? _gameViewport;         // the video region the Vulkan overlay floats over
        private bool _vulkanActive;           // true once the present thread confirms the Vulkan overlay is live
        private bool _overlayFullscreen;      // F11 toggles overlay fullscreen (KWin direct-scanout = clean 60)
        private int _lastOvX = int.MinValue, _lastOvY, _lastOvW, _lastOvH; private bool _lastOvFs;
        private WriteableBitmap? _bmp;
        private int _bmpW, _bmpH;
        private long _lastSeq;
        private int _videoPending;           // 0/1 guard: at most one queued present at a time (push model)
        private DispatcherTimer? _statusTimer;
        private int _zeroFpsSeconds;

        // Avalonia Key -> libretro joypad id (player 1 keyboard fallback)
        private static readonly Dictionary<Key, int> KeyMap = new()
        {
            { Key.Up, 4 }, { Key.Down, 5 }, { Key.Left, 6 }, { Key.Right, 7 },
            { Key.Z, 0 },  // B
            { Key.X, 8 },  // A
            { Key.A, 1 },  // Y
            { Key.S, 9 },  // X
            { Key.Enter, 3 },      // START
            { Key.RightShift, 2 }, // SELECT
            { Key.Q, 10 }, // L
            { Key.W, 11 }, // R
        };

        // Parameterless ctor for the XAML designer/loader only.
        public EmulatorWindow() : this(CreateDesignSession()) { }

        private System.Diagnostics.TextWriterTraceListener? _fileLog;

        public EmulatorWindow(EmulatorSession session)
        {
            InitializeComponent();

            // Remember the game window's size/position across sessions (config is loaded at App
            // startup). Restore pre-show to avoid a resize flash; persist on close.
            RestoreWindowBounds();
            Closing += (_, _) => SaveWindowBounds();

            _session = session;
            _screen = this.FindControl<Image>("Screen")!;
            RenderOptions.SetBitmapInterpolationMode(_screen, BitmapInterpolationMode.None); // crisp pixels

            // Vulkan overlay: feed the present thread the video viewport's screen rect (+ fullscreen flag)
            // whenever layout or window position changes; hide the WriteableBitmap Image once the overlay
            // is confirmed live (or keep the Image on fallback).
            _gameViewport = this.FindControl<Panel>("GameViewport");
            _session.PresenterResolved += ok => Dispatcher.UIThread.Post(() => OnPresenterResolved(ok));
            if (_gameViewport != null)
            {
                _gameViewport.LayoutUpdated += (_, _) => PushOverlayGeometry();
                PositionChanged += (_, _) => PushOverlayGeometry();
            }

            SetupEmulatorLog(session);

            // Themed custom chrome (matches the rest of the app).
            Platform.WindowResize.Enable(this);
            var titleBar = this.FindControl<Grid>("CustomTitleBar")!;
            titleBar.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2) ToggleMaximize(); else BeginMoveDrag(e);
            };
            this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
            this.FindControl<Button>("MaximizeButton")!.Click += (_, _) => ToggleMaximize();
            this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

            // In-game overlay: HUD pill appears on mouse-move, the pause button freezes the game and
            // plays the saved pause-effect animation over the frozen frame.
            PointerMoved += (_, _) => ShowHud();
            this.FindControl<Button>("OverlayPowerBtn")!.Click += (_, _) => Close();
            this.FindControl<Button>("OverlayPauseBtn")!.Click += (_, _) => TogglePause();
            this.FindControl<Button>("OverlayResetBtn")!.Click += (_, _) => _session.RequestReset();

            Opened += OnOpened;
            Closed += OnClosed;
        }

        // Mirror all Trace output ([Emu]/[core:] etc.) to Logs/emulator.log for this session, so a
        // crash or core misbehavior is diagnosable post-hoc (matches upstream). Rotates at 5 MB.
        private void SetupEmulatorLog(EmulatorSession session)
        {
            try
            {
                string logDir = AppPaths.GetFolder("Logs");
                string logPath = System.IO.Path.Combine(logDir, "emulator.log");
                if (System.IO.File.Exists(logPath) && new System.IO.FileInfo(logPath).Length > 5 * 1024 * 1024)
                    System.IO.File.Move(logPath, System.IO.Path.Combine(logDir, "emulator.old.log"), overwrite: true);
                _fileLog = new System.Diagnostics.TextWriterTraceListener(logPath, "EmuFileLog")
                {
                    TraceOutputOptions = System.Diagnostics.TraceOptions.DateTime,
                };
                System.Diagnostics.Trace.Listeners.Add(_fileLog);
                System.Diagnostics.Trace.AutoFlush = true;
                System.Diagnostics.Trace.WriteLine($"[Emu] === session start: core={session.CoreName} ===");
            }
            catch { /* logging is best-effort */ }
        }

        private static EmulatorSession CreateDesignSession() => new("", "");

        private void OnOpened(object? sender, EventArgs e)
        {
            SetTitle("Emutastic — loading…");
            // GOLDEN RULE: never block the UI thread. Core dlopen + retro_load_game (which can be
            // slow for heavy cores / BIOS / CHD) runs on a background thread; we marshal back to the
            // UI thread only to start the frame timer (or report failure).
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = _session.Start(out string? error);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ok)
                    {
                        SetTitle("Emutastic — failed to start");
                        var sf = this.FindControl<TextBlock>("StatusText");
                        if (sf != null) sf.Text = error ?? "Failed to start";
                        System.Diagnostics.Trace.WriteLine($"[EmulatorWindow] start failed: {error}");
                        return;
                    }
                    SetTitle($"Emutastic — {_session.CoreName}");
                    // PUSH present: present each frame as the core produces it (single clock = the emu
                    // thread's pacing), matching upstream's OnVideoRefresh. A pulled present (timer or
                    // RequestAnimationFrame) adds a second clock that beats against production → chunky.
                    _session.FrameReady += OnFrameReady;
                    // Bottom status bar: real fps / target / core.Run avg, refreshed once a second.
                    _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
                    _statusTimer.Tick += (_, _) => UpdateStatus();
                    _statusTimer.Start();
                });
            });
        }

        // Bottom status bar: real produced-frame rate vs target + average retro_run cost. Mirrors
        // upstream's StatusText; the "Working…" hint catches a stalled core (e.g. a slow disc seek).
        private void UpdateStatus()
        {
            var st = this.FindControl<TextBlock>("StatusText");
            if (st == null) return;

            _session.SampleStats(out int frames, out double avgRunMs);
            double target = _session.TargetFps;

            if (_session.IsPaused)
            {
                _zeroFpsSeconds = 0;
                st.Text = $"Paused   (target {target:F0} fps)";
                return;
            }

            if (frames == 0) _zeroFpsSeconds++; else _zeroFpsSeconds = 0;
            string s = $"{frames} fps   (target {target:F0})   core.Run avg {avgRunMs:F1}ms";
            if (_zeroFpsSeconds >= 2) s += $"   ⏳ Working… ({_zeroFpsSeconds}s with no frame)";
            st.Text = s;
        }

        // Fired on the emu thread when a frame is produced. Marshal one blit to the UI thread at Render
        // priority, coalescing so a slow UI can't queue a backlog (the next FrameReady is dropped while
        // one present is pending; PumpFrame always blits the latest snapshot).
        private void OnFrameReady()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _videoPending, 1, 0) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                System.Threading.Interlocked.Exchange(ref _videoPending, 0);
                PumpFrame();
            }, DispatcherPriority.Render);
        }

        private void PumpFrame()
        {
            if (_vulkanActive) return;   // game is presented by the Vulkan surface on the emu thread
            if (!_session.TrySnapshot(ref _lastSeq, out byte[]? buf, out int w, out int h) || buf == null)
                return;

            if (_bmp == null || _bmpW != w || _bmpH != h)
            {
                // NOTE: per-console display-aspect-ratio (EmulatorSession.DisplayAspectRatio, incl.
                // TG16's 4:3 and matching upstream's universal core-AR) is a render-fidelity task on
                // its own — it must also invert AR for 90/270 rotation. Deferred; we keep native
                // pixel-ratio rendering (square DPI) here for now to avoid changing every game's AR.
                _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _bmpW = w; _bmpH = h;
                _screen.Source = _bmp;
            }

            using (var fb = _bmp.Lock())
            {
                int srcStride = w * 4;
                if (fb.RowBytes == srcStride)
                {
                    Marshal.Copy(buf, 0, fb.Address, buf.Length);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(buf, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
                }
            }
            _screen.InvalidateVisual();
        }

        // ── Vulkan overlay plumbing ──
        // Feed the present thread the video viewport's SCREEN rect (physical px) + fullscreen flag.
        // Deduped so repeated LayoutUpdated ticks with the same rect don't churn the overlay.
        private void PushOverlayGeometry()
        {
            if (_gameViewport == null) return;
            double scale = RenderScaling;
            int w = Math.Max(1, (int)(_gameViewport.Bounds.Width * scale));
            int h = Math.Max(1, (int)(_gameViewport.Bounds.Height * scale));
            int sx = 0, sy = 0;
            if (!_overlayFullscreen)
            {
                try { var tl = _gameViewport.PointToScreen(new Point(0, 0)); sx = tl.X; sy = tl.Y; }
                catch { return; }   // not laid out / not attached yet
            }
            if (sx == _lastOvX && sy == _lastOvY && w == _lastOvW && h == _lastOvH && _overlayFullscreen == _lastOvFs) return;
            _lastOvX = sx; _lastOvY = sy; _lastOvW = w; _lastOvH = h; _lastOvFs = _overlayFullscreen;
            _session.SetOverlayGeometry(sx, sy, w, h, _overlayFullscreen);
        }

        // Present thread resolved whether the Vulkan overlay is live: hide the WriteableBitmap Image (the
        // overlay window shows the game on top), or keep the Image (automatic fallback).
        private void OnPresenterResolved(bool ok)
        {
            _vulkanActive = ok;
            _screen.IsVisible = !ok;
            if (ok) PushOverlayGeometry();
        }

        private void ToggleOverlayFullscreen()
        {
            _overlayFullscreen = !_overlayFullscreen;
            PushOverlayGeometry();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.F11) { ToggleOverlayFullscreen(); e.Handled = true; return; }  // overlay fullscreen (scanout)
            if (ResolveRetroKey(e.Key, out int id)) { _session.Input.SetKeyboardButton(id, true); e.Handled = true; }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (ResolveRetroKey(e.Key, out int id)) { _session.Input.SetKeyboardButton(id, false); e.Handled = true; }
        }

        // Prefer the player-1 keyboard bindings saved in the Controls panel; fall back to the
        // built-in defaults when this console has no configured keyboard mapping.
        private bool ResolveRetroKey(Key key, out int id)
        {
            if (_session.Input.HasKeyboardConfig)
            {
                id = _session.Input.KeyboardRetroId(key.ToString());
                return id >= 0;
            }
            return KeyMap.TryGetValue(key, out id);
        }

        // ── In-game overlay (pause HUD + pause-effect animation) ──
        private PauseEffects.PauseEffectRunner? _pauseRunner;
        private DispatcherTimer? _hudHideTimer;

        private void ShowHud()
        {
            var hud = this.FindControl<StackPanel>("OverlayHud");
            if (hud == null) return;
            hud.IsVisible = true;
            hud.Opacity = 1;
            _hudHideTimer ??= CreateHudHideTimer();
            _hudHideTimer.Stop();
            _hudHideTimer.Start();
        }

        private DispatcherTimer CreateHudHideTimer()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_session.IsPaused) return;   // keep the HUD up while paused (matches upstream)
                var hud = this.FindControl<StackPanel>("OverlayHud");
                if (hud != null) { hud.Opacity = 0; hud.IsVisible = false; }
            };
            return t;
        }

        private void TogglePause()
        {
            bool willPause = !_session.IsPaused;
            _session.SetPaused(willPause);

            var host = this.FindControl<PauseEffects.PauseEffectHost>("PauseEffectOverlay")!;
            var glyph = this.FindControl<TextBlock>("OverlayPauseGlyph");

            if (willPause)
            {
                _pauseRunner ??= new PauseEffects.PauseEffectRunner(host);
                var cfg = App.Configuration?.GetThemeConfiguration();
                string id = cfg?.PauseEffect ?? "none";
                double intensity = Math.Clamp(cfg?.PauseEffectIntensity ?? 1.0, 0.5, 2.0);
                var entry = PauseEffects.PauseEffectRegistry.Find(id);
                if (entry != null && entry.Id != PauseEffects.PauseEffectRegistry.NoneId)
                {
                    // Only show the host when an effect actually runs — otherwise its 30% shade
                    // would dim the frozen frame (and, since the runner no-ops for "none", never clear).
                    host.IsVisible = true;
                    var inst = entry.Factory();
                    if (entry.IsPixel) _pauseRunner.Start((PauseEffects.IPixelPauseEffect)inst, intensity);
                    else _pauseRunner.Start((PauseEffects.IPauseEffect)inst, intensity);
                }
                if (glyph != null) glyph.Text = "▶";
                ShowHud();   // keep HUD visible while paused
            }
            else
            {
                _pauseRunner?.Stop();      // fades out, then hides the host itself
                host.IsVisible = false;    // belt-and-braces for the "none" path (runner never showed it)
                if (glyph != null) glyph.Text = "⏸";
                ShowHud();                 // restart the auto-hide countdown
            }
        }

        private void SetTitle(string t)
        {
            Title = t;
            var tb = this.FindControl<TextBlock>("TitleText");
            if (tb != null) tb.Text = t;
        }

        private void ToggleMaximize()
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        // ── Game-window size/position persistence (same scheme as MainWindow, separate keys) ──
        private void RestoreWindowBounds()
        {
            try
            {
                var cfg = App.Configuration;
                if (cfg == null) return;

                double w = cfg.GetValue("emuWinWidth", 0.0);
                double h = cfg.GetValue("emuWinHeight", 0.0);
                int x = cfg.GetValue("emuWinLeft", int.MinValue);
                int y = cfg.GetValue("emuWinTop", int.MinValue);
                bool maximized = cfg.GetValue("emuWinMaximized", false);

                if (w >= MinWidth && h >= MinHeight)
                {
                    Width = w;
                    Height = h;
                }
                if (x != int.MinValue && y != int.MinValue)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = new PixelPoint(x, y);
                }
                if (maximized)
                    WindowState = WindowState.Maximized;
            }
            catch { /* fall back to the XAML default size */ }
        }

        private void SaveWindowBounds()
        {
            try
            {
                var cfg = App.Configuration;
                if (cfg == null) return;

                cfg.SetValue("emuWinMaximized", WindowState == WindowState.Maximized);
                if (WindowState == WindowState.Normal)
                {
                    cfg.SetValue("emuWinWidth", ClientSize.Width);
                    cfg.SetValue("emuWinHeight", ClientSize.Height);
                    cfg.SetValue("emuWinLeft", Position.X);
                    cfg.SetValue("emuWinTop", Position.Y);
                }
                // Off the UI thread — SaveAsync captures the UI context across its awaits, which can
                // deadlock against a blocking save on close (see MainWindow.SaveWindowBounds).
                _ = System.Threading.Tasks.Task.Run(() => cfg.SaveAsync());
            }
            catch { /* best-effort on close */ }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _session.FrameReady -= OnFrameReady;   // stop presenting before teardown
            _statusTimer?.Stop();
            _hudHideTimer?.Stop();
            _pauseRunner?.Dispose();
            // GOLDEN RULE: Dispose joins the emu thread (up to 5s) and tears down native resources —
            // never do that on the UI thread. Run teardown on a background thread.
            var session = _session;
            System.Threading.Tasks.Task.Run(() => session.Dispose());

            // Remove this session's file-log listener so launches don't accumulate duplicate writers.
            if (_fileLog != null)
            {
                System.Diagnostics.Trace.WriteLine("[Emu] === session end ===");
                try { System.Diagnostics.Trace.Flush(); System.Diagnostics.Trace.Listeners.Remove(_fileLog); _fileLog.Dispose(); } catch { }
                _fileLog = null;
            }
        }
    }
}
