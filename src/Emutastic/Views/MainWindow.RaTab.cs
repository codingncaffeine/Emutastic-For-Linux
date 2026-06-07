using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Configuration;

namespace Emutastic.Views;

// ── Achievements tab + Friends (A8f) — port of upstream MainWindow.xaml.cs
//    lines 2668-5034. Split into its own partial file for reviewability; the
//    AXAML lives in MainWindow.axaml (AchievementsView). WPF image loads are
//    routed through FriendImageLoader (Avalonia has no URL-loading bitmap). ──
public partial class MainWindow
{
    private RaDataService? _raData;
    private Views.FriendBriefCard? _openFriendBrief;
    private readonly Dictionary<int, Views.FriendDetailWindow> _friendDetailWindows = new();
    private FriendService? _friendService;
    private System.Threading.CancellationTokenSource? _raTabCts;

    /// <summary>Call once from the MainWindow constructor (after InitializeComponent):
    /// wires the Achievements-tab buttons the AXAML declares without Click attrs,
    /// the brief-card dismiss-on-click (tunneling) handler, and the startup hooks
    /// (stale-cache mark + follows sync) that upstream ran from OnLoaded.</summary>
    private void WireRaTab()
    {
        this.FindControl<Button>("RAOfTheWeekPlay")!.Click += RAOfTheWeekPlay_Click;
        this.FindControl<Button>("FriendsAddButton")!.Click += FriendsAddButton_Click;
        this.FindControl<Button>("FriendsImportButton")!.Click += FriendsImportButton_Click;
        this.FindControl<Border>("FollowersHeader")!.PointerPressed += FollowersHeader_Click;

        // Dismiss model for the friend brief card: any press that lands on the
        // MainWindow surface closes it (upstream's OnPreviewMouseDown tunneling).
        AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,
            (_, _) => { _openFriendBrief?.CloseBrief(); _openFriendBrief = null; },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Session-restart cache policy + follow-graph auto-sync (upstream OnLoaded).
        try
        {
            if (App.Configuration != null && _db != null)
                GetOrCreateRaDataService().MarkUserCacheStaleForFreshFetch();
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[RA] session-start stale-mark failed: {ex.Message}"); }
        WireLbScoreboard();
        _ = SyncFollowsIfEnabledAsync();
    }

    /// <summary>RA follow-graph auto-sync (upstream Phase 7.4) — fire-and-forget at
    /// startup; silent on missing credentials AND network failure. The Import
    /// button is the user-visible surface; this is "stay current" plumbing.</summary>
    private async Task SyncFollowsIfEnabledAsync()
    {
        try
        {
            if (App.Configuration == null) return;
            var ra = App.Configuration.GetRetroAchievementsConfiguration();
            if (ra == null || !ra.IsConfigured
                || !ra.SyncFollowsOnLaunch
                || string.IsNullOrWhiteSpace(ra.Username)
                || string.IsNullOrWhiteSpace(ra.ApiKey))
                return;

            var api = new Services.RetroAchievementsService(App.Configuration!, _db!);
            var followed = await Task.Run(() => api.GetUsersIFollowAsync()).ConfigureAwait(false);
            if (followed == null || followed.Count == 0) return;

            var friends = GetOrCreateFriendService();
            var result = await Task.Run(() => friends.ApplyFollowSyncAsync(followed)).ConfigureAwait(false);

            // Quiet status only when something actually changed.
            if (result.Added > 0 || result.MutualCleared > 0)
            {
                var parts = new List<string>();
                if (result.Added         > 0) parts.Add($"{result.Added} new follow(s)");
                if (result.MutualCleared > 0) parts.Add($"{result.MutualCleared} mutual flag(s) cleared");
                string msg = "RA follow sync: " + string.Join(" · ", parts);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _vm?.SetStatus(msg, autoClear: true));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[FollowSync] launch sync failed: {ex.Message}");
        }
    }

    // ── Phase 6b.1: leaderboard SCOREBOARD handling (upstream EmulatorWindow's
    //    HandleLbScoreboard, adapted to the host split: the host ships the raw
    //    event up via "lb-scoreboard"; the decision runs HERE against the
    //    pre-fetched friend ranks; the toast ships back down via
    //    "show-ra-toast" for in-game GlOsd display; the sound plays here). ──
    private readonly Dictionary<int, DateTimeOffset> _lbToastCooldown = new();

    private void WireLbScoreboard()
    {
        Services.GameHostLauncher.OnGameLaunching = g =>
        {
            try
            {
                if (g.RAGameId > 0 && App.Configuration != null && _db != null)
                    GetOrCreateFriendService().StartFriendLbPrefetch(g.RAGameId);
            }
            catch (Exception ex) { RaLog.Write($"[LbPrefetch] launch hook failed: {ex.Message}"); }
        };
        Services.GameHostLauncher.OnHostCommandForGame = (g, verb, arg) =>
        {
            if (verb != "lb-scoreboard") return;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(arg).RootElement;
                HandleLbScoreboard(g,
                    doc.GetProperty("lb").GetInt32(),
                    doc.GetProperty("rank").GetInt32(),
                    doc.GetProperty("title").GetString() ?? "",
                    doc.GetProperty("submitted").GetString() ?? "",
                    doc.TryGetProperty("hc", out var hcEl) && hcEl.GetBoolean());
            }
            catch (Exception ex) { RaLog.Write($"[LbToast] scoreboard parse failed: {ex.Message}"); }
        };
    }

    private void HandleLbScoreboard(Models.Game game, int leaderboardId, int newRank,
                                    string lbTitle, string submittedScore, bool sessionHardcore)
    {
        try
        {
            var cfg = App.Configuration?.GetFriendsConfiguration();
            if (cfg == null) return;
            if (!cfg.LbToastWhenYouBeat && !cfg.LbToastForProximity) return;
            // Respect "hardcore-only toasts": if the user asked for HC-only signal
            // and this session is softcore, suppress LB toasts wholesale.
            if (cfg.HardcoreOnlyToast && !sessionHardcore)
            {
                RaLog.Write("[LbToast] HardcoreOnlyToast=true and session is softcore — suppressing");
                return;
            }
            // rcheevos failure submissions report new_rank == 0 — without this guard
            // "newRank <= friendRank" trivially fires for every friend.
            if (newRank == 0)
            {
                RaLog.Write("[LbToast] new_rank=0 (failed submit?) — skipping");
                return;
            }

            // Cooldown gate + opportunistic prune (shmup/pinball burst protection).
            var now = DateTimeOffset.UtcNow;
            var cooldownTtl = TimeSpan.FromSeconds(cfg.LbToastCooldownSec);
            var staleThreshold = now - TimeSpan.FromSeconds(cfg.LbToastCooldownSec * 2);
            var stale = new List<int>();
            foreach (var kv in _lbToastCooldown)
                if (kv.Value < staleThreshold) stale.Add(kv.Key);
            foreach (var k in stale) _lbToastCooldown.Remove(k);
            if (_lbToastCooldown.TryGetValue(leaderboardId, out var lastFired)
                && (now - lastFired) < cooldownTtl)
            {
                RaLog.Write($"[LbToast] cooldown active for lb={leaderboardId}, suppressing");
                return;
            }

            var svc = GetOrCreateFriendService();
            var friendRanks = svc?.AllFriendLbRanks;
            if (svc == null || friendRanks == null || friendRanks.Count == 0)
            {
                RaLog.Write($"[LbToast] no friend ranks cached — skipping (ranks={friendRanks?.Count ?? 0})");
                return;
            }

            string gameTitle = game.Title ?? "";
            string consoleName = game.Console ?? "";

            // Triumph needs my OLD rank — "did I cross the friend?" is
            // oldRank > friendRank && newRank <= friendRank. Pre-fetch caches both
            // sides at game launch; without it we'd false-positive on every
            // submission where I was ALWAYS above a friend.
            var myOld = svc.GetMyLbScore(leaderboardId);
            int myOldRank = myOld?.Rank ?? int.MaxValue;   // no prior entry = effectively last

            // Snapshot the list ONCE (the getter re-materializes per call).
            var friendsByUid = svc.Friends.ToDictionary(f => f.UserId);

            var triumphs = new List<(string user, FriendLbScore prev)>();
            var nearMisses = new List<(string user, FriendLbScore other, long gap)>();
            foreach (var kv in friendRanks)
            {
                int friendUid = kv.Key;
                var byLb = kv.Value;
                if (!byLb.TryGetValue(leaderboardId, out var fScore)) continue;
                if (fScore.Rank <= 0) continue;
                // Per-friend mute applies symmetrically — "no notifications about
                // this user" includes YOU beating them.
                if (!friendsByUid.TryGetValue(friendUid, out var friend)) continue;
                if (!friend.ToastsEnabled) continue;
                string fUser = friend.Username;

                if (cfg.LbToastWhenYouBeat
                    && myOldRank > fScore.Rank
                    && newRank <= fScore.Rank)
                {
                    triumphs.Add((fUser, fScore));
                }

                // Proximity: rank-gap form (see upstream's note on lower_is_better
                // score-pct semantics); pct threshold reused as max rank gap.
                int rankGapThreshold = Math.Max(1, cfg.LbToastProximityPct);
                if (cfg.LbToastForProximity
                    && fScore.Rank < newRank
                    && (newRank - fScore.Rank) <= rankGapThreshold)
                {
                    long rankGap = newRank - fScore.Rank;
                    nearMisses.Add((fUser, fScore, rankGap));
                }
            }

            // Update my cached rank AFTER the comparison so the next submission
            // compares against the post-submission baseline.
            svc.UpdateMyLbRank(leaderboardId, newRank, submittedScore);

            if (triumphs.Count == 0 && nearMisses.Count == 0)
            {
                RaLog.Write($"[LbToast] no candidates for lb={leaderboardId} (rank=#{newRank})");
                return;
            }

            _lbToastCooldown[leaderboardId] = now;

            string lbTitleDisplay = string.IsNullOrWhiteSpace(lbTitle) ? "this leaderboard" : lbTitle;

            string headline, subline;
            if (triumphs.Count > 0)
            {
                var first = triumphs[0];
                if (triumphs.Count == 1)
                    (headline, subline) = FriendsCopy.LbTriumphYou(first.user, lbTitleDisplay, gameTitle, consoleName);
                else
                {
                    headline = $"You beat {first.user} and {triumphs.Count - 1} other{(triumphs.Count > 2 ? "s" : "")} on {lbTitleDisplay}";
                    subline = string.IsNullOrEmpty(consoleName) ? gameTitle : $"{gameTitle} · {consoleName}";
                }
                RaLog.Write($"[LbToast] TRIUMPH lb={leaderboardId} title=[{lbTitleDisplay}] passed={triumphs.Count}");
                SendRaToastToHost(game.Id, headline, subline);
                FriendNotificationSound.Play(App.Configuration);
            }
            else
            {
                var closest = nearMisses.OrderBy(n => n.gap).First();
                string gapDesc = $"{closest.gap} rank{(closest.gap == 1 ? "" : "s")}";
                (headline, subline) = FriendsCopy.LbProximity(closest.user, gapDesc, lbTitleDisplay, gameTitle, consoleName);
                RaLog.Write($"[LbToast] PROXIMITY lb={leaderboardId} title=[{lbTitleDisplay}] closest={closest.user} gap={closest.gap}");
                SendRaToastToHost(game.Id, headline, subline);
                // No sound on proximity — informational, not celebratory.
            }
        }
        catch (Exception ex)
        {
            RaLog.Write($"[LbToast] HandleLbScoreboard EX: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static void SendRaToastToHost(int gameId, string headline, string subline)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { h = headline, s = subline });
        Services.GameHostLauncher.SendToHost(gameId, "show-ra-toast " + json);
    }

    private object RaRes(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var v) && v != null
            ? v : Avalonia.Media.Brushes.Gray;


        // ── Achievements tab ────────────────────────────────────────────────
        // Reads cached payloads from RaDataService synchronously on click
        // (instant first paint), then kicks the refresh in the background.
        // The refresh task uses ConfigureAwait(false) end-to-end and never
        // touches WPF state directly — it marshals via Dispatcher.BeginInvoke.
        //
        // RA-config changes (username / API key edited in Preferences) are
        // picked up automatically without an explicit hook: CurrentUser()
        // and GetApiKey() re-read App.Configuration on every call. A new
        // username produces different cache keys (user:Foo vs user:Bar) so
        // the new user's cards auto-fetch on next tab open. Changing only
        // the API key reuses the username-keyed cache rows (still valid)
        // with the new key driving subsequent refreshes.

        private RaDataService GetOrCreateRaDataService()
        {
            if (_raData == null && App.Configuration != null && _db != null)
            {
                _raData = new RaDataService(App.Configuration, _db, new RetroAchievementsService(App.Configuration, _db));
            }
            return _raData!;
        }

        // Lazy-constructed Friends service. Phase 2+ wiring binds the
        // Friends sub-tab to its FriendListChanged event; Phase 3 starts
        // the DispatcherTimer poll here.
        private FriendService GetOrCreateFriendService()
        {
            if (_friendService == null && App.Configuration != null && _db != null)
            {
                _friendService = new FriendService(
                    App.Configuration, _db,
                    new RetroAchievementsService(App.Configuration, _db));
            }
            return _friendService!;
        }

        private void PopulateAchievementsView()
        {
            var ra = GetOrCreateRaDataService();
            string? user = ra.CurrentUser();
            bool keyOk = ra.HasApiKey();

            // No username / no Web API key → friendly empty state, hide the
            // panels that depend on per-user data. Friends sub-tab gets its
            // own placeholder (same condition) since lookups need the key.
            if (string.IsNullOrWhiteSpace(user) || !keyOk)
            {
                RAUnconfiguredCard.IsVisible = true;
                RAProfileCard.IsVisible = false;
                RARecentCard.IsVisible = false;
                FriendsUnconfiguredCard.IsVisible = true;
                FriendsListCard.IsVisible = false;
                FollowersCard.IsVisible = false;
                FriendsAddButton.IsEnabled = false;
                FriendsImportButton.IsEnabled = false;
                return;
            }

            RAUnconfiguredCard.IsVisible = false;
            RAProfileCard.IsVisible = true;
            RARecentCard.IsVisible = true;
            FriendsUnconfiguredCard.IsVisible = false;
            FriendsListCard.IsVisible = true;
            FollowersCard.IsVisible = true;
            FriendsAddButton.IsEnabled = true;
            // Don't re-enable the import button if a sync is in flight —
            // a tab-flip during import would otherwise let the user start
            // a second parallel sync.
            FriendsImportButton.IsEnabled = !_friendsImportInFlight;

            // Initial Friends-tab paint + subscribe to state change events.
            // RefreshFriendsView reads the local list synchronously from
            // config + cache; no network here.
            RefreshFriendsView();
            RefreshFriendsActivity();
            var friends = GetOrCreateFriendService();
            friends.FriendListChanged -= OnFriendsChanged;
            friends.FriendListChanged += OnFriendsChanged;
            friends.ActivityReceived  -= OnFriendActivity;
            friends.ActivityReceived  += OnFriendActivity;
            friends.FriendLbImproved  -= OnFriendLbImproved;
            friends.FriendLbImproved  += OnFriendLbImproved;
            // Polling starts here (idempotent). DispatcherTimer lives on
            // the UI thread; the Tick handler offloads network work via
            // Task.Run and marshals state back through FriendListChanged.
            friends.StartPolling();

            // Restore Expander state from config (persisted across sessions).
            // Paint cached state immediately. No network here — fast path.
            // PeekCachedWithMeta combines the row read + JSON parse into a
            // single DB hit (we need fetched_at later for the avatar cache-
            // buster anyway).
            var (cachedProfile, _) = ra.PeekCachedWithMeta<Models.RAUserProfile>($"user_profile:v2:user={user}");
            var cachedPoints  = ra.PeekCached<Models.RAUserPoints>($"user_points:v2:user={user}");
            var cachedRecent  = ra.PeekCached<List<Models.RAUserRecentAchievement>>($"user_recent:v2:user={user}");
            RenderProfileCard(cachedProfile, cachedPoints);
            RenderRecentUnlocks(cachedRecent);

            // Cold-paint the two new top-row panels from the cached spotlight
            // snapshot (already materialized in RaDataService — no joins).
            var cachedSpotlightTopRow = ra.PeekCached<Models.RALibrarySpotlight>($"library_spotlight:v2:user={user}");
            RenderInProgressTop5(cachedSpotlightTopRow);
            RenderRecentlyPlayedTop5(cachedSpotlightTopRow);

            // Cancel any prior in-flight fetch and kick a fresh refresh.
            // Dispose the previous CTS so its callback list is released —
            // without this we'd accumulate orphaned cancelled CTS instances
            // on every tab click.
            try { _raTabCts?.Cancel(); _raTabCts?.Dispose(); } catch { }
            _raTabCts = new System.Threading.CancellationTokenSource();
            var ct = _raTabCts.Token;
            // Trophy case (peek paints stale; bg fetch fills fresh).
            var cachedAwards = ra.PeekCached<Models.RAUserAwards>($"user_awards:user={user}");
            RenderTrophyCase(cachedAwards);

            // Library Spotlight (cached snapshot — already materialized in
            // RaDataService so render is just iteration, no joins here).
            var cachedSpotlight = ra.PeekCached<Models.RALibrarySpotlight>($"library_spotlight:v2:user={user}");
            RenderLibrarySpotlight(cachedSpotlight);

            // Featured / Discovery — all three panels share the same cold-paint pattern.
            RenderAchievementOfTheWeek(ra.PeekCached<Models.RAAchievementOfTheWeek>("achievement_of_the_week"));
            RenderCommunityPulse(ra.PeekCached<List<Models.RARecentGameAward>>("recent_game_awards:c=25"));
            RenderTopTen(ra.PeekCached<List<RaDataService.TopTenEntry>>("top_ten_users:v2"));

            // Heatmap — render from persisted ra_heatmap_daily for instant
            // paint. Background task tops up today's bucket if stale.
            try
            {
                var endUtc = DateTime.UtcNow.Date;
                var startUtc = endUtc.AddDays(-89);
                var persistedHeatmap = _db!.GetRaHeatmapRange(user, startUtc.ToString("yyyy-MM-dd"), endUtc.ToString("yyyy-MM-dd"));
                RenderHeatmap(persistedHeatmap);
            }
            catch { /* empty grid until refresh lands */ }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // Profile + points are the priority pair — fetch first so
                    // the header lights up before the heavier recent feed lands.
                    var profileTask = ra.GetProfileAsync(ct);
                    var pointsTask  = ra.GetPointsAsync(ct);
                    await System.Threading.Tasks.Task.WhenAll(profileTask, pointsTask).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post((() =>
                        RenderProfileCard(profileTask.Result, pointsTask.Result)));

                    // Recent unlocks (5-min TTL, ~50KB max).
                    var recent = await ra.GetRecentAsync(ct).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post((() => RenderRecentUnlocks(recent)));

                    // Trophy case (1h TTL).
                    var awards = await ra.GetAwardsAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post((() => RenderTrophyCase(awards)));

                    // Library Spotlight (15-min TTL, materialized in service).
                    // Same snapshot feeds the new top-row Closest-to-Mastering
                    // + Recently-Played panels and the lower spotlight strip.
                    var spotlight = await ra.GetLibrarySpotlightAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post((() =>
                    {
                        RenderInProgressTop5(spotlight);
                        RenderRecentlyPlayedTop5(spotlight);
                        RenderLibrarySpotlight(spotlight);
                    }));

                    // Featured / Discovery + heatmap (parallel fan — none
                    // share per-user state; heatmap's typical cost is
                    // one network call for today's bucket or nothing if
                    // the TTL marker is still warm).
                    var aotwTask = ra.GetAchievementOfTheWeekAsync(ct);
                    var pulseTask = ra.GetRecentGameAwardsAsync(25, ct);
                    var topTenTask = ra.GetTopTenAsync(ct);
                    var heatmapTask = ra.GetHeatmapAsync(90, ct);
                    await System.Threading.Tasks.Task.WhenAll(aotwTask, pulseTask, topTenTask, heatmapTask).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post((() =>
                    {
                        RenderAchievementOfTheWeek(aotwTask.Result);
                        RenderCommunityPulse(pulseTask.Result);
                        RenderTopTen(topTenTask.Result);
                        RenderHeatmap(heatmapTask.Result);
                    }));
                }
                catch (OperationCanceledException) { /* tab switched away */ }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] Achievements tab refresh failed: {ex.Message}");
                }
            });
        }

        // Resolves the username used for cache-key lookups + display
        // fallback. Authoritative source is RaDataService.CurrentUser()
        // (the config-time username); the audit caught that taking a
        // separate fallbackUser parameter was a footgun — a future caller
        // could pass profile.User instead, which RA returns in registered
        // casing and would silently miss the cache-buster lookup.
        private void RenderProfileCard(Models.RAUserProfile? profile, Models.RAUserPoints? points)
        {
            string fallbackUser = _raData?.CurrentUser() ?? "";
            RAProfileName.Text = string.IsNullOrWhiteSpace(profile?.User) ? fallbackUser : profile!.User;
            RAProfileMotto.Text = string.IsNullOrWhiteSpace(profile?.Motto) ? "" : "“" + profile!.Motto + "”";
            RAProfileMotto.IsVisible = !(string.IsNullOrWhiteSpace(profile?.Motto) );

            if (!string.IsNullOrWhiteSpace(profile?.MemberSince))
            {
                if (DateTime.TryParse(profile!.MemberSince, out var since))
                    RAProfileMemberSince.Text = $"Member since {since:MMMM yyyy}";
                else
                    RAProfileMemberSince.Text = $"Member since {profile.MemberSince}";
            }
            else
            {
                RAProfileMemberSince.Text = "";
            }

            int hardcore = points?.Points ?? profile?.TotalPoints ?? 0;
            int softcore = points?.SoftcorePoints ?? profile?.TotalSoftcorePoints ?? 0;
            RAProfilePoints.Text   = hardcore.ToString("N0");
            RAProfileSoftcore.Text = softcore.ToString("N0");

            // Avatar — RA serves UserPic as an absolute path like "/UserPic/Foo.png".
            // The path stays stable across avatar changes (same filename, new
            // bytes), and WPF's BitmapImage caches downloads by URL, so a
            // raw URL would silently serve the previously-downloaded image
            // even after a fresh profile JSON arrives. Stamp the URL with
            // the cache row's fetched_at so each TTL cycle produces a new
            // URL → forces a fresh download, while within-TTL renders reuse
            // the same URL → WPF cache hits cheaply.
            if (!string.IsNullOrWhiteSpace(profile?.UserPic))
            {
                try
                {
                    string trimmed = profile!.UserPic!.Trim();
                    string url = trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? trimmed
                        : "https://media.retroachievements.org" + trimmed;

                    long stamp = _raData?.PeekCachedFetchedAt($"user_profile:v2:user={fallbackUser}") ?? 0L;
                    string sep = url.Contains('?') ? "&" : "?";
                    string bustedUrl = stamp > 0 ? $"{url}{sep}v={stamp}" : url;

                    FriendImageLoader.Load(RAProfileAvatar, bustedUrl, "ra-tab", "avatar");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] render avatar failed: {ex.Message}");
                }
            }
        }

        // ── Compact top-row: Closest to Mastering ──────────────────────────
        // First 8 in-progress games sorted by smallest remaining-achievement
        // gap (the same order RaDataService computes for the spotlight).
        private void RenderInProgressTop5(Models.RALibrarySpotlight? spotlight)
        {
            RAInProgressItems.Items.Clear();
            var items = spotlight?.ClosestToMastering;
            if (items == null || items.Count == 0)
            {
                RAInProgressEmpty.IsVisible = true;
                return;
            }
            RAInProgressEmpty.IsVisible = false;

            var ctx = BuildMiniRowContext();
            int rendered = 0;
            foreach (var g in items)
            {
                RAInProgressItems.Items.Add(BuildSpotlightMiniRow(g, ctx));
                if (++rendered >= 8) break;
            }
        }

        // ── Compact top-row: Recently Played ───────────────────────────────
        // The RA "recently played" list (most-recent-first), capped at 8.
        private void RenderRecentlyPlayedTop5(Models.RALibrarySpotlight? spotlight)
        {
            RARecentPlayedItems.Items.Clear();
            var items = spotlight?.ContinueWhereLeftOff;
            if (items == null || items.Count == 0)
            {
                RARecentPlayedEmpty.IsVisible = true;
                return;
            }
            RARecentPlayedEmpty.IsVisible = false;

            var ctx = BuildMiniRowContext();
            int rendered = 0;
            foreach (var g in items)
            {
                RARecentPlayedItems.Items.Add(BuildSpotlightMiniRow(g, ctx));
                if (++rendered >= 8) break;
            }
        }

        private RecentRowContext BuildMiniRowContext() => new RecentRowContext
        {
            Font          = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
            TextPrimary   = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush"),
            TextSecondary = (Avalonia.Media.IBrush)RaRes("TextSecondaryBrush"),
            TextMuted     = (Avalonia.Media.IBrush)RaRes("TextMutedBrush"),
            BgTertiary    = (Avalonia.Media.IBrush)RaRes("BgTertiaryBrush"),
            Accent        = (Avalonia.Media.IBrush)RaRes("AccentBrush"),
        };

        // Single mini-row for the top-row spotlight panels. 36px icon +
        // title/subtitle stack + percentage chip. Matches the visual rhythm
        // of BuildRecentRow so the three top-row cards read as a family.
        private static Control BuildSpotlightMiniRow(Models.RASpotlightGame g, RecentRowContext ctx)
        {
            var row = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Game icon (36px square, rounded). Falls back to grey square
            // when no RA icon path is available — never throws on a bad URL.
            var iconBorder = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(4), ClipToBounds = true,
                Background = ctx.BgTertiary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (!string.IsNullOrEmpty(g.ImageIcon))
            {
                try
                {
                    var img__ = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.UniformToFill };
                    iconBorder.Child = img__;
                    FriendImageLoader.Load(img__, ($"https://media.retroachievements.org{g.ImageIcon}").ToString(), "ra-tab");
                }
                catch { }
            }
            Grid.SetColumn(iconBorder, 0);
            row.Children.Add(iconBorder);

            // Title + subtitle
            var stack = new StackPanel
            {
                Margin = new Thickness(10, 0, 6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            stack.Children.Add(new TextBlock
            {
                Text = g.Title,
                FontFamily = ctx.Font,
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ctx.TextPrimary,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock
            {
                Text = g.Subtitle,
                FontFamily = ctx.Font,
                FontSize = 11,
                Foreground = ctx.TextMuted,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(stack, 1);
            row.Children.Add(stack);

            // Percentage chip (right-aligned). MaxPossible can legitimately
            // be 0 on the "Recently Played" path for new RA-tagged games
            // before the user unlocks anything — show "—" rather than NaN.
            string pctText = "—";
            if (g.MaxPossible > 0)
            {
                int pct = (int)Math.Round(100.0 * g.NumAchieved / g.MaxPossible);
                pctText = $"{pct}%";
            }
            var pctBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Background = ctx.BgTertiary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            pctBorder.Child = new TextBlock
            {
                Text = pctText,
                FontFamily = ctx.Font,
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ctx.TextSecondary,
            };
            Grid.SetColumn(pctBorder, 2);
            row.Children.Add(pctBorder);

            return row;
        }

        private void RenderRecentUnlocks(List<Models.RAUserRecentAchievement>? unlocks)
        {
            RARecentItems.Items.Clear();
            if (unlocks == null || unlocks.Count == 0)
            {
                RARecentEmpty.IsVisible = true;
                return;
            }
            RARecentEmpty.IsVisible = false;

            // Hoist resource lookups: FindResource walks the visual tree on
            // every call, and a 20-row render previously did ~140 of them.
            // Resolve once, pass into BuildRecentRow.
            var ctx = new RecentRowContext
            {
                Font          = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
                TextPrimary   = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush"),
                TextSecondary = (Avalonia.Media.IBrush)RaRes("TextSecondaryBrush"),
                TextMuted     = (Avalonia.Media.IBrush)RaRes("TextMutedBrush"),
                BgTertiary    = (Avalonia.Media.IBrush)RaRes("BgTertiaryBrush"),
                Accent        = (Avalonia.Media.IBrush)RaRes("AccentBrush"),
            };

            int rendered = 0;
            foreach (var u in unlocks)
            {
                RARecentItems.Items.Add(BuildRecentRow(u, ctx));
                if (++rendered >= 20) break;
            }
        }

        // Lookup bundle for BuildRecentRow — resolved once per render.
        private sealed class RecentRowContext
        {
            public Avalonia.Media.FontFamily Font = null!;
            public Avalonia.Media.IBrush TextPrimary = null!;
            public Avalonia.Media.IBrush TextSecondary = null!;
            public Avalonia.Media.IBrush TextMuted = null!;
            public Avalonia.Media.IBrush BgTertiary = null!;
            public Avalonia.Media.IBrush Accent = null!;
        }

        private static Control BuildRecentRow(Models.RAUserRecentAchievement u, RecentRowContext ctx)
        {
            // Single-row layout: 32px badge + title/subtitle stack + points pill on the right.
            var row = new Grid { Margin = new Thickness(14, 10, 14, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Badge
            var badgeBorder = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(4), ClipToBounds = true,
                Background = ctx.BgTertiary,
            };
            if (!string.IsNullOrEmpty(u.BadgeName))
            {
                try
                {
                    var img__ = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.UniformToFill };
                    badgeBorder.Child = img__;
                    FriendImageLoader.Load(img__, ($"https://media.retroachievements.org/Badge/{u.BadgeName}.png").ToString(), "ra-tab");
                }
                catch { }
            }
            Grid.SetColumn(badgeBorder, 0);
            row.Children.Add(badgeBorder);

            // Title + subtitle
            var stack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = u.Title,
                FontFamily = ctx.Font,
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ctx.TextPrimary,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            });
            string when = FormatTimeAgo(u.Date);
            string sub = $"{u.GameTitle} · {u.ConsoleName}";
            if (!string.IsNullOrWhiteSpace(when)) sub += $" · {when}";
            stack.Children.Add(new TextBlock
            {
                Text = sub,
                FontFamily = ctx.Font,
                FontSize = 11,
                Foreground = ctx.TextMuted,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(stack, 1);
            row.Children.Add(stack);

            // Points pill (hardcore unlocks get the accent tint to mark them).
            bool hc = u.HardcoreMode == 1;
            var ptsBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                Background = hc ? ctx.Accent : ctx.BgTertiary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            ptsBorder.Child = new TextBlock
            {
                Text = $"{u.Points} pts",
                FontFamily = ctx.Font,
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = hc ? Avalonia.Media.Brushes.White : ctx.TextSecondary,
            };
            Grid.SetColumn(ptsBorder, 2);
            row.Children.Add(ptsBorder);

            // Tooltip with full description
            if (!string.IsNullOrEmpty(u.Description))
                ToolTip.SetTip(row, u.Description);

            return row;
        }

        // Award ring colors for the trophy case. Gold for mastery (hardcore
        // 100%), silver for completion (softcore 100%), bronze for beaten-
        // hardcore, muted for beaten-softcore. Frozen brushes so they're
        // safe to share across many tiles.
        private static readonly Avalonia.Media.IBrush _ringMastery = MakeFrozenBrush(0xFF, 0xC8, 0x3D);
        private static readonly Avalonia.Media.IBrush _ringCompletion = MakeFrozenBrush(0xC0, 0xC8, 0xD0);
        private static readonly Avalonia.Media.IBrush _ringBeatenHardcore = MakeFrozenBrush(0xB2, 0x72, 0x43);
        private static readonly Avalonia.Media.IBrush _ringBeatenSoftcore = MakeFrozenBrush(0x55, 0x55, 0x5A);

        private static Avalonia.Media.IBrush MakeFrozenBrush(byte r, byte g, byte b)
        {
            var br = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(r, g, b));
            return br;
        }

        private void RenderTrophyCase(Models.RAUserAwards? awards)
        {
            RATrophyWall.Items.Clear();
            RATrophyRollups.Children.Clear();

            if (awards == null
                || awards.VisibleUserAwards == null
                || awards.VisibleUserAwards.Count == 0)
            {
                RATrophyEmpty.IsVisible = true;
                return;
            }
            RATrophyEmpty.IsVisible = false;

            var font = (Avalonia.Media.FontFamily)RaRes("PrimaryFont");
            var bgTertiary = (Avalonia.Media.IBrush)RaRes("BgTertiaryBrush");
            var textMuted = (Avalonia.Media.IBrush)RaRes("TextMutedBrush");

            // Rollup chips: only show counts > 0. Mastery / Completion /
            // Beaten Hardcore / Beaten Softcore — the four canonical RA award
            // kinds, in importance order. Foreground per chip so the
            // dark-muted "beaten" pill stays legible (black-on-gray fails
            // contrast; white-on-gray reads fine).
            AppendRollupChip(font, _ringMastery,        Avalonia.Media.Brushes.Black, "mastered",    awards.MasteryAwardsCount);
            AppendRollupChip(font, _ringCompletion,     Avalonia.Media.Brushes.Black, "completed",   awards.CompletionAwardsCount);
            AppendRollupChip(font, _ringBeatenHardcore, Avalonia.Media.Brushes.Black, "beaten (hc)", awards.BeatenHardcoreAwardsCount);
            AppendRollupChip(font, _ringBeatenSoftcore, Avalonia.Media.Brushes.White, "beaten",      awards.BeatenSoftcoreAwardsCount);

            // Badge wall: most-recent first, filter out non-game awards (event
            // / site badges) — those have no Mastery / Beaten classification
            // and would render ring-less mid-shelf which looks broken. Cap
            // at 100 so a thousand-award user doesn't render an enormous
            // visual tree all at once. AwardedAt sorts lexicographically
            // because RA returns "yyyy-MM-dd HH:mm:ss" — same as chronological.
            var gameAwards = awards.VisibleUserAwards
                .Where(a => a.AwardType == "Mastery/Completion" || a.AwardType == "Game Beaten")
                .OrderByDescending(a => a.AwardedAt ?? "")
                .Take(100)
                .ToList();
            foreach (var a in gameAwards)
                RATrophyWall.Items.Add(BuildTrophyTile(a, bgTertiary));

            int totalGameAwards = awards.VisibleUserAwards.Count(
                a => a.AwardType == "Mastery/Completion" || a.AwardType == "Game Beaten");
            if (totalGameAwards > gameAwards.Count)
            {
                // Footer label sits below the WrapPanel via the StackPanel
                // wrapper in XAML, not inside the wall — otherwise it would
                // wrap onto whatever row had space and look accidental.
                var footer = new TextBlock
                {
                    Text = $"+ {totalGameAwards - gameAwards.Count} older awards",
                    FontFamily = font,
                    FontSize = 11,
                    Foreground = textMuted,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Margin = new Thickness(0, 8, 4, 0),
                };
                if (RATrophyCard.Child is StackPanel cardStack)
                {
                    // Remove any prior footer first (re-render swap).
                    for (int i = cardStack.Children.Count - 1; i >= 0; i--)
                        if (cardStack.Children[i] is TextBlock tb && tb.Name == "RATrophyFooter")
                            cardStack.Children.RemoveAt(i);
                    footer.Name = "RATrophyFooter";
                    cardStack.Children.Add(footer);
                }
            }
            else if (RATrophyCard.Child is StackPanel cardStack2)
            {
                for (int i = cardStack2.Children.Count - 1; i >= 0; i--)
                    if (cardStack2.Children[i] is TextBlock tb && tb.Name == "RATrophyFooter")
                        cardStack2.Children.RemoveAt(i);
            }
        }

        private void AppendRollupChip(Avalonia.Media.FontFamily font,
                                       Avalonia.Media.IBrush ringColor,
                                       Avalonia.Media.IBrush foreground,
                                       string label, int count)
        {
            if (count <= 0) return;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(6, 0, 0, 0),
                Background = ringColor,
                Opacity = 0.92,
            };
            pill.Child = new TextBlock
            {
                Text = $"{count} {label}",
                FontFamily = font,
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = foreground,
            };
            RATrophyRollups.Children.Add(pill);
        }

        private Control BuildTrophyTile(Models.RAVisibleAward award, Avalonia.Media.IBrush bgFallback)
        {
            // Award-type → ring color. Mastery (hardcore 100%) gets gold, the
            // softcore equivalent silver, and the beaten awards bronze/muted.
            (Avalonia.Media.IBrush ring, string label) classify = award.AwardType switch
            {
                "Mastery/Completion" => award.AwardDataExtra == 1
                    ? (_ringMastery, "Mastered")
                    : (_ringCompletion, "Completed"),
                "Game Beaten" => award.AwardDataExtra == 1
                    ? (_ringBeatenHardcore, "Beaten (Hardcore)")
                    : (_ringBeatenSoftcore, "Beaten"),
                _ => (bgFallback, award.AwardType ?? "Award"),
            };

            const double TileSize = 60;
            const double Margin = 4;

            // Outer border = colored ring. Inner border = matching CornerRadius
            // with ClipToBounds so the game-icon Image is actually clipped to
            // the rounded shape (WPF's ClipToBounds on the outer Border alone
            // clips to the rectangle, not the rounded corners — the image
            // would punch through the rounding without the inner wrapper).
            var outer = new Border
            {
                Width = TileSize, Height = TileSize,
                Margin = new Thickness(Margin),
                CornerRadius = new CornerRadius(8),
                Background = bgFallback,
                BorderBrush = classify.ring,
                BorderThickness = new Thickness(2),
            };
            var inner = new Border
            {
                CornerRadius = new CornerRadius(6),  // outer 8 − 2px stroke = inner 6
                ClipToBounds = true,
                Background = bgFallback,
            };
            outer.Child = inner;

            if (!string.IsNullOrEmpty(award.ImageIcon))
            {
                try
                {
                    string trimmed = award.ImageIcon!.Trim();
                    string url = trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? trimmed
                        : "https://media.retroachievements.org" + trimmed;
                    var img__ = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.UniformToFill };
                    inner.Child = img__;
                    FriendImageLoader.Load(img__, (url).ToString(), "ra-tab");
                }
                catch { }
            }

            // Tooltip with full context — title, console, kind, date
            var tip = new System.Text.StringBuilder();
            tip.AppendLine(award.Title ?? "Untitled");
            if (!string.IsNullOrEmpty(award.ConsoleName))
            {
                tip.Append(award.ConsoleName);
                tip.Append("  ·  ");
            }
            tip.AppendLine(classify.label);
            if (!string.IsNullOrWhiteSpace(award.AwardedAt))
            {
                if (DateTime.TryParse(award.AwardedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt))
                    tip.Append(dt.ToLocalTime().ToString("MMM d, yyyy"));
                else
                    tip.Append(award.AwardedAt);
            }
            ToolTip.SetTip(outer, tip.ToString().TrimEnd());
            return outer;
        }

        // ── Library Spotlight ─────────────────────────────────────────────

        private void RenderLibrarySpotlight(Models.RALibrarySpotlight? spotlight)
        {
            RASpotlightStack.Children.Clear();
            if (spotlight == null) return;

            var font          = (Avalonia.Media.FontFamily)RaRes("PrimaryFont");
            var bgSecondary   = (Avalonia.Media.IBrush)RaRes("BgSecondaryBrush");
            var bgTertiary    = (Avalonia.Media.IBrush)RaRes("BgTertiaryBrush");
            var borderSubtle  = (Avalonia.Media.IBrush)RaRes("BorderSubtleBrush");
            var textPrimary   = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush");
            var textSecondary = (Avalonia.Media.IBrush)RaRes("TextSecondaryBrush");
            var textMuted     = (Avalonia.Media.IBrush)RaRes("TextMutedBrush");

            var ctx = new SpotlightContext
            {
                Font = font, BgSecondary = bgSecondary, BgTertiary = bgTertiary,
                BorderSubtle = borderSubtle, TextPrimary = textPrimary,
                TextSecondary = textSecondary, TextMuted = textMuted,
            };

            // Closest-to-Mastering and Continue-Where-Left-Off moved into the
            // top-row compact panels (RenderInProgressTop5 + RenderRecentlyPlayedTop5).
            // The lower spotlight strip keeps the other three angles.
            AppendSpotlightQuickWinsPanel("QUICK WINS", spotlight.QuickWins, ctx);
            AppendSpotlightGamePanel("OWNED BUT NEVER STARTED", spotlight.NeverStarted, ctx);
            AppendSpotlightGamePanel("WISHLIST YOU OWN", spotlight.WishlistOwned, ctx);

            // If every panel is empty, fall back to a single explanation row.
            if (RASpotlightStack.Children.Count == 0)
            {
                var empty = new Border
                {
                    Background = bgSecondary,
                    BorderBrush = borderSubtle,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16, 22, 16, 22),
                };
                empty.Child = new TextBlock
                {
                    Text = "Launch a RetroAchievements-supported game at least once to start populating these panels.",
                    FontFamily = font,
                    FontSize = 12,
                    Foreground = textMuted,
                    TextAlignment = Avalonia.Media.TextAlignment.Center,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                };
                RASpotlightStack.Children.Add(empty);
            }
        }

        private sealed class SpotlightContext
        {
            public Avalonia.Media.FontFamily Font = null!;
            public Avalonia.Media.IBrush BgSecondary = null!;
            public Avalonia.Media.IBrush BgTertiary = null!;
            public Avalonia.Media.IBrush BorderSubtle = null!;
            public Avalonia.Media.IBrush TextPrimary = null!;
            public Avalonia.Media.IBrush TextSecondary = null!;
            public Avalonia.Media.IBrush TextMuted = null!;
        }

        private void AppendSpotlightGamePanel(string header, List<Models.RASpotlightGame> items, SpotlightContext ctx)
        {
            if (items == null || items.Count == 0) return;

            // Section header (small caps, muted).
            RASpotlightStack.Children.Add(new TextBlock
            {
                Text = header,
                FontFamily = ctx.Font,
                FontSize = 10,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ctx.TextMuted,
                Margin = new Thickness(2, 12, 0, 6),
            });

            var card = new Border
            {
                Background = ctx.BgSecondary,
                BorderBrush = ctx.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
            };
            // WrapPanel so tiles beyond the visible width spill onto a second
            // row instead of getting clipped. At 160+12px per game tile,
            // ~6 fit per row at the card's max content width; 10 tiles
            // produces a tidy two-row shelf.
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items)
                row.Children.Add(BuildSpotlightGameTile(item, ctx));
            card.Child = row;
            RASpotlightStack.Children.Add(card);
        }

        private Control BuildSpotlightGameTile(Models.RASpotlightGame item, SpotlightContext ctx)
        {
            // Tile = 160-wide column: 80x80 icon on top, title (1 line),
            // console (1 line, muted), subtitle (1 line, accent or muted).
            // Click anywhere → open the local game's detail card if we can
            // resolve the local Game row by LocalGameId.
            var col = new StackPanel
            {
                Width = 160,
                Margin = new Thickness(6),
                Cursor = item.LocalGameId > 0 ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : Avalonia.Input.Cursor.Default,
            };

            // Icon — outer Border for background fallback, inner rounded
            // wrapper for the actual image so the corners clip properly.
            var iconOuter = new Border
            {
                Width = 80, Height = 80,
                CornerRadius = new CornerRadius(8),
                Background = ctx.BgTertiary,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            var iconInner = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
            };
            iconOuter.Child = iconInner;
            // Prefer the local cover-art path when the spotlight item carries
            // one (the "Never started" panel uses this fallback because RA's
            // completion response doesn't include an image icon). Falls back
            // to the RA image-icon URL otherwise.
            try
            {
                var img = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.UniformToFill };
                if (!string.IsNullOrEmpty(item.LocalArtPath) && System.IO.File.Exists(item.LocalArtPath))
                {
                    // Local cover art decodes synchronously — no network hop.
                    img.Source = new Avalonia.Media.Imaging.Bitmap(item.LocalArtPath!);
                    iconInner.Child = img;
                }
                else if (!string.IsNullOrEmpty(item.ImageIcon))
                {
                    string trimmed = item.ImageIcon!.Trim();
                    string url = trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? trimmed
                        : "https://media.retroachievements.org" + trimmed;
                    iconInner.Child = img;
                    FriendImageLoader.Load(img, url, "ra-tab", "spotlight");
                }
            }
            catch { }
            col.Children.Add(iconOuter);

            // Title
            col.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontFamily = ctx.Font,
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ctx.TextPrimary,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            });
            // Console
            col.Children.Add(new TextBlock
            {
                Text = item.Console,
                FontFamily = ctx.Font,
                FontSize = 10,
                Foreground = ctx.TextMuted,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            });
            // Subtitle (panel-specific blurb — kept on its own line so the
            // tiles align)
            col.Children.Add(new TextBlock
            {
                Text = item.Subtitle,
                FontFamily = ctx.Font,
                FontSize = 11,
                Foreground = ctx.TextSecondary,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            });

            // Tooltip + visual cue when the local library doesn't have this
            // game yet. Cursor stays Arrow (not Hand) and the tile dims to
            // make the non-clickable state legible without being noisy.
            if (item.LocalGameId > 0)
            {
                ToolTip.SetTip(col, item.Title);
                int localId = item.LocalGameId;
                col.PointerReleased += (_, e) =>
                {
                    e.Handled = true;
                    OpenLocalGameDetail(localId);
                };
            }
            else
            {
                col.Opacity = 0.6;
                ToolTip.SetTip(col, $"{item.Title} — not in your local library");
            }
            return col;
        }

        private void AppendSpotlightQuickWinsPanel(string header, List<Models.RASpotlightQuickWin> items, SpotlightContext ctx)
        {
            if (items == null || items.Count == 0) return;

            RASpotlightStack.Children.Add(new TextBlock
            {
                Text = header,
                FontFamily = ctx.Font,
                FontSize = 10,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ctx.TextMuted,
                Margin = new Thickness(2, 12, 0, 6),
            });

            var card = new Border
            {
                Background = ctx.BgSecondary,
                BorderBrush = ctx.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
            };
            // WrapPanel — same overflow story as the game-tile rows. At
            // 140+12px per quick-win tile ~7 fit per row; 10 wraps cleanly.
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items)
                row.Children.Add(BuildQuickWinTile(item, ctx));
            card.Child = row;
            RASpotlightStack.Children.Add(card);
        }

        private Control BuildQuickWinTile(Models.RASpotlightQuickWin item, SpotlightContext ctx)
        {
            var col = new StackPanel
            {
                Width = 140,
                Margin = new Thickness(6),
                Cursor = item.LocalGameId > 0 ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : Avalonia.Input.Cursor.Default,
            };

            // 56x56 badge tile (matches the "Coming up" row treatment on the
            // game detail card so the visual vocabulary is consistent).
            var badgeOuter = new Border
            {
                Width = 56, Height = 56,
                CornerRadius = new CornerRadius(8),
                Background = ctx.BgTertiary,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            var badgeInner = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
            };
            badgeOuter.Child = badgeInner;
            if (!string.IsNullOrEmpty(item.BadgeName))
            {
                try
                {
                    var img__ = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.UniformToFill };
                    badgeInner.Child = img__;
                    FriendImageLoader.Load(img__, ($"https://media.retroachievements.org/Badge/{item.BadgeName}.png").ToString(), "ra-tab");
                }
                catch { }
            }
            col.Children.Add(badgeOuter);

            col.Children.Add(new TextBlock
            {
                Text = item.AchievementTitle,
                FontFamily = ctx.Font,
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ctx.TextPrimary,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });
            col.Children.Add(new TextBlock
            {
                Text = item.GameTitle,
                FontFamily = ctx.Font,
                FontSize = 10,
                Foreground = ctx.TextMuted,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            });
            col.Children.Add(new TextBlock
            {
                Text = "~" + FormatDurationShort(item.MedianSeconds),
                FontFamily = ctx.Font,
                FontSize = 11,
                Foreground = ctx.TextSecondary,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            });

            var tip = new System.Text.StringBuilder();
            tip.AppendLine(item.AchievementTitle);
            if (!string.IsNullOrEmpty(item.Description))
            {
                tip.AppendLine();
                tip.AppendLine(item.Description);
            }
            tip.AppendLine();
            tip.Append($"{item.GameTitle} · {item.Console}");
            if (item.LocalGameId <= 0) tip.Append(" · (not in your local library)");
            ToolTip.SetTip(col, tip.ToString());

            if (item.LocalGameId > 0)
            {
                int localId = item.LocalGameId;
                col.PointerReleased += (_, e) =>
                {
                    e.Handled = true;
                    OpenLocalGameDetail(localId);
                };
            }
            else
            {
                col.Opacity = 0.6;
            }
            return col;
        }

        private void OpenLocalGameDetail(int localGameId)
        {
            try
            {
                var game = _db?.GetGameById(localGameId);
                if (game == null) return;
                // Mirror the Library grid tile-click pattern (line ~1537):
                // modeless Show() + _openDetailWindow tracking so the
                // outside-click dismiss handler can close us, and so we
                // can't stack two detail windows. Switching to ShowDialog
                // here would block the Achievements tab and bypass the
                // single-window invariant.
                _openDetailWindow?.Close();
                _openDetailWindow = new Views.GameDetailWindow(game);
                _openDetailWindow.Closed += (_, _) => { _openDetailWindow = null; };
                _openDetailWindow.Show(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RA] OpenLocalGameDetail failed: {ex.Message}");
            }
        }

        // ── Featured / Discovery renderers ────────────────────────────────

        private void RenderAchievementOfTheWeek(Models.RAAchievementOfTheWeek? aotw)
        {
            if (aotw == null || aotw.Achievement == null)
            {
                RAOfTheWeekCard.IsVisible = false;
                return;
            }
            RAOfTheWeekCard.IsVisible = true;

            // Gold ring matches the trophy-case mastery treatment.
            RAOfTheWeekBadgeOuter.BorderBrush = _ringMastery;

            RAOfTheWeekTitle.Text = aotw.Achievement.Title ?? "";
            RAOfTheWeekDescription.Text = aotw.Achievement.Description ?? "";
            RAOfTheWeekGame.Text = aotw.Game?.Title is { } gt && !string.IsNullOrEmpty(gt)
                ? $"{gt} · {aotw.Console?.Title ?? ""}".TrimEnd(' ', '·')
                : "";

            int total = aotw.TotalPlayers;
            int unlocks = aotw.UnlocksCount;
            int hc = aotw.UnlocksHardcoreCount;
            if (total > 0 && unlocks > 0)
            {
                double pct = unlocks * 100.0 / total;
                RAOfTheWeekStats.Text = hc > 0
                    ? $"{unlocks:N0} of {total:N0} players ({pct:0.#}%) · {hc:N0} hardcore"
                    : $"{unlocks:N0} of {total:N0} players ({pct:0.#}%)";
            }
            else
            {
                RAOfTheWeekStats.Text = "";
            }

            // Badge image
            RAOfTheWeekBadgeInner.Child = null;
            string? badge = aotw.Achievement.BadgeName;
            if (!string.IsNullOrEmpty(badge))
            {
                try
                {
                    var img__ = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.UniformToFill };
                    RAOfTheWeekBadgeInner.Child = img__;
                    FriendImageLoader.Load(img__, ($"https://media.retroachievements.org/Badge/{badge}.png").ToString(), "ra-tab");
                }
                catch { }
            }

            // Play Now visible only if the AOTW's game exists in the local
            // library (we can launch). Indexed lookup so we don't scan
            // every game row on every render.
            RAOfTheWeekPlay.IsVisible = false;
            try
            {
                int gid = aotw.Game?.Id ?? 0;
                if (gid > 0)
                {
                    int? localId = _db?.GetLocalGameIdByRAGameId(gid);
                    if (localId.HasValue && localId.Value > 0)
                    {
                        RAOfTheWeekPlay.Tag = localId.Value;
                        RAOfTheWeekPlay.IsVisible = true;
                    }
                }
            }
            catch { }
        }

        private void RAOfTheWeekPlay_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Opens the game's detail card (where the user can hit Play Now
            // with the existing core-routing logic). Doesn't launch the
            // emulator directly — that'd skip console-specific prep.
            if (sender is Button btn && btn.Tag is int localId && localId > 0)
                OpenLocalGameDetail(localId);
        }

        // ── Achievements: Friends sub-tab ─────────────────────────────────

        // Debounces RefreshFriendsView calls during RefreshAllAsync —
        // FriendService fires FriendListChanged per friend per cycle,
        // which would rebuild the visual tree N times for a single
        // poll burst. Coalesce to one refresh after 150ms of quiet.
        private DispatcherTimer? _friendsRefreshDebounce;

        private void OnFriendsChanged(object? sender, EventArgs e)
        {
            // FriendService can fire from any thread; marshal to UI.
            // Guard against shutdown — BeginInvoke after dispatcher
            // shutdown throws TaskCanceledException. Matches the pattern
            // at MainWindow:268 / :279 elsewhere in the file.
            try
            {Avalonia.Threading.Dispatcher.UIThread.Post((() =>
                {
                    if (_friendsRefreshDebounce == null)
                    {
                        _friendsRefreshDebounce = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(150),
                        };
                        _friendsRefreshDebounce.Tick += (_, __) =>
                        {
                            _friendsRefreshDebounce!.Stop();
                            RefreshFriendsView();
                            RefreshFriendsActivity();
                        };
                    }
                    _friendsRefreshDebounce.Stop();
                    _friendsRefreshDebounce.Start();
                }));
            }
            catch { }
        }

        // Phase 6b.2 — "friend beat your score" toast from polling diff.
        // Fires once per friend per poll cycle that produced improvements.
        // CRITICAL: this event is raised from FriendService's poll thread
        // (NOT the UI thread), so we MUST marshal to the UI thread before
        // touching ToastStack or any other WPF surface. async/await would
        // resume on whatever SyncContext was captured at the entry — on
        // a poll thread that's the ThreadPool, NOT WPF's dispatcher.
        // Wrapping the whole handler in Dispatcher.BeginInvoke is the
        // only safe pattern; the existing OnFriendActivity does this too.
        private void OnFriendLbImproved(object? sender, FriendService.FriendLbImprovementEvent ev)
        {
            try { Avalonia.Threading.Dispatcher.UIThread.Post(async () => await HandleFriendLbImprovedOnUi(ev)); }
            catch { }
        }

        // Per-game cache of MY OWN LB ranks (rank-by-LB-id). 30-minute TTL
        // — long enough to avoid 20-friends-in-one-cycle pile-up of
        // identical fetches, short enough that I see my own progress
        // reflected after I play between sessions.
        private readonly Dictionary<int, (Dictionary<int, int> ranks, DateTime fetchedUtc)> _myLbRanksByGame = new();
        // Sound cooldown across all "friend beat you" toasts (any LB).
        // Without this, a friend who improves on 10 LBs in one cycle
        // would play the chime 10 times.
        private DateTime _lastFriendBeatSoundUtc = DateTime.MinValue;

        private async System.Threading.Tasks.Task HandleFriendLbImprovedOnUi(FriendService.FriendLbImprovementEvent ev)
        {
            try
            {
                var cfg = App.Configuration?.GetFriendsConfiguration();
                if (cfg == null || !cfg.LbToastWhenBeaten) return;
                // RA web API doesn't expose HC/SC per LB submission; if
                // the user asked for HC-only signal, the conservative
                // choice is to suppress friend LB toasts wholesale rather
                // than mis-flag a softcore score as hardcore. Matches the
                // achievement-toast HC filter at line 3835 in spirit
                // (that one has per-event HC info; this path doesn't).
                if (cfg.HardcoreOnlyToast)
                {
                    Services.RaLog.Write($"[Phase6b.2] HardcoreOnlyToast=true; suppressing friend LB toast (mode per submission not exposed by RA web API)");
                    return;
                }

                string? myUser = null;
                try { myUser = App.Configuration?.GetRetroAchievementsConfiguration()?.Username; }
                catch { }
                if (string.IsNullOrWhiteSpace(myUser)) return;

                // Use cached my-ranks if fresh (30min TTL). Without
                // caching, 20 friends improving on the same game in one
                // poll cycle would issue 20 identical HTTP calls.
                Dictionary<int, int>? myRankByLb = null;
                if (_myLbRanksByGame.TryGetValue(ev.GameId, out var cached)
                    && (DateTime.UtcNow - cached.fetchedUtc) < TimeSpan.FromMinutes(30))
                {
                    myRankByLb = cached.ranks;
                }
                if (myRankByLb == null)
                {
                    var raSvc = new Services.RetroAchievementsService(App.Configuration!, _db!);
                    try
                    {
                        var myBoards = await raSvc.GetUserGameLeaderboardsAsync(myUser, ev.GameId).ConfigureAwait(true);
                        myRankByLb = new Dictionary<int, int>(myBoards.Count);
                        foreach (var b in myBoards)
                        {
                            if (b.UserEntry != null && b.UserEntry.Rank > 0)
                                myRankByLb[b.Id] = b.UserEntry.Rank;
                        }
                        _myLbRanksByGame[ev.GameId] = (myRankByLb, DateTime.UtcNow);
                    }
                    catch (Exception fetchEx)
                    {
                        Services.RaLog.Write($"[Phase6b.2] my LB fetch failed game={ev.GameId}: {fetchEx.Message}");
                        return;
                    }
                }

                // For each improvement: did the friend's new rank cross
                // mine? Triggers when their new rank <= my rank AND
                // their old rank > my rank (or they had no prior entry).
                var passes = new List<(string lbTitle, int newRank, int myRank)>();
                foreach (var imp in ev.Improvements)
                {
                    if (!myRankByLb.TryGetValue(imp.LeaderboardId, out int myRank)) continue;
                    // Friend now at or above my rank, was below before.
                    if (imp.NewRank <= myRank && imp.OldRank > myRank)
                    {
                        // Use the LB id as a fallback title for now — the
                        // polling endpoint doesn't include LB titles
                        // per-entry. Phase 6b.3 polish: fetch
                        // GetGameLeaderboards once per game and cache
                        // the title map.
                        passes.Add(($"LB #{imp.LeaderboardId}", imp.NewRank, myRank));
                    }
                }

                if (passes.Count == 0) return;
                var top = passes[0];
                Services.RaLog.Write($"[LbToast] FRIEND BEAT ME friend={ev.FriendUsername} lb={top.lbTitle} their=#{top.newRank} mine=#{top.myRank} (and {passes.Count - 1} other(s))");

                // Surface as a toast in the main toast stack (Phase 4
                // surface). Click would deep-link to the friend's LB
                // tab — see ShowFriendLbToast.
                ShowFriendLbBeatYouToast(ev.FriendUserId, ev.FriendUsername, top.lbTitle, ev.GameId, passes.Count);

                // Sound cooldown: a single chime per N seconds across all
                // friends. Prevents the 20-friends-improving-at-once
                // pile-up. Visual toasts are NOT cooldown-gated here —
                // they get capped by the toast stack's 4-visible limit.
                if (cfg.LbToastSoundEnabled
                    && (DateTime.UtcNow - _lastFriendBeatSoundUtc).TotalSeconds >= cfg.LbToastCooldownSec)
                {
                    Services.FriendNotificationSound.Play(App.Configuration);
                    _lastFriendBeatSoundUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Services.RaLog.Write($"[Phase6b.2] HandleFriendLbImprovedOnUi EX: {ex.GetType().Name} {ex.Message}");
            }
        }

        private void ShowFriendLbBeatYouToast(int friendUserId, string friendName, string lbTitle, int raGameId, int passCount)
        {
            var toast = new Border
            {
                Background = (Brush)RaRes("BgSecondaryBrush"),
                BorderBrush = (Brush)RaRes("AccentBrush"),
                BorderThickness = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                MinWidth = 280,
                MaxWidth = 360,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Tag = (friendUserId, raGameId),
            };

            var stack = new StackPanel();
            var headline = new TextBlock
            {
                FontFamily = (FontFamily)RaRes("PrimaryFont"),
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = (Brush)RaRes("TextPrimaryBrush"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            headline.Inlines!.Add(new Avalonia.Controls.Documents.Run(friendName) { FontWeight = Avalonia.Media.FontWeight.Bold });
            headline.Inlines.Add($" just beat your score on {lbTitle}");
            stack.Children.Add(headline);
            if (passCount > 1)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"+{passCount - 1} other leaderboard{(passCount > 2 ? "s" : "")} as well",
                    FontFamily = (FontFamily)RaRes("PrimaryFont"),
                    FontSize = 10,
                    Foreground = (Brush)RaRes("TextMutedBrush"),
                    Margin = new Thickness(0, 3, 0, 0),
                });
            }
            toast.Child = stack;

            toast.PointerReleased += (s, e) =>
            {
                if (s is Border b && b.Tag is ValueTuple<int, int> tag)
                {
                    OpenFriendDetail(tag.Item1);
                    // Phase 5/6 already set up the LB tab + game picker.
                    // Pre-selection requires NavigateToLeaderboard which
                    // is the deferred Phase 6b polish — for now opening
                    // the detail window is the click destination.
                }
                ToastStack.Children.Remove(toast);
                e.Handled = true;
            };

            ToastStack.Children.Insert(0, toast);
            while (ToastStack.Children.Count > 4)
                ToastStack.Children.RemoveAt(ToastStack.Children.Count - 1);

            // 12s — longer than achievement toasts because LB events
            // carry more weight and the user might be mid-game.
            var dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            dismissTimer.Tick += (_, __) =>
            {
                dismissTimer.Stop();
                try { ToastStack.Children.Remove(toast); } catch { }
            };
            dismissTimer.Start();
        }

        private void OnFriendActivity(object? sender, FriendActivityEntry entry)
        {
            // Refresh the in-tab activity feed. Live per-unlock toasts were
            // removed deliberately — at scale (10+ active friends) they
            // turned into a constant interruption. The Friends sub-tab
            // surfaces the same data on demand. Leaderboard toasts
            // (triumph / proximity / beaten) and YOUR OWN achievement
            // unlocks still fire as toasts; only the friend-unlock toast
            // is gone.
            OnFriendsChanged(sender, EventArgs.Empty);
        }

        private void RefreshFriendsActivity()
        {
            var svc = GetOrCreateFriendService();
            var activity = svc.RecentActivity;
            FriendsActivityItems.Items.Clear();

            if (activity.Count == 0)
            {
                FriendsActivityCard.IsVisible = false;
                return;
            }
            FriendsActivityCard.IsVisible = true;

            // Cap rendered rows at 8 — the feed itself caps at 100 but
            // we don't want to scroll the friends sub-tab past the
            // friends list itself. "View All" deferred to Phase 5.
            int max = Math.Min(activity.Count, 8);
            for (int i = 0; i < max; i++)
                FriendsActivityItems.Items.Add(BuildActivityRow(activity[i]));
        }

        private Control BuildActivityRow(FriendActivityEntry entry)
        {
            var border = new Border
            {
                Padding = new Thickness(16, 6, 16, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(4),
                Background = (Avalonia.Media.IBrush)RaRes("BgTertiaryBrush"),
                ClipToBounds = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (!string.IsNullOrEmpty(entry.Unlock.BadgeName))
            {
                var img = new Avalonia.Controls.Image
                {
                    Stretch = Avalonia.Media.Stretch.UniformToFill,
                };
                badge.Child = img;
                Emutastic.Services.FriendImageLoader.Load(
                    img,
                    $"https://media.retroachievements.org/Badge/{entry.Unlock.BadgeName}.png",
                    "activity-badge",
                    $"user={entry.FriendUsername} ach={entry.Unlock.AchievementId}");
            }
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var stack = new StackPanel { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var line = new TextBlock
            {
                FontFamily = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
                FontSize = 12,
                Foreground = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush"),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            // Action-oriented framing: "FriendX unlocked <Achievement>"
            // — name leads, achievement after. Hardcore marker prefixed.
            string hardcore = entry.Unlock.HardcoreMode != 0 ? "[HC] " : "";
            line.Inlines!.Add(new Avalonia.Controls.Documents.Run(entry.FriendUsername)
            {
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
            });
            line.Inlines.Add($" unlocked {hardcore}{entry.Unlock.Title}");
            stack.Children.Add(line);

            stack.Children.Add(new TextBlock
            {
                Text = $"{entry.Unlock.GameTitle} · {entry.Unlock.ConsoleName}",
                FontFamily = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
                FontSize = 11,
                Foreground = (Avalonia.Media.IBrush)RaRes("TextMutedBrush"),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            // Relative time (best-effort — RA returns UTC without Z suffix)
            string when = FormatRelativeTime(entry.Unlock.Date);
            var whenTb = new TextBlock
            {
                Text = when,
                FontFamily = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
                FontSize = 10,
                Foreground = (Avalonia.Media.IBrush)RaRes("TextMutedBrush"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(whenTb, 2);
            grid.Children.Add(whenTb);

            border.Child = grid;
            return border;
        }

        private static string FormatRelativeTime(string isoDate)
        {
            if (string.IsNullOrWhiteSpace(isoDate)) return "";
            // RA returns "yyyy-MM-dd HH:mm:ss" UTC without Z. Parse as
            // UTC explicitly to avoid local-time interpretation.
            if (!DateTime.TryParseExact(isoDate, "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
                return "";
            var delta = DateTime.UtcNow - dt;
            if (delta.TotalMinutes < 1) return "just now";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
            if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
            return dt.ToLocalTime().ToString("MMM d");
        }

        private void RefreshFriendsView()
        {
            var svc = GetOrCreateFriendService();
            var friends = svc.Friends;

            FriendsListItems.Items.Clear();
            FriendsHeaderLabel.Text = friends.Count == 0
                ? "FRIENDS"
                : $"FRIENDS ({friends.Count})";

            if (friends.Count == 0)
            {
                FriendsListEmpty.IsVisible = true;
                return;
            }
            FriendsListEmpty.IsVisible = false;

            foreach (var f in friends)
                FriendsListItems.Items.Add(BuildFriendRow(f, svc.GetSnapshot(f.UserId)));
        }

        private Control BuildFriendRow(FriendEntry entry, FriendCacheSnapshot? snap)
        {
            var border = new Border
            {
                BorderBrush = (Avalonia.Media.IBrush)RaRes("BorderSubtleBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 12, 14, 12),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Tag = entry.UserId,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Avatar + unseen pip
            var avatarBox = new Grid { Width = 48, Height = 48 };
            var avatarBorder = new Border
            {
                Width = 48, Height = 48,
                CornerRadius = new CornerRadius(24),
                Background = (Avalonia.Media.IBrush)RaRes("BgTertiaryBrush"),
                ClipToBounds = true,
            };
            if (!string.IsNullOrEmpty(snap?.AvatarUrl))
            {
                var img = new Avalonia.Controls.Image
                {
                    Stretch = Avalonia.Media.Stretch.UniformToFill,
                };
                avatarBorder.Child = img;
                Emutastic.Services.FriendImageLoader.Load(
                    img,
                    snap.AvatarUrl,
                    "list-avatar",
                    $"user={entry.Username}");
            }
            else
            {
                Emutastic.Services.RaLog.Write(
                    $"[FriendImg:list-avatar] no avatar URL user={entry.Username} snap={(snap == null ? "null" : "non-null, empty AvatarUrl")}");
            }
            avatarBox.Children.Add(avatarBorder);
            if (snap != null && snap.UnseenUnlockCount > 0)
            {
                var pip = new Border
                {
                    Width = 22, Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = (Avalonia.Media.IBrush)RaRes("AccentBrush"),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                };
                pip.Child = new TextBlock
                {
                    Text = snap.UnseenUnlockCount > 99 ? "99+" : snap.UnseenUnlockCount.ToString(),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Avalonia.Media.Brushes.White,
                    FontSize = 10,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                };
                avatarBox.Children.Add(pip);
            }
            Grid.SetColumn(avatarBox, 0);
            grid.Children.Add(avatarBox);

            // Name + secondary line
            var stack = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = entry.Username,
                FontFamily = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
                FontSize = 14,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush"),
            });
            string secondary;
            if (entry.IsInvalid) secondary = "Account unavailable";
            else if (entry.IsPrivate) secondary = "Profile is private";
            else if (snap == null) secondary = "Loading…";
            else secondary = $"{snap.PointsHardcore:N0} pts · {snap.PointsSoftcore:N0} softcore";
            stack.Children.Add(new TextBlock
            {
                Text = secondary,
                FontFamily = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
                FontSize = 11,
                Foreground = (Avalonia.Media.IBrush)RaRes("TextMutedBrush"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            // Remove button (compact, right-aligned)
            var removeBtn = new Button
            {
                Content = "Remove",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
                Tag = entry.UserId,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            removeBtn.Click += FriendRowRemove_Click;
            Grid.SetColumn(removeBtn, 2);
            grid.Children.Add(removeBtn);

            border.Child = grid;
            border.PointerReleased += FriendRow_MouseLeftButtonUp;
            return border;
        }

        private void FriendRow_MouseLeftButtonUp(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
        {
            if (sender is Border b && b.Tag is int userId)
            {
                // Mark seen first so the pip clears whether the user
                // opens the brief card or it auto-dismisses.
                var svc = GetOrCreateFriendService();
                svc.MarkSeen(userId);

                var entry = svc.Friends.FirstOrDefault(f => f.UserId == userId);
                if (entry == null) return;

                // Close any prior brief card; show one tied to this row.
                _openFriendBrief?.CloseBrief();
                var snap = svc.GetSnapshot(userId);
                var brief = new Views.FriendBriefCard(entry, snap, svc);
                brief.OpenProfileRequested += (_, uid) => OpenFriendDetail(uid);
                brief.RemoveRequested += async (_, uid) =>
                {
                    try { await svc.RemoveAsync(uid).ConfigureAwait(true); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Friends] remove from brief failed: {ex.Message}"); }
                };
                // Null the field on close so a later OnPreviewMouseDown
                // doesn't call CloseBrief on a disposed window. WPF
                // tolerates the second Close but the field would point
                // at stale state until the next click.
                brief.Closed += (_, __) =>
                {
                    if (ReferenceEquals(_openFriendBrief, brief)) _openFriendBrief = null;
                };
                _openFriendBrief = brief;
                // Position the popup near the clicked row.
                var point = b.PointToScreen(new Avalonia.Point(0, b.Bounds.Height + 4));
                brief.Position = new Avalonia.PixelPoint(point.X, point.Y);
                brief.Show(this);

                e.Handled = true; // prevent immediate dismiss by OnPreviewMouseDown bubble
            }
        }

        private void OpenFriendDetail(int userId)
        {
            var svc = GetOrCreateFriendService();
            var entry = svc.Friends.FirstOrDefault(f => f.UserId == userId);
            if (entry == null) return;

            if (_friendDetailWindows.TryGetValue(userId, out var existing))
            {
                // Focus existing rather than opening a duplicate.
                try
                {
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    existing.Focus();
                    return;
                }
                catch { /* fall through and reopen */ }
            }

            var window = new Views.FriendDetailWindow(
                entry,
                svc,
                new RetroAchievementsService(App.Configuration!, _db!),
                _db!);
            window.Closed += (_, __) =>
            {
                _friendDetailWindows.Remove(userId);
            };
            _friendDetailWindows[userId] = window;
            // Owned by the main window so it closes with the app — unowned, it outlived a main-
            // window close (ShutdownMode is last-window-close) and, with the ✕ unwired, could
            // only be killed from the taskbar.
            window.Show(this);
        }

        private async void FriendRowRemove_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Button's default template consumes MouseLeftButtonUp via its
            // own ClickMode=Release routing; e.Handled here is
            // belt-and-suspenders so the row's MouseLeftButtonUp won't
            // also fire if the template ever changes.
            e.Handled = true;
            if (sender is Button btn && btn.Tag is int userId)
            {
                try
                {
                    var svc = GetOrCreateFriendService();
                    await svc.RemoveAsync(userId).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[Friends] remove failed: {ex.Message}");
                    // FriendListChanged didn't fire; manually refresh so
                    // the row doesn't disappear if the remove succeeded
                    // partway through. RefreshFriendsView is idempotent.
                    RefreshFriendsView();
                }
            }
        }

        private void FriendsAddButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var dialog = new Views.AddFriendDialog
            {
                FriendService = GetOrCreateFriendService(),
            };
            // ShowDialog is synchronous; AddAsync fires FriendListChanged
            // on success which triggers RefreshFriendsView via
            // OnFriendsChanged. No explicit refresh needed.
            _ = dialog.ShowDialog<bool>(this);
        }

        // Reentrancy gate for the Import button: PopulateAchievementsView
        // unconditionally re-enables FriendsImportButton, so a tab-flip
        // mid-import could otherwise let the user fire two parallel
        // ApplyFollowSyncAsync passes. Set on entry, cleared in finally.
        private bool _friendsImportInFlight;

        // Followers disclosure state.
        private bool _followersExpanded;
        private bool _followersInFlight;

        /// <summary>
        /// Pulls the user's RetroAchievements follow list and reconciles it
        /// against the local friends list. New entries are added muted —
        /// the per-friend bell on each card toggles toasts back on.
        /// Existing friends gain MutualFollow + Ulid backfill without
        /// losing their notification preference.
        /// </summary>
        private async void FriendsImportButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (FriendsImportButton == null) return;
            if (App.Configuration == null) return; // RA never initializes without config
            if (_friendsImportInFlight) return;
            _friendsImportInFlight = true;
            FriendsImportButton.IsEnabled = false;
            try
            {
                _vm?.SetStatus("Importing follows from RetroAchievements…");
                var api = new Services.RetroAchievementsService(App.Configuration!, _db!);
                var followed = await Task.Run(() => api.GetUsersIFollowAsync())
                                         .ConfigureAwait(true);
                if (followed == null || followed.Count == 0)
                {
                    // Distinguish "API key/username missing" (silent fallback
                    // returns empty list) from "RA says you have no follows."
                    var ra = GetOrCreateRaDataService();
                    string msg = (string.IsNullOrWhiteSpace(ra.CurrentUser()) || !ra.HasApiKey())
                        ? "RetroAchievements isn't configured — add credentials in Preferences."
                        : "RetroAchievements returned no follows.";
                    _vm?.SetStatus(msg, autoClear: true);
                    return;
                }

                var friends = GetOrCreateFriendService();
                var result = await Task.Run(() => friends.ApplyFollowSyncAsync(followed))
                                       .ConfigureAwait(true);

                var parts = new List<string>();
                if (result.Added         > 0) parts.Add($"{result.Added} new");
                if (result.Updated       > 0) parts.Add($"{result.Updated} updated");
                if (result.MutualCleared > 0) parts.Add($"{result.MutualCleared} mutual flag(s) cleared");
                if (result.Failed        > 0) parts.Add($"{result.Failed} skipped");
                string summary = parts.Count == 0
                    ? "Already in sync with RetroAchievements."
                    : "Import complete — " + string.Join(" · ", parts) +
                      (result.Added > 0 ? "  (new entries are muted — tap the bell to enable toasts)" : "");
                _vm?.SetStatus(summary, autoClear: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[FriendsImport] failed: {ex.Message}");
                _vm?.SetStatus("Import failed — see logs.", autoClear: true);
            }
            finally
            {
                _friendsImportInFlight = false;
                if (FriendsImportButton != null)
                    FriendsImportButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Toggles the Followers disclosure. First expand fetches the list
        /// from RA via the public web API (cached 10 min server-side per
        /// session); subsequent expands re-render from cache without
        /// re-hitting the network.
        /// </summary>
        private async void FollowersHeader_Click(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            _followersExpanded = !_followersExpanded;
            FollowersContent.IsVisible = _followersExpanded ? true : false;
            FollowersHeaderCaret.Text = _followersExpanded ? "▴" : "▾";

            // Only fetch on expand. Cheap idempotent re-render on collapse-expand
            // pairs via the service's 10-min cache.
            if (!_followersExpanded) return;
            if (_followersInFlight) return;
            if (App.Configuration == null) return;
            // Skip rebuild if we already have populated rows — collapsing
            // then re-expanding shouldn't re-flicker the avatars. (Cache
            // would return the same payload within the 10-min TTL window
            // anyway; this avoids the WPF Items.Clear + N image-bindings.)
            if (FollowersListItems.Items.Count > 0) return;

            _followersInFlight = true;
            FollowersStatus.IsVisible = true;
            FollowersStatus.Text = "Loading followers…";
            FollowersListItems.Items.Clear();
            try
            {
                var api = new Services.RetroAchievementsService(App.Configuration!, _db!);
                var followers = await Task.Run(() => api.GetUsersFollowingMeAsync())
                                          .ConfigureAwait(true);

                // Filter out anyone already in the friends list — they
                // can't be re-added, so showing them serves no purpose.
                var friendUsernames = new HashSet<string>(
                    GetOrCreateFriendService().Friends.Select(f => f.Username),
                    StringComparer.OrdinalIgnoreCase);
                var notYetFriends = followers
                    .Where(f => !friendUsernames.Contains(f.User))
                    .ToList();

                FollowersHeaderLabel.Text = followers.Count > 0
                    ? $"YOUR FOLLOWERS ({notYetFriends.Count} not yet friends)"
                    : "YOUR FOLLOWERS";

                if (notYetFriends.Count == 0)
                {
                    FollowersStatus.Text = followers.Count == 0
                        ? "No one follows you on RetroAchievements yet."
                        : "All your followers are already in your friends list.";
                    return;
                }

                FollowersStatus.IsVisible = false;
                foreach (var f in notYetFriends)
                    FollowersListItems.Items.Add(BuildFollowerRow(f));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Followers] populate failed: {ex.Message}");
                FollowersStatus.Text = "Couldn't load followers — check your internet connection.";
            }
            finally
            {
                _followersInFlight = false;
            }
        }

        /// <summary>
        /// Builds one row in the Followers list — avatar + username +
        /// "Add as Friend" button. Avatar URL is derived directly from
        /// the username (RA's CDN convention) so no extra API call.
        /// </summary>
        private Control BuildFollowerRow(Models.RAUsersFollowingMeEntry follower)
        {
            var border = new Border
            {
                BorderBrush = (Avalonia.Media.IBrush)RaRes("BorderSubtleBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 10, 14, 10),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new Border
            {
                Width = 32, Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = (Avalonia.Media.IBrush)RaRes("BgTertiaryBrush"),
                ClipToBounds = true,
            };
            var img = new Avalonia.Controls.Image
            {
                Stretch = Avalonia.Media.Stretch.UniformToFill,
            };
            avatar.Child = img;
            // RA's UserPic CDN convention — derivable from username, no API call.
            string avatarUrl = $"https://media.retroachievements.org/UserPic/{Uri.EscapeDataString(follower.User)}.png";
            Emutastic.Services.FriendImageLoader.Load(img, avatarUrl, "follower-avatar", $"user={follower.User}");
            Grid.SetColumn(avatar, 0);

            var nameStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10, 0, 8, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            nameStack.Children.Add(new TextBlock
            {
                Text = follower.User,
                FontFamily = (Avalonia.Media.FontFamily)RaRes("PrimaryFont"),
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush"),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(nameStack, 1);

            var addBtn = new Button
            {
                Content = "Add as Friend",
                Theme = (Avalonia.Styling.ControlTheme)RaRes("PrefActionBtn"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Tag = follower.User,
            };
            addBtn.Click += FollowerAddBtn_Click;
            Grid.SetColumn(addBtn, 2);

            grid.Children.Add(avatar);
            grid.Children.Add(nameStack);
            grid.Children.Add(addBtn);
            border.Child = grid;
            return border;
        }

        private async void FollowerAddBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not string username || string.IsNullOrWhiteSpace(username)) return;

            btn.IsEnabled = false;
            btn.Content = "Adding…";
            try
            {
                var svc = GetOrCreateFriendService();
                var preview = await Task.Run(() => svc.LookupAsync(username)).ConfigureAwait(true);
                if (!preview.Success)
                {
                    btn.Content = "Failed";
                    System.Diagnostics.Trace.WriteLine($"[FollowerAdd] LookupAsync failed for {username}: {preview.Error}");
                    return;
                }
                bool added = await Task.Run(() => svc.AddAsync(preview)).ConfigureAwait(true);
                btn.Content = added ? "Added" : "Already added";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[FollowerAdd] failed for {username}: {ex.Message}");
                btn.Content = "Failed";
            }
        }

        private void RenderCommunityPulse(List<Models.RARecentGameAward>? awards)
        {
            RACommunityPulseItems.Items.Clear();
            if (awards == null || awards.Count == 0)
            {
                RACommunityPulseEmpty.IsVisible = true;
                return;
            }
            RACommunityPulseEmpty.IsVisible = false;

            var font = (Avalonia.Media.FontFamily)RaRes("PrimaryFont");
            var textPrimary = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush");
            var textMuted = (Avalonia.Media.IBrush)RaRes("TextMutedBrush");

            int shown = 0;
            foreach (var a in awards)
            {
                RACommunityPulseItems.Items.Add(BuildCommunityPulseRow(a, font, textPrimary, textMuted));
                if (++shown >= 12) break;
            }
        }

        private static Control BuildCommunityPulseRow(Models.RARecentGameAward a,
            Avalonia.Media.FontFamily font,
            Avalonia.Media.IBrush textPrimary,
            Avalonia.Media.IBrush textMuted)
        {
            // Three columns: small award-kind dot · user/game line · time-ago.
            var row = new Grid { Margin = new Thickness(16, 4, 16, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Award-kind color dot — mirrors the trophy-case ring scheme.
            var ringColor = a.AwardKind switch
            {
                "mastered" => _ringMastery,
                "completed" => _ringCompletion,
                "beaten-hardcore" => _ringBeatenHardcore,
                _ => _ringBeatenSoftcore,
            };
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = ringColor,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            // User · award · game · console (one line, ellipsis if narrow).
            var line = new TextBlock
            {
                FontFamily = font,
                FontSize = 11,
                Foreground = textPrimary,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            line.Inlines!.Add(new Avalonia.Controls.Documents.Run(a.User) { FontWeight = Avalonia.Media.FontWeight.SemiBold });
            line.Inlines.Add($" {AwardKindLabel(a.AwardKind)} ");
            line.Inlines.Add(new Avalonia.Controls.Documents.Run(a.GameTitle) { FontWeight = Avalonia.Media.FontWeight.SemiBold });
            Grid.SetColumn(line, 1);
            row.Children.Add(line);

            // Time-ago
            var ago = new TextBlock
            {
                Text = FormatTimeAgo(a.AwardDate),
                FontFamily = font,
                FontSize = 10,
                Foreground = textMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(ago, 2);
            row.Children.Add(ago);

            ToolTip.SetTip(row, $"{a.GameTitle} · {a.ConsoleName}");
            return row;
        }

        private static string AwardKindLabel(string kind) => kind switch
        {
            "mastered"         => "mastered",
            "completed"        => "completed",
            "beaten-hardcore"  => "beat (hc)",
            "beaten-softcore"  => "beat",
            _                  => "earned",   // unknown future kinds fall back to a generic verb
        };

        private void RenderTopTen(List<RaDataService.TopTenEntry>? top)
        {
            RATopTenItems.Items.Clear();
            if (top == null || top.Count == 0)
            {
                RATopTenEmpty.IsVisible = true;
                return;
            }
            RATopTenEmpty.IsVisible = false;

            var font = (Avalonia.Media.FontFamily)RaRes("PrimaryFont");
            var textPrimary = (Avalonia.Media.IBrush)RaRes("TextPrimaryBrush");
            var textMuted = (Avalonia.Media.IBrush)RaRes("TextMutedBrush");
            var textSecondary = (Avalonia.Media.IBrush)RaRes("TextSecondaryBrush");

            int rank = 0;
            foreach (var t in top)
            {
                rank++;
                var row = new Grid { Margin = new Thickness(16, 3, 16, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

                var rankCell = new TextBlock
                {
                    Text = rank.ToString(),
                    FontFamily = font,
                    FontSize = 11,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Foreground = textMuted,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                Grid.SetColumn(rankCell, 0);
                row.Children.Add(rankCell);

                var userCell = new TextBlock
                {
                    Text = t.User,
                    FontFamily = font,
                    FontSize = 12,
                    Foreground = textPrimary,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                Grid.SetColumn(userCell, 1);
                row.Children.Add(userCell);

                var ptsCell = new TextBlock
                {
                    Text = t.Points.ToString("N0"),
                    FontFamily = font,
                    FontSize = 11,
                    Foreground = textSecondary,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                };
                Grid.SetColumn(ptsCell, 2);
                row.Children.Add(ptsCell);
                RATopTenItems.Items.Add(row);
            }
        }

        // ── Heatmap ───────────────────────────────────────────────────────
        // Five-stop intensity ramp from BgTertiary (no activity) up through
        // increasing red tint. Frozen so we can hand the same brush to many
        // cells without per-cell allocations.
        private static readonly Avalonia.Media.IBrush[] _heatmapStops = BuildHeatmapStops();

        private static Avalonia.Media.IBrush[] BuildHeatmapStops()
        {
            // 0 = empty (dark gray, BgTertiary), 1..4 ramp toward accent red.
            // Accent is #E03535 per the project's accent color memory; we
            // step from a muted tone to full saturation.
            var stops = new[]
            {
                Avalonia.Media.Color.FromRgb(0x2A, 0x2A, 0x2D),   // 0 unlocks
                Avalonia.Media.Color.FromRgb(0x5A, 0x29, 0x29),   // 1
                Avalonia.Media.Color.FromRgb(0x8C, 0x2A, 0x2A),   // 2-3
                Avalonia.Media.Color.FromRgb(0xB7, 0x2F, 0x2F),   // 4-7
                Avalonia.Media.Color.FromRgb(0xE0, 0x35, 0x35),   // 8+
            };
            return stops.Select(c =>
            {
                var b = new Avalonia.Media.SolidColorBrush(c);
                return (Avalonia.Media.IBrush)b;
            }).ToArray();
        }

        private static int HeatmapBucket(int count)
        {
            if (count <= 0) return 0;
            if (count <= 1) return 1;
            if (count <= 3) return 2;
            if (count <= 7) return 3;
            return 4;
        }

        private void RenderHeatmap(Dictionary<string, int>? counts)
        {
            // Note on time zones: the grid bins by UTC date because RA's
            // unlock timestamps are UTC. A user playing at, say, 23:30 PST
            // will see that unlock on the next UTC day's cell — a small
            // edge-of-day misalignment vs their local calendar. Tradeoff
            // accepted because matching local-date semantics would require
            // converting every RA timestamp during aggregation and shifting
            // the range bounds, both of which complicate the API contract
            // for marginal UX gain on a 90-day overview chart.
            RAHeatmapGrid.Children.Clear();
            counts ??= new Dictionary<string, int>();

            const int Days = 90;
            var endUtc = DateTime.UtcNow.Date;
            var startUtc = endUtc.AddDays(-(Days - 1));

            // Column count has to account for the leading partial week —
            // the renderer offsets each cell's date by (weekday - startDoW),
            // so the first column holds the Sunday on-or-before startUtc.
            // Without `+startDoW` in the ceiling, weekday=0 of the trailing
            // partial week falls off the right edge and today's cell goes
            // missing (regression caught by audit).
            int startDoW = (int)startUtc.DayOfWeek;
            int cols = (int)Math.Ceiling((Days + startDoW) / 7.0);
            RAHeatmapGrid.Columns = cols;

            // Build cells row-major-by-weekday (Sun..Sat) so the grid reads
            // like the GitHub heatmap. We iterate weekdays 0..6 across the
            // outer loop and weeks 0..cols-1 across the inner; each cell's
            // actual date is startUtc + (week * 7) + weekday. If that date
            // is outside the 90-day window (overflow on the right edge),
            // render an invisible filler.
            int total = 0;
            for (int weekday = 0; weekday < 7; weekday++)
            {
                for (int week = 0; week < cols; week++)
                {
                    var date = startUtc.AddDays(week * 7 + (weekday - (int)startUtc.DayOfWeek));
                    if (date < startUtc || date > endUtc)
                    {
                        RAHeatmapGrid.Children.Add(new Border
                        {
                            Width = 14, Height = 14,
                            Margin = new Thickness(2),
                            Background = Avalonia.Media.Brushes.Transparent,
                        });
                        continue;
                    }
                    string iso = date.ToString("yyyy-MM-dd");
                    int count = counts.TryGetValue(iso, out var c) ? c : 0;
                    total += count;
                    var cell = new Border
                    {
                        Width = 14, Height = 14,
                        Margin = new Thickness(2),
                        CornerRadius = new CornerRadius(3),
                        Background = _heatmapStops[HeatmapBucket(count)],
                    };
                    ToolTip.SetTip(cell, count == 0
                        ? $"No unlocks · {date:MMM d, yyyy}"
                        : $"{count} unlock{(count == 1 ? "" : "s")} · {date:MMM d, yyyy}");
                    RAHeatmapGrid.Children.Add(cell);
                }
            }

            RAHeatmapCaption.Text = $"Last 90 days · {startUtc:MMM d} → {endUtc:MMM d}";
            RAHeatmapTotal.Text = total == 1
                ? "1 achievement unlocked in this window"
                : $"{total:N0} achievements unlocked in this window";

            // Legend: five small squares mirroring the bucket ramp.
            // Built once and reused — same five static stops each render.
            if (RAHeatmapLegend.Children.Count == 0)
            {
                foreach (var brush in _heatmapStops)
                {
                    RAHeatmapLegend.Children.Add(new Border
                    {
                        Width = 12, Height = 12,
                        Margin = new Thickness(2, 0, 2, 0),
                        CornerRadius = new CornerRadius(2),
                        Background = brush,
                    });
                }
            }
        }

        private static string FormatDurationShort(int sec)
        {
            if (sec <= 0) return "—";
            if (sec < 60) return $"{sec}s";
            if (sec < 3600) return $"{sec / 60}m";
            double h = sec / 3600.0;
            return h < 100 ? $"{h:0.#}h" : $"{(int)h}h";
        }

        private static string FormatTimeAgo(string isoDate)
        {
            if (string.IsNullOrWhiteSpace(isoDate)) return "";
            // RA returns "yyyy-MM-dd HH:mm:ss" without timezone — DateTime.TryParse
            // leaves Kind=Unspecified, which ToUniversalTime() then treats as
            // local time and offsets by the user's TZ (wrong; the value is
            // UTC server time). AssumeUniversal+AdjustToUniversal gives us a
            // correctly-tagged UTC DateTime.
            if (!DateTime.TryParse(isoDate, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var t))
                return "";
            var diff = DateTime.UtcNow - t;
            if (diff.TotalMinutes < 1)  return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours   < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays}d ago";
            return t.ToLocalTime().ToString("MMM d, yyyy");
        }


}
