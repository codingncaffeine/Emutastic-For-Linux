using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using Emutastic.Emulator;
using Emutastic.Models;
using Emutastic.Services;
using LibVLCSharp.Shared;

namespace Emutastic.Views;

/// <summary>
/// U4a — the per-game detail card (port of upstream GameDetailWindows.xaml.cs).
/// Modal overlay card: art/header, metadata + play stats, description, play/favorite/more.
/// The RetroAchievements panel lands in U8 (kept collapsed); the LibVLC trailer video
/// lands in U4b (cover art shows for now).
/// </summary>
public partial class GameDetailWindow : Window
{
    private readonly Game _game;
    private readonly DatabaseService _db = new();
    private volatile bool _closed;

    // LibVLC snap-trailer playback (U4b). The display callback marshals to the UI
    // thread and memcpys _videoBuffer → _videoBitmap; the bail-out guards are nulled
    // in OnClosed BEFORE the buffer is freed so a late callback can't touch freed memory.
    private MediaPlayer? _vlcPlayer;
    private WriteableBitmap? _videoBitmap;
    private IntPtr _videoBuffer;
    private bool _crossfadeDone;

    public GameDetailWindow() : this(new Game { Title = "Game", Console = "NES" }) { }

    public GameDetailWindow(Game game)
    {
        InitializeComponent();
        _game = game;

        this.FindControl<Border>("Overlay")!.PointerPressed += (_, _) => Close();
        this.FindControl<Border>("CloseButton")!.PointerPressed += (_, _) => Close();
        this.FindControl<Button>("PlayButton")!.Click += PlayButton_Click;
        this.FindControl<Button>("FavoriteButton")!.Click += FavoriteButton_Click;
        this.FindControl<Button>("MoreButton")!.Click += MoreButton_Click;

        PopulateData();
        SetupAnimateIn();
        _ = LoadSnapAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Signal in-flight async work (placeholder decode, snap-video worker) to drop
        // its results instead of writing into a dead window.
        _closed = true;

        if (_vlcPlayer != null)
        {
            try { _vlcPlayer.Stop(); _vlcPlayer.Dispose(); } catch { }
            _vlcPlayer = null;
        }

        // The display callback posts to the dispatcher, so a queued blit can outlive
        // Dispose. Null the guards BEFORE freeing the buffer so any late callback bails
        // instead of memcpying into freed memory.
        _videoBitmap = null;
        var buf = _videoBuffer;
        _videoBuffer = IntPtr.Zero;
        if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);

        base.OnClosed(e);
    }

    private void PopulateData()
    {
        Get<TextBlock>("GameTitle").Text = _game.Title;
        Get<TextBlock>("ConsoleTag").Text = _game.Console;
        Get<TextBlock>("ArtPlaceholderText").Text = _game.Title;

        bool hasYear  = _game.Year > 0;
        bool hasDev   = !string.IsNullOrEmpty(_game.Developer);
        bool hasGenre = !string.IsNullOrEmpty(_game.Genre);
        bool hasDesc  = !string.IsNullOrEmpty(_game.Description);

        if (hasYear || hasDev || hasGenre)
        {
            Get<WrapPanel>("MetadataPanel").IsVisible = true;
            if (hasYear)
            {
                Get<Border>("YearPill").IsVisible = true;
                Get<TextBlock>("GameYear").Text = _game.Year.ToString();
            }
            if (hasDev)
            {
                Get<Border>("DeveloperPill").IsVisible = true;
                var devText = !string.IsNullOrEmpty(_game.Publisher) && _game.Publisher != _game.Developer
                    ? $"{_game.Developer}  ·  {_game.Publisher}"
                    : _game.Developer;
                var devBlock = Get<TextBlock>("GameDeveloper");
                devBlock.Text = devText;
                // Full name on hover, since the pill ellipsis-truncates at MaxWidth (upstream ToolTip).
                ToolTip.SetTip(devBlock, devText);
            }
            if (hasGenre)
            {
                Get<Border>("GenrePill").IsVisible = true;
                string genre = _game.Genre;
                int comma = genre.IndexOf(',');
                Get<TextBlock>("GameGenre").Text = comma > 0 ? genre.Substring(0, comma) : genre;
            }
        }

        if (hasDesc)
        {
            Get<ScrollViewer>("GameDescriptionScroll").IsVisible = true;
            Get<TextBlock>("GameDescription").Text = _game.Description;
        }

        UpdateStatPills();
        Get<Border>("FavoriteBadge").IsVisible = _game.IsFavorite;
        Get<Button>("FavoriteButton").Content = _game.IsFavorite ? "♥  Favorited" : "♡  Favorite";

        try
        {
            var brush = this.FindControl<Border>("ArtBackground")!.Background as SolidColorBrush;
            if (brush != null) brush.Color = Color.Parse(_game.BackgroundColor);
        }
        catch { /* malformed color → keep default */ }
    }

    private void RefreshStats() => UpdateStatPills();

    // Inline play-stat pills; each hides when its value is zero/Never.
    private void UpdateStatPills()
    {
        int plays = _game.PlayCount;
        int totalSec = _game.TotalPlayTimeSeconds;
        bool everPlayed = _game.LastPlayed.HasValue;

        if (plays > 0)
        {
            Get<TextBlock>("StatPlayed").Text = plays == 1 ? "1 play" : $"{plays} plays";
            Get<Border>("PlayedPill").IsVisible = true;
        }
        else Get<Border>("PlayedPill").IsVisible = false;

        if (totalSec > 0)
        {
            Get<TextBlock>("StatPlayTime").Text = FormatDuration(totalSec);
            Get<Border>("PlayTimePill").IsVisible = true;
        }
        else Get<Border>("PlayTimePill").IsVisible = false;

        if (everPlayed)
        {
            Get<TextBlock>("StatLastPlayed").Text = _game.LastPlayedDisplay;
            Get<Border>("LastPlayedPill").IsVisible = true;
        }
        else Get<Border>("LastPlayedPill").IsVisible = false;
    }

    private static string FormatDuration(int sec)
    {
        if (sec <= 0) return "—";
        if (sec < 60) return $"{sec}s";
        if (sec < 3600) return $"{sec / 60}m";
        double h = sec / 3600.0;
        return h < 100 ? $"{h:0.#}h" : $"{(int)h}h";
    }

    // ── Snap loading: cover art placeholder → ScreenScraper video → static snap ──
    private async Task LoadSnapAsync()
    {
        try
        {
            // Show cover art immediately as a placeholder while a video loads.
            await ShowCoverArtPlaceholderAsync();

            string romPath = AppPaths.FromStoragePath(_game.RomPath);

            // 1 — ScreenScraper video snap if configured (cache first, then network).
            var snapConfig = App.Configuration?.GetSnapConfiguration();
            if (snapConfig is { ScreenScraperEnabled: true } && !string.IsNullOrWhiteSpace(snapConfig.ScreenScraperUser))
            {
                var ss = new ScreenScraperService();
                string? cached = ss.FindCachedSnap(_game.RomHash, _game.Console)
                    ?? await ss.FetchSnapAsync(snapConfig.ScreenScraperUser, snapConfig.ScreenScraperPassword,
                                               _game.Console, _game.RomHash, romPath);
                if (cached != null && !_closed)
                {
                    await PlaySnapVideoAsync(cached);
                    return;
                }
            }

            // 2 — fall back to a static libretro screenshot (off-thread decode).
            string? snapPath = await new ArtworkService().FetchSnapAsync(_game.RomHash, romPath, _game.Console);
            if (snapPath == null || !System.IO.File.Exists(snapPath)) return;

            var bmp = await Task.Run(() => Decode(snapPath, 920));
            if (_closed || bmp == null) return;
            var header = Get<Image>("HeaderImage");
            header.Source = bmp;
            header.IsVisible = true;
            Get<TextBlock>("ArtPlaceholderText").IsVisible = false;
        }
        catch { /* cosmetic — silently ignore */ }
    }

    private async Task ShowCoverArtPlaceholderAsync()
    {
        string artPath = _game.DisplayArtPath;
        if (string.IsNullOrEmpty(artPath) || !System.IO.File.Exists(artPath)) return;
        try
        {
            var bmp = await Task.Run(() => Decode(artPath, 920));
            if (_closed || bmp == null) return;
            var header = Get<Image>("HeaderImage");
            header.Source = bmp;
            header.IsVisible = true;
            Get<TextBlock>("ArtPlaceholderText").IsVisible = false;
        }
        catch { }
    }

    private static Bitmap? Decode(string path, int width)
    {
        try { using var fs = System.IO.File.OpenRead(path); return Bitmap.DecodeToWidth(fs, width); }
        catch { return null; }
    }

    // Play a ScreenScraper MP4 snap into VideoImage via LibVLC video callbacks.
    private async Task PlaySnapVideoAsync(string mp4Path)
    {
        _crossfadeDone = false;

        // ── UI thread: bitmap + buffer MUST exist before any VLC display callback
        // fires. ScreenScraper snaps are 320x240; use a fixed RV32 (BGRA) format. ──
        const int width = 320, height = 240;
        const int stride = width * 4;

        if (_videoBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_videoBuffer);
        _videoBuffer = Marshal.AllocHGlobal(stride * height);

        _videoBitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                           PixelFormat.Bgra8888, AlphaFormat.Opaque);
        Get<Image>("VideoImage").Source = _videoBitmap;

        IntPtr bufferPtr = _videoBuffer;

        // Awaits an already-completed Task on the hot path (warmed at startup).
        var libVLC = await VideoPlaybackService.Instance.GetLibVLCAsync();

        // ── Worker thread: MediaPlayer ctor, callback wiring, Media open, Play ──
        await Task.Run(() =>
        {
            var player = new MediaPlayer(libVLC);
            player.SetVideoFormat("RV32", width, height, stride);

            player.SetVideoCallbacks(
                // Lock: hand VLC our buffer.
                (IntPtr opaque, IntPtr planes) => { Marshal.WriteIntPtr(planes, bufferPtr); return IntPtr.Zero; },
                // Unlock: no-op.
                null,
                // Display: blit to the WriteableBitmap on the UI thread.
                (IntPtr opaque, IntPtr picture) => Dispatcher.UIThread.Post(() =>
                {
                    if (_videoBitmap == null || _videoBuffer == IntPtr.Zero) return;
                    using (var fb = _videoBitmap.Lock())
                    {
                        // Copy row-by-row honoring the framebuffer's own stride — Avalonia
                        // may pad rows, so never assume RowBytes == width*4.
                        unsafe
                        {
                            byte* src = (byte*)_videoBuffer;
                            byte* dst = (byte*)fb.Address;
                            int dstStride = fb.RowBytes;
                            for (int y = 0; y < height; y++)
                                Buffer.MemoryCopy(src + y * stride, dst + (long)y * dstStride, dstStride, stride);
                        }
                    }
                    Get<Image>("VideoImage").InvalidateVisual();

                    if (!_crossfadeDone)
                    {
                        _crossfadeDone = true;
                        Get<Image>("VideoImage").IsVisible = true;
                        Get<TextBlock>("ArtPlaceholderText").IsVisible = false;
                        var header = Get<Image>("HeaderImage");
                        header.Transitions ??= new Transitions
                        {
                            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(400) },
                        };
                        header.Opacity = 0;
                    }
                }));

            using var media = new Media(libVLC, mp4Path, FromType.FromPath);
            // Loop natively (libvlc restarts the input); avoids racing an EndReached re-play.
            media.AddOption(":input-repeat=65535");

            // Bail before the blocking Invoke if the window already closed, so we don't
            // marshal onto a tearing-down dispatcher.
            if (_closed) { try { player.Dispose(); } catch { } return; }

            // Stash AND start inside the UI critical section so OnClosed (also UI) can't
            // interleave and Dispose the player between the assignment and Play.
            bool keep = false;
            try
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    if (_closed) return;
                    _vlcPlayer = player;
                    player.Play(media);
                    keep = true;
                });
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[GameDetail] snap play failed: {ex.Message}"); }

            if (!keep) { try { player.Dispose(); } catch { } }
        });
    }

    // ── Slide-up + fade-in entrance ──
    private void SetupAnimateIn()
    {
        var card = Get<Border>("ModalCard");
        card.Opacity = 0;
        card.RenderTransform = TransformOperations.Parse("translateY(30px)");
        card.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(250), Easing = new CubicEaseOut() },
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            card.Opacity = 1;
            card.RenderTransform = TransformOperations.Parse("translateY(0px)");
        });
    }

    // ── Actions ──
    private void PlayButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var coreManager = new CoreManager(App.Configuration!);
        string? corePath = coreManager.GetCorePathForGame(_game);
        if (string.IsNullOrEmpty(corePath))
        {
            _ = Info("Missing Core", $"No emulator core installed for {_game.Console}. Download one in Preferences → Cores.");
            return;
        }
        string romPath = AppPaths.FromStoragePath(_game.RomPath);
        if (!System.IO.File.Exists(romPath))
        {
            _ = Info("File Not Found", $"ROM file not found:\n{romPath}");
            return;
        }
        try
        {
            var emu = new EmulatorWindow(new EmulatorSession(corePath, romPath));
            // The emulator session mutates _game's play stats; refresh the pills when it
            // closes so the still-open card reflects the latest play/last-played numbers.
            emu.Closed += (_, _) => { if (IsVisible) RefreshStats(); };
            emu.Show();
        }
        catch (Exception ex)
        {
            _ = Info("Launch Error", $"Failed to launch emulator:\n\n{ex.Message}");
        }
    }

    private void FavoriteButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _game.IsFavorite = !_game.IsFavorite;
        _db.ToggleFavorite(_game.Id, _game.IsFavorite);
        Get<Button>("FavoriteButton").Content = _game.IsFavorite ? "♥  Favorited" : "♡  Favorite";
        Get<Border>("FavoriteBadge").IsVisible = _game.IsFavorite;
    }

    private void MoreButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var showInFiles = new MenuItem { Header = "Show in Files" };
        showInFiles.Click += (_, _) =>
        {
            string rom = AppPaths.FromStoragePath(_game.RomPath);
            string? dir = System.IO.Path.GetDirectoryName(rom);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                try { System.Diagnostics.Process.Start("xdg-open", dir); } catch { }
        };
        menu.Items.Add(showInFiles);

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += async (_, _) =>
        {
            string? newTitle = await new RenameWindow(_game.Title).ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(newTitle)) return;
            _game.Title = newTitle;
            _db.UpdateTitle(_game.Id, _game.Title);
            Get<TextBlock>("GameTitle").Text = _game.Title;
            Get<TextBlock>("ArtPlaceholderText").Text = _game.Title;
        };
        menu.Items.Add(rename);

        // Notes editor lands later in U7; Manual opens via the system PDF viewer (xdg-open).
        menu.Items.Add(new MenuItem { Header = "Notes…", IsEnabled = false });
        var manual = new MenuItem { Header = ManualLauncher.HasUsableManual(_game) ? "View Manual" : "Download Manual…" };
        manual.Click += (_, _) =>
        {
            var fetch = (this.Owner as MainWindow)?.ArtworkFetch;
            if (fetch != null) _ = ManualLauncher.OpenOrDownloadAsync(_game, fetch);
        };
        menu.Items.Add(manual);

        // Cheats — shown only when this console's core actually supports cheats (upstream gate).
        if (CoreManager.ConsoleCoreMap.TryGetValue(_game.Console ?? "", out var cores) && cores.Length > 0
            && CheatSupport.Lookup(cores[0]).Level != CheatSupportLevel.NotSupported)
        {
            var cheats = new MenuItem { Header = "Cheats…" };
            cheats.Click += (_, _) => new CheatsManagerWindow(_game).Show(this);
            menu.Items.Add(cheats);
        }

        menu.Items.Add(new Separator());

        var remove = new MenuItem { Header = "Remove from Library" };
        remove.Click += async (_, _) =>
        {
            bool ok = await new ConfirmDialog("Remove Game",
                $"Remove \"{_game.Title}\" from your library?\n\nThis will not delete the ROM file.",
                "Remove", danger: true).ShowDialog<bool>(this);
            if (ok) { _db.DeleteGame(_game.Id); Close(); }
        };
        menu.Items.Add(remove);

        menu.PlacementTarget = sender as Control;
        menu.Placement = PlacementMode.Bottom;
        menu.Open(sender as Control);
    }

    private Task Info(string title, string message) =>
        new ConfirmDialog(title, message, "OK", infoOnly: true).ShowDialog<bool>(this);

    private T Get<T>(string name) where T : Control => this.FindControl<T>(name)!;
}
