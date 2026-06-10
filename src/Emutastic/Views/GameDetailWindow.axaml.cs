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
        _ = LoadRetroAchievementsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Signal in-flight async work (placeholder decode, snap-video worker) to drop
        // its results instead of writing into a dead window.
        _closed = true;
        _raRefreshCts?.Cancel();

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
        var missingBios = CoreManager.GetMissingBiosForLaunch(_game.Console ?? "", romPath, corePath);
        if (missingBios.Count > 0)
        {
            // BIOS-required dialog instead of a cryptic core load failure.
            _ = Info("BIOS Required",
                $"{_game.Console} requires a BIOS to run.\n\nMissing: {string.Join(", ", missingBios)}\n\n" +
                "Add it in Preferences → System Files, or place it next to your ROMs.");
            return;
        }
        try
        {
            // Legacy in-process EmulatorWindow, or (EMUTASTIC_PRESENT=gl) a separate --game-host process.
            // Refresh the pills when the game ends so the still-open card reflects latest play stats.
            // _game gives the host its save-state context (--save-dir/--game-title/--rom-hash).
            Services.GameHostLauncher.Launch(corePath, romPath, _game.Console ?? "",
                _game, loadStatePath: null,
                _ => { if (IsVisible) RefreshStats(); });
        }
        catch (Exception ex)
        {
            _ = Info("Launch Error", $"Failed to launch emulator:\n\n{ex.Message}");
        }
    }

    private void FavoriteButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _game.IsFavorite = !_game.IsFavorite;
        // UI updates first; persistence off the UI thread (same blocking class as the rating fix:
        // a contended SQLite write must never pin the UI thread).
        bool fav = _game.IsFavorite;
        _ = Task.Run(() => { try { _db.ToggleFavorite(_game.Id, fav); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[DetailFav] {ex.Message}"); } });
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
                Services.ShellOpen.Open(dir);
        };
        menu.Items.Add(showInFiles);

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += async (_, _) =>
        {
            string? newTitle = await new RenameWindow(_game.Title).ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(newTitle)) return;
            _game.Title = newTitle;
            await Task.Run(() => _db.UpdateTitle(_game.Id, _game.Title));
            Get<TextBlock>("GameTitle").Text = _game.Title;
            Get<TextBlock>("ArtPlaceholderText").Text = _game.Title;
        };
        menu.Items.Add(rename);

        var notes = new MenuItem { Header = "Notes…" };
        notes.Click += (_, _) => NotesWindow.ShowFor(_game, this);
        menu.Items.Add(notes);
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
            cheats.Click += (_, _) => _ = new CheatsManagerWindow(_game).ShowDialog(this);
            menu.Items.Add(cheats);
        }

        menu.Items.Add(new Separator());

        var remove = new MenuItem { Header = "Remove from Library" };
        remove.Click += async (_, _) =>
        {
            bool ok = await new ConfirmDialog("Remove Game",
                $"Remove \"{_game.Title}\" from your library?\n\nThis will not delete the ROM file.",
                "Remove", danger: true).ShowDialog<bool>(this);
            if (ok) { await Task.Run(() => _db.DeleteGame(_game.Id)); Close(); }
        };
        menu.Items.Add(remove);

        menu.PlacementTarget = sender as Control;
        menu.Placement = PlacementMode.Bottom;
        menu.Open(sender as Control);
    }

    private Task Info(string title, string message) =>
        new ConfirmDialog(title, message, "OK", infoOnly: true).ShowDialog<bool>(this);

    // ════════════════════════════════════════════════════════════════════════
    //  A8e — RetroAchievements section (port of upstream's detail-card RA pane)
    // ════════════════════════════════════════════════════════════════════════

    private System.Threading.CancellationTokenSource? _raRefreshCts;

    // Badge tiles (56×56 from media.retroachievements.org) — small per-app cache so
    // reopening cards doesn't refetch; misses render as blank tiles, never throw.
    private static readonly System.Net.Http.HttpClient _raBadgeHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Dictionary<string, Bitmap?> _raBadgeCache = new();

    private IBrush Res(string key, string fallbackHex)
        => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b
            ? b : new SolidColorBrush(Color.Parse(fallbackHex));

    /// <summary>Renders whatever's cached, then fires a background refresh and re-renders.
    /// Bails fast for users who haven't opted into RA.</summary>
    private async Task LoadRetroAchievementsAsync()
    {
        var raConfig = App.Configuration?.GetRetroAchievementsConfiguration();
        if (raConfig == null || !raConfig.IsConfigured)
        {
            Get<StackPanel>("RASection").IsVisible = false;
            return;
        }

        RenderRetroAchievements();

        _raRefreshCts?.Cancel();
        _raRefreshCts = new System.Threading.CancellationTokenSource();
        var token = _raRefreshCts.Token;
        try
        {
            if (App.Configuration == null) return;
            var svc = new Services.RetroAchievementsService(App.Configuration, _db);
            await svc.RefreshDetailForGameAsync(_game, token);
            if (token.IsCancellationRequested || _closed) return;
            RenderRetroAchievements();
        }
        catch (OperationCanceledException) { /* window closed during fetch */ }
        catch
        {
            // Network failures are swallowed inside the service; this belt covers any
            // DB / render glitch so a flaky API can never crash the card.
        }
    }

    /// <summary>Pushes the cached typed views into the UI. Safe to call repeatedly;
    /// hides sub-sections piece by piece rather than the whole pane.</summary>
    /// <summary>
    /// Console-aware message for the "rcheevos couldn't identify this ROM" case
    /// (port of upstream's UnrecognizedHashMessage). A universal "Redump" hint only
    /// makes sense for disc systems — arcade ZIPs and cartridge ROMs get their own.
    /// </summary>
    private static string UnrecognizedHashMessage(string console)
    {
        switch (console)
        {
            case "Arcade":
            case "NeoGeo":
                return "RetroAchievements doesn't recognize this ROM set — RA usually targets one specific parent or clone; check retroachievements.org to confirm the title is on RA and which set is supported";
            case "PS1":
            case "PS2":
            case "PSP":
            case "Saturn":
            case "Dreamcast":
            case "GameCube":
            case "SegaCD":
            case "TGCD":
            case "3DO":
            case "NeoCD":
            case "CDi":
            case "3DS":
                return "RetroAchievements doesn't recognize this disc image — try a Redump-matching dump";
            default:
                return "RetroAchievements doesn't recognize this ROM hash — try a No-Intro matching dump";
        }
    }

    private void RenderRetroAchievements()
    {
        var prog = _game.RAProgressionTyped;
        var user = _game.RAUserProgressTyped;
        var section = Get<StackPanel>("RASection");
        var label = Get<TextBlock>("RAProgressLabel");
        var bar = Get<ProgressBar>("RAProgress");

        // No progression data — one labeled status line, driven by RALastLaunchOutcome,
        // so "never launched" / "no set authored" / "ROM unrecognized" don't all look
        // like the same empty pane (upstream's four-state matrix verbatim).
        if (prog == null || prog.NumAchievements <= 0)
        {
            bool identified  = _game.RAGameId > 0;
            bool unsupported = _game.RAGameId >= 1_000_000_000;   // RA's "unsupported version" placeholder band
            bool emptySet    = prog != null && prog.NumAchievements <= 0;
            string status = (identified, unsupported, emptySet, _game.RALastLaunchOutcome) switch
            {
                (true,  true,  _,    _)                 => "This ROM dump isn't on the RetroAchievements database — try a different release",
                (true,  false, true, _)                 => "No achievements authored for this game yet",
                (true,  false, false, _)                => "Fetching achievement data…",
                (false, _,     _,    "not_in_database") => UnrecognizedHashMessage(_game.Console),
                (false, _,     _,    "load_failed")     => "RetroAchievements identification failed — try relaunching",
                _                                        => "Not checked yet — launch this game with RetroAchievements enabled",
            };
            section.IsVisible = true;
            label.Text = status;
            bar.Value = 0;
            bar.IsVisible = false;
            Get<StackPanel>("ComingUpSection").IsVisible = false;
            Get<TextBlock>("RATimingsCaption").IsVisible = false;
            return;
        }

        section.IsVisible = true;
        bar.IsVisible = true;

        // The unlock track follows the user's hardcore setting (hardcore unlocks are the
        // ones that count there); falls back to softcore when logged out.
        bool hardcore = App.Configuration?.GetRetroAchievementsConfiguration()?.HardcoreMode == true;

        int total  = prog.NumAchievements;
        int earned = hardcore
            ? (user?.NumAwardedToUserHardcore ?? 0)
            : (user?.NumAwardedToUser ?? 0);
        int userPts = 0;
        if (user != null)
        {
            foreach (var a in user.Achievements.Values)
            {
                string? earnedDate = hardcore ? a.DateEarnedHardcore : a.DateEarned;
                if (!string.IsNullOrEmpty(earnedDate)) userPts += a.Points;
            }
        }

        if (user != null)
        {
            label.Text = userPts > 0 ? $"{earned} / {total}  ·  {userPts:N0} pts" : $"{earned} / {total}";
            bar.Value = total > 0 ? earned * 100.0 / total : 0;
            // Mastered (100%) flips the bar to gold; otherwise theme accent.
            bar.Foreground = (earned >= total && total > 0)
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D))
                : Res("AccentBrush", "#E03535");
        }
        else
        {
            label.Text = $"{total} achievements";
            bar.Value = 0;
        }

        BuildComingUp(prog, user, hardcore);
        BuildTimingsCaption(prog, hardcore);
    }

    private void BuildComingUp(Models.RAProgression prog, Models.RAUserProgress? user, bool hardcore)
    {
        var grid = Get<Avalonia.Controls.Primitives.UniformGrid>("ComingUpGrid");
        var sectionPanel = Get<StackPanel>("ComingUpSection");
        grid.Children.Clear();

        // Logged-out users have no "earned" set — every achievement would be "Coming
        // up", which is meaningless. Hide instead.
        if (user == null || user.Achievements.Count == 0)
        {
            sectionPanel.IsVisible = false;
            return;
        }

        var earnedIds = new HashSet<int>();
        foreach (var a in user.Achievements.Values)
        {
            string? earnedDate = hardcore ? a.DateEarnedHardcore : a.DateEarned;
            if (!string.IsNullOrEmpty(earnedDate)) earnedIds.Add(a.Id);
        }

        // Live in-game progress (captured by rcheevos last session) wins — "closest to
        // unlocking right now" — but only when its captured mode matches the current
        // mode (softcore-captured % is meaningless under hardcore). Remaining slots
        // fill from the community proxy: ascending median TTU, tiebreak popularity.
        // Null/zero medians are no-data signals, not "instant" — skipped.
        var live = _game.RALiveProgressTyped;
        var liveMap = (live != null && live.Hardcore == hardcore)
            ? live.Achievements
            : new Dictionary<int, Models.RALiveAchievementProgress>();

        var unearned = prog.Achievements.Where(a => !earnedIds.Contains(a.Id)).ToList();

        var liveHits = unearned
            .Where(a => liveMap.TryGetValue(a.Id, out var lp) && lp.Percent > 0 && lp.Percent < 100)
            .OrderByDescending(a => liveMap[a.Id].Percent)
            .ToList();
        var liveHitIds = new HashSet<int>(liveHits.Select(a => a.Id));

        var proxyPool = unearned
            .Where(a => !liveHitIds.Contains(a.Id))
            .Select(a => new
            {
                Ach = a,
                Median = hardcore ? (a.MedianTimeToUnlockHardcore ?? a.MedianTimeToUnlock) : a.MedianTimeToUnlock,
                Pop    = hardcore ? a.NumAwardedHardcore : a.NumAwarded,
            })
            .Where(x => x.Median.HasValue && x.Median.Value > 0)
            .OrderBy(x => x.Median!.Value)
            .ThenByDescending(x => x.Pop)
            .Select(x => x.Ach)
            .ToList();

        var picks = liveHits.Concat(proxyPool).Take(3).ToList();
        if (picks.Count == 0)
        {
            sectionPanel.IsVisible = false;
            return;
        }

        foreach (var ach in picks)
        {
            int median = (hardcore ? (ach.MedianTimeToUnlockHardcore ?? ach.MedianTimeToUnlock) : ach.MedianTimeToUnlock) ?? 0;
            liveMap.TryGetValue(ach.Id, out var livePick);
            grid.Children.Add(BuildBadgeTile(ach, median, livePick));
        }
        sectionPanel.IsVisible = true;
    }

    /// <summary>One "Coming up" tile: 56×56 badge, truncated title, and a caption — the
    /// user's live progress (accent) when available, else the community median (muted).
    /// Tooltip carries the full description + points.</summary>
    private Control BuildBadgeTile(Models.RAAchievement ach, int medianSec, Models.RALiveAchievementProgress? live)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };

        var img = new Image
        {
            Width = 56, Height = 56,
            Stretch = Avalonia.Media.Stretch.UniformToFill,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(ach.BadgeName))
            _ = LoadBadgeIntoAsync(img, $"https://media.retroachievements.org/Badge/{ach.BadgeName}.png");
        panel.Children.Add(img);

        panel.Children.Add(new TextBlock
        {
            Text = ach.Title,
            FontFamily = (Avalonia.Media.FontFamily)(this.FindResource("PrimaryFont") ?? FontFamily.Default),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("TextPrimaryBrush", "#FFFFFF"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxWidth = 110,
            Margin = new Thickness(0, 4, 0, 0),
        });

        string captionText;
        IBrush captionBrush;
        if (live != null && live.Percent > 0 && live.Percent < 100)
        {
            string pctStr = $"{live.Percent:0.#}%";
            captionText = string.IsNullOrEmpty(live.ProgressText) ? pctStr : $"{pctStr} · {live.ProgressText}";
            captionBrush = Res("AccentBrush", "#E03535");   // your data, not a community average
        }
        else
        {
            captionText = "~" + FormatDuration(medianSec);
            captionBrush = Res("TextMutedBrush", "#9A9A9A"); // estimate
        }
        panel.Children.Add(new TextBlock
        {
            Text = captionText,
            FontFamily = (Avalonia.Media.FontFamily)(this.FindResource("PrimaryFont") ?? FontFamily.Default),
            FontSize = 10,
            Foreground = captionBrush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        });

        var tip = new System.Text.StringBuilder();
        tip.AppendLine(ach.Title);
        if (!string.IsNullOrEmpty(ach.Description)) { tip.AppendLine(); tip.AppendLine(ach.Description); }
        tip.AppendLine();
        tip.Append($"{ach.Points} pts");
        ToolTip.SetTip(panel, tip.ToString());

        return panel;
    }

    private static async Task LoadBadgeIntoAsync(Image img, string url)
    {
        Bitmap? bmp;
        lock (_raBadgeCache)
        {
            if (_raBadgeCache.TryGetValue(url, out bmp))
            {
                if (bmp != null) img.Source = bmp;
                return;
            }
        }
        try
        {
            byte[] png = await _raBadgeHttp.GetByteArrayAsync(url);
            using var ms = new System.IO.MemoryStream(png);
            bmp = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[RA] badge load failed ({url}): {ex.Message}");
            bmp = null;
        }
        lock (_raBadgeCache) { _raBadgeCache[url] = bmp; }
        if (bmp != null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => img.Source = bmp);
    }

    private void BuildTimingsCaption(Models.RAProgression prog, bool hardcore)
    {
        var caption = Get<TextBlock>("RATimingsCaption");
        // Sample-size gate — under n=20 the medians are too noisy to present as typical.
        const int MinSamples = 20;
        int? beatSec = hardcore ? (prog.MedianTimeToBeatHardcore ?? prog.MedianTimeToBeat) : prog.MedianTimeToBeat;
        string? beat = beatSec.HasValue && prog.TimesUsedInBeatMedian >= MinSamples
            ? FormatDuration(beatSec.Value) : null;
        string? master = prog.MedianTimeToMaster.HasValue && prog.TimesUsedInMasteryMedian >= MinSamples
            ? FormatDuration(prog.MedianTimeToMaster.Value) : null;

        if (beat == null && master == null)
        {
            caption.IsVisible = false;
            return;
        }
        var parts = new List<string>();
        if (beat != null) parts.Add($"beat ~{beat}");
        if (master != null) parts.Add($"master ~{master}");
        caption.Text = "Typical run: " + string.Join("  ·  ", parts);
        caption.IsVisible = true;
    }

    private T Get<T>(string name) where T : Control => this.FindControl<T>(name)!;
}
