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
        private WriteableBitmap? _bmp;
        private int _bmpW, _bmpH;
        private long _lastSeq;
        private DispatcherTimer? _timer;

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
            _session = session;
            _screen = this.FindControl<Image>("Screen")!;
            RenderOptions.SetBitmapInterpolationMode(_screen, BitmapInterpolationMode.None); // crisp pixels

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
                        System.Diagnostics.Trace.WriteLine($"[EmulatorWindow] start failed: {error}");
                        return;
                    }
                    SetTitle($"Emutastic — {_session.CoreName}");
                    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0) };
                    _timer.Tick += (_, _) => PumpFrame();
                    _timer.Start();
                });
            });
        }

        private void PumpFrame()
        {
            if (!_session.TrySnapshot(ref _lastSeq, out byte[]? buf, out int w, out int h) || buf == null)
                return;

            if (_bmp == null || _bmpW != w || _bmpH != h)
            {
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
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

        private void SetTitle(string t)
        {
            Title = t;
            var tb = this.FindControl<TextBlock>("TitleText");
            if (tb != null) tb.Text = t;
        }

        private void ToggleMaximize()
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnClosed(object? sender, EventArgs e)
        {
            _timer?.Stop();
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
