using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emutastic.Configuration;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// RetroAchievements Web API client (port of upstream's
    /// RetroAchievementsService — detail-card subset). Fetches community
    /// progression stats and per-user unlock state for the game detail card,
    /// and validates credentials for the settings page. The remaining
    /// Achievements-tab endpoints (profiles, awards, leaderboards, follows —
    /// ~20 more) land with the A8f friends/tab phase.
    ///
    /// Auth model: the Web API key (settings → Web API Key) authenticates
    /// STATS fetches; the rcheevos token authenticates UNLOCKS. Independent —
    /// a user can be logged in for unlocks with no API key (card shows no data).
    /// </summary>
    public class RetroAchievementsService
    {
        // Single shared HttpClient per .NET guidance. 15s is generous; the API
        // is normally <500ms but degrades during nightly DB regenerations.
        private static readonly HttpClient _http = BuildHttp();

        private static HttpClient BuildHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(EmutasticUserAgent.Build());
            return http;
        }

        // Cap concurrent Web API calls at 2 to stay polite — the API host is
        // a community service, not a CDN.
        private static readonly SemaphoreSlim _throttle = new(2, 2);

        // Minimum gap between request *starts* — the concurrency cap alone
        // lets sub-100ms responses machine-gun the API and trip the burst
        // limiter. Cache hits never enter GetJsonAsync, so warm visits stay
        // instant.
        private const int MinRequestGapMs = 350;
        private static readonly SemaphoreSlim _paceGate = new(1, 1);
        private static DateTimeOffset _lastRequestStartUtc = DateTimeOffset.MinValue;

        private static async Task EnterRequestSlotAsync(CancellationToken ct)
        {
            await _paceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var sinceLast = (DateTimeOffset.UtcNow - _lastRequestStartUtc).TotalMilliseconds;
                if (sinceLast < MinRequestGapMs)
                    await Task.Delay((int)(MinRequestGapMs - sinceLast), ct).ConfigureAwait(false);
                _lastRequestStartUtc = DateTimeOffset.UtcNow;
            }
            finally { _paceGate.Release(); }

            await _throttle.WaitAsync(ct).ConfigureAwait(false);
        }

        private static void LeaveRequestSlot()
        {
            try { _throttle.Release(); } catch { }
        }

        private const string ApiBase = "https://retroachievements.org/API";

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly IConfigurationService? _config;
        private readonly DatabaseService? _db;

        public RetroAchievementsService() { }
        public RetroAchievementsService(IConfigurationService config) { _config = config; }
        public RetroAchievementsService(IConfigurationService config, DatabaseService db)
        { _config = config; _db = db; }

        // TTLs for the two cached responses: community medians shift slowly;
        // per-user state changes every play session.
        public static readonly TimeSpan ProgressionTtl  = TimeSpan.FromHours(24);
        public static readonly TimeSpan UserProgressTtl = TimeSpan.FromHours(1);

        private string? GetApiKey() => _config?.GetRetroAchievementsConfiguration()?.ApiKey;

        /// <summary>
        /// Validates credentials by attempting a password login via rcheevos.
        /// Returns (null, token) on success, or (error, null) on failure.
        /// </summary>
        public Task<(string? error, string? token)> TestLoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                return Task.FromResult<(string?, string?)>(("Username is required.", null));
            if (string.IsNullOrWhiteSpace(password))
                return Task.FromResult<(string?, string?)>(("Password is required.", null));

            return Task.Run<(string?, string?)>(() =>
            {
                RetroAchievementsClient? client = null;
                try
                {
                    client = new RetroAchievementsClient();
                    client.Initialize(null, false);
                    var (ok, err, token) = client.LoginWithPassword(username, password);
                    return ok ? (null, token) : (err ?? "Login failed.", null);
                }
                catch (Exception ex)
                {
                    return ($"Error: {ex.Message}", null);
                }
                finally
                {
                    try { client?.Dispose(); } catch { }
                }
            });
        }

        /// <summary>
        /// Refreshes the detail card's two cached responses when stale.
        /// No-op without an API key / RAGameId. Writes both the DB columns
        /// and the live Game object so the card can re-render immediately.
        /// </summary>
        public async Task RefreshDetailForGameAsync(Game game, CancellationToken ct = default)
        {
            if (game == null || _db == null || game.RAGameId <= 0) return;
            var ra = _config?.GetRetroAchievementsConfiguration();
            if (ra == null || string.IsNullOrWhiteSpace(ra.ApiKey)) return;

            // Game-wide progression (no user needed).
            if (game.IsRAProgressionStale(ProgressionTtl))
            {
                var prog = await GetGameProgressionAsync(game.RAGameId, ct).ConfigureAwait(false);
                if (prog != null)
                {
                    string json = JsonSerializer.Serialize(prog);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    try { _db.UpdateRAProgression(game.Id, json, ts); }
                    catch (Exception ex) { Trace.WriteLine($"[RA] persist progression failed: {ex.Message}"); }
                    game.RAProgressionJson = json;
                    game.RAProgressionFetchedAt = ts;
                }
            }

            // Per-user (only if logged in).
            if (!string.IsNullOrWhiteSpace(ra.Username) && game.IsRAUserProgressStale(UserProgressTtl))
            {
                var user = await GetGameInfoAndUserProgressAsync(game.RAGameId, ra.Username, ct)
                    .ConfigureAwait(false);
                if (user != null)
                {
                    string json = JsonSerializer.Serialize(user);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    try { _db.UpdateRAUserProgress(game.Id, json, ts); }
                    catch (Exception ex) { Trace.WriteLine($"[RA] persist user progress failed: {ex.Message}"); }
                    game.RAUserProgressJson = json;
                    game.RAUserProgressFetchedAt = ts;
                }
            }
        }

        /// <summary>
        /// Marks the per-user cache stale so the next detail-card open
        /// refetches. Cheap — DB-only, no network. Call after the user exits
        /// a game so freshly-unlocked achievements show up next peek.
        /// </summary>
        public void InvalidateUserProgressForGame(Game game)
        {
            if (game == null || _db == null || game.RAGameId <= 0) return;
            try { _db.UpdateRAUserProgress(game.Id, "", 0L); }
            catch (Exception ex) { Trace.WriteLine($"[RA] invalidate failed: {ex.Message}"); }
            game.RAUserProgressJson = "";
            game.RAUserProgressFetchedAt = 0L;
        }

        /// <summary>
        /// Game-wide progression stats — community medians for time to beat /
        /// complete / master, plus per-achievement metadata. No user context.
        /// Null on any failure (missing API key, network, parse); never throws.
        /// </summary>
        public Task<RAProgression?> GetGameProgressionAsync(int raGameId, CancellationToken ct = default)
        {
            if (raGameId <= 0) return Task.FromResult<RAProgression?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAProgression?>(null);

            string url = $"{ApiBase}/API_GetGameProgression.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&i={raGameId}";
            return GetJsonAsync<RAProgression>(url, "GetGameProgression", ct);
        }

        /// <summary>
        /// The given user's per-achievement unlock state for a single game
        /// (DateEarned populated only for earned achievements). Null on failure.
        /// </summary>
        public Task<RAUserProgress?> GetGameInfoAndUserProgressAsync(
            int raGameId, string username, CancellationToken ct = default)
        {
            if (raGameId <= 0 || string.IsNullOrWhiteSpace(username))
                return Task.FromResult<RAUserProgress?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAUserProgress?>(null);

            string url = $"{ApiBase}/API_GetGameInfoAndUserProgress.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&u={Uri.EscapeDataString(username)}"
                       + $"&g={raGameId}";
            return GetJsonAsync<RAUserProgress>(url, "GetGameInfoAndUserProgress", ct);
        }

        private async Task<T?> GetJsonAsync<T>(string url, string opName, CancellationToken ct)
            where T : class
        {
            // One retry on 429 — first hit honors Retry-After (or default 2s),
            // second hit gives up and returns null. Pacing should prevent most
            // 429s; this is the safety net for genuine bursts.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await EnterRequestSlotAsync(ct).ConfigureAwait(false);
                bool released = false;
                try
                {
                    using var resp = await _http.GetAsync(url,
                        HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                    if ((int)resp.StatusCode == 429 && attempt == 0)
                    {
                        int retryAfterMs = ComputeRetryAfterMs(resp);
                        RaLog.Write($"[RA pacing] {opName} hit 429, retry-after={retryAfterMs}ms");
                        // Release the concurrency slot during the wait so other
                        // queued requests aren't blocked by our cool-down.
                        LeaveRequestSlot();
                        released = true;
                        await Task.Delay(retryAfterMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        string body = "";
                        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                        if (body.Length > 240) body = body.Substring(0, 240);
                        Trace.WriteLine($"[RA] {opName} HTTP {(int)resp.StatusCode}");
                        RaLog.Write($"http error: op={opName} status={(int)resp.StatusCode} body={body}");
                        return null;
                    }
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    try { return JsonSerializer.Deserialize<T>(json, _jsonOpts); }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[RA] {opName} JSON parse failed: {ex.Message}");
                        string snippet = json ?? "";
                        if (snippet.Length > 240) snippet = snippet.Substring(0, 240);
                        RaLog.Write($"parse failed: op={opName} err={ex.Message} json={snippet}");
                        return null;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RA] {opName} failed: {ex.GetType().Name}: {ex.Message}");
                    RaLog.Write($"exception: op={opName} type={ex.GetType().Name} msg={ex.Message}");
                    return null;
                }
                finally
                {
                    if (!released) LeaveRequestSlot();
                }
            }
            return null;
        }

        private static int ComputeRetryAfterMs(HttpResponseMessage resp)
        {
            try
            {
                var ra = resp.Headers.RetryAfter;
                if (ra?.Delta is { } delta) return Math.Clamp((int)delta.TotalMilliseconds, 500, 30_000);
                if (ra?.Date is { } when) return Math.Clamp((int)(when - DateTimeOffset.UtcNow).TotalMilliseconds, 500, 30_000);
            }
            catch { }
            return 2_000;
        }
    }
}
