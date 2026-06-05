using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>
/// Compact popup shown when a friend row is clicked on the Achievements →
/// Friends sub-tab (port of upstream FriendBriefCard). Identity + points +
/// last activity + two actions (Open Full Profile, Remove). Shown with
/// Show() (not ShowDialog) so the parent stays interactive — required for
/// MainWindow's dismiss-on-click pattern. The bell icon is a glyph stand-in
/// for upstream's MaterialDesign PackIcon (hover ring animation dropped;
/// hover tint kept).
/// </summary>
public partial class FriendBriefCard : Window
{
    private readonly int _userId;
    private bool _toastsEnabled;

    /// <summary>Raised when the user clicks "Open Full Profile".</summary>
    public event EventHandler<int>? OpenProfileRequested;
    /// <summary>Raised when the user clicks "Remove".</summary>
    public event EventHandler<int>? RemoveRequested;

    private readonly FriendService _friends;

    public FriendBriefCard() : this(new FriendEntry { Username = "Friend" }, null, null!) { }

    public FriendBriefCard(FriendEntry entry, FriendCacheSnapshot? snap, FriendService friends)
    {
        InitializeComponent();
        _userId = entry.UserId;
        _friends = friends;
        _toastsEnabled = entry.ToastsEnabled;

        this.FindControl<Button>("OpenProfileBtn")!.Click += (_, _) => { OpenProfileRequested?.Invoke(this, _userId); CloseBrief(); };
        this.FindControl<Button>("RemoveBtn")!.Click += (_, _) => { RemoveRequested?.Invoke(this, _userId); CloseBrief(); };
        var toggle = this.FindControl<Button>("BriefToastsToggle")!;
        toggle.Click += BriefToastsToggle_Click;
        toggle.PointerEntered += (_, _) =>
            this.FindControl<Avalonia.Controls.Shapes.Path>("BriefToastsIcon")!.Fill =
                new SolidColorBrush(Color.FromRgb(0xE0, 0xB5, 0x4B));
        toggle.PointerExited += (_, _) =>
            this.FindControl<Avalonia.Controls.Shapes.Path>("BriefToastsIcon")!.Fill =
                this.TryFindResource("TextSecondaryBrush", ActualThemeVariant, out var b) && b is IBrush br
                    ? br : Brushes.Gray;

        // Authoritative identity from the entry; the snap can be empty/stale.
        this.FindControl<TextBlock>("BriefName")!.Text = entry.Username;
        this.FindControl<Border>("BriefMutualChip")!.IsVisible = entry.MutualFollow;
        ApplyToastsIcon();

        // Re-read snap fresh from the service rather than trusting the
        // parameter, which can be stale if polling rewrote between row
        // paint and click.
        var fresh = friends?.GetSnapshot(_userId) ?? snap;
        ApplySnapshot(fresh);
    }

    private void ApplySnapshot(FriendCacheSnapshot? snap)
    {
        var points = this.FindControl<TextBlock>("BriefPoints")!;
        var motto = this.FindControl<TextBlock>("BriefMotto")!;
        var lastRow = this.FindControl<Grid>("BriefLastPlayedRow")!;
        var pill = this.FindControl<Border>("BriefUnlocks24hCard")!;

        if (snap == null)
        {
            points.Text = "Loading…";
            motto.Text = "";
            this.FindControl<TextBlock>("BriefLastActivity")!.Text = "—";
            lastRow.IsVisible = false;
            pill.IsVisible = false;
            System.Diagnostics.Trace.WriteLine("[FriendBriefCard] snap is null — no data to render");
            return;
        }

        points.Text = FriendsCopy.PointsAndSoftcore(snap.PointsHardcore, snap.PointsSoftcore);
        motto.Text = string.IsNullOrWhiteSpace(snap.Motto) ? "" : snap.Motto;

        if (string.IsNullOrEmpty(snap.LastGameTitle))
        {
            lastRow.IsVisible = false;
        }
        else
        {
            lastRow.IsVisible = true;
            this.FindControl<TextBlock>("BriefLastActivity")!.Text = snap.LastGameTitle;
            if (!string.IsNullOrEmpty(snap.LastGameImageIcon))
                FriendImageLoader.Load(this.FindControl<Image>("BriefLastGameImage")!,
                    "https://media.retroachievements.org" + snap.LastGameImageIcon, "brief", "game icon");
        }

        if (snap.RecentUnlockCount24h > 0)
        {
            pill.IsVisible = true;
            this.FindControl<TextBlock>("BriefUnlocks24hText")!.Text = snap.RecentUnlockCount24h == 1
                ? "1 achievement unlocked in the last 24 hours"
                : $"{snap.RecentUnlockCount24h} achievements unlocked in the last 24 hours";
        }
        else
        {
            pill.IsVisible = false;
        }

        if (!string.IsNullOrEmpty(snap.AvatarUrl))
            FriendImageLoader.Load(this.FindControl<Image>("BriefAvatar")!, snap.AvatarUrl, "brief", "avatar");
    }

    /// <summary>Closes the card if it's still open. Idempotent.</summary>
    public void CloseBrief()
    {
        try { Close(); } catch { }
    }

    private async void BriefToastsToggle_Click(object? sender, RoutedEventArgs e)
    {
        // Optimistic UI: flip the icon immediately; the async config write
        // reconciles in the background.
        _toastsEnabled = !_toastsEnabled;
        ApplyToastsIcon();
        try
        {
            await _friends.SetToastsEnabledAsync(_userId, _toastsEnabled);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[FriendBriefCard] SetToastsEnabledAsync failed: {ex.Message}");
        }
    }

    private void ApplyToastsIcon()
    {
        var icon = this.FindControl<Avalonia.Controls.Shapes.Path>("BriefToastsIcon")!;
        icon.Data = _toastsEnabled ? BellIcon.On : BellIcon.Off;
        string tip = _toastsEnabled
            ? "Notifications on — click to mute this friend's toasts"
            : "Notifications off — click to enable this friend's toasts";
        ToolTip.SetTip(this.FindControl<Button>("BriefToastsToggle")!, tip);
    }
}
