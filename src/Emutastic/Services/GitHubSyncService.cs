using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    /// <summary>
    /// GitHub Cloud Sync (port of upstream GitHubSyncService): battery saves, console
    /// save folders and library.db backed up through a private repository via the
    /// Contents API. OAuth device flow (no client secret), manifest-based
    /// last-write-wins, optional AES-256-GCM with a PBKDF2-derived key.
    ///
    /// Linux deltas from upstream:
    ///  - Every PC syncs to its own repository (emutastic-saves-&lt;machine&gt;). Upstream
    ///    shares one repository between PCs unless "Make this PC unique" is ticked; one
    ///    shared save set let a newer-but-behind save from one PC replace another PC's
    ///    progress, and let a new PC pull saves it never made.
    ///  - DPAPI (ProtectedData) doesn't exist here. The token and the encryption
    ///    passphrase live in the desktop keyring instead (Platform.SecretStore), and
    ///    config.json carries only <see cref="KeyringSentinel"/>. Without a keyring
    ///    they fall back to config.json, which is written owner-only.
    ///  - Local battery saves live at Saves/&lt;romstem&gt;.srm (the session's
    ///    RetroArch-style scheme), not upstream's BatterySaves/&lt;Console&gt;/ tree.
    ///    The REPO layout keeps upstream's hash-keyed convention so Windows and
    ///    Linux installs can share one repo.
    /// </summary>
    public sealed class GitHubSyncService
    {
        public static GitHubSyncService Instance { get; } = new();

        private const string SharedRepoName = "emutastic-saves";
        private static string ClientId => Emutastic.Secrets.GitHubOAuthClientId;

        /// <summary>GitHub REST root. Test-only override: the offline sync self-test points it at an
        /// in-process fake so a whole sync runs without an account or a network.</summary>
        internal static string ApiBase { get; set; } = "https://api.github.com";

        // Every PC syncs to its own repository. Sharing one save set between PCs meant a newer
        // save from one machine replaced another machine's further-along save, and a new PC
        // pulled saves it never made. RepoNameOverride exists for the self-tests only.
        private static string RepoName => RepoNameOverride ?? PerPcRepoName;

        /// <summary>
        /// Test-only: points every operation at another repository. The cloud-sync
        /// self-test sets it so it never writes into a real saves repository — its
        /// manifest round-trip replaces the remote manifest wholesale.
        /// </summary>
        internal static string? RepoNameOverride { get; set; }

        // ⚠ MachineSuffix MUST be declared before PerPcRepoName / DbRepoFileName: static
        // auto-property initializers run in TEXTUAL order, so if it came later it would
        // still be null when they initialize → "library..db" / "emutastic-saves-" on every
        // machine, silently defeating the per-machine namespacing. Keep it first.
        /// <summary>
        /// Stable per-machine token: the hostname squashed to repo/path-safe chars.
        /// (Environment.MachineName is the hostname on Linux.)
        /// </summary>
        private static string MachineSuffix { get; } = BuildMachineSuffix();

        private static string BuildMachineSuffix()
        {
            // GitHub repo names + path segments allow letters, digits, '-', '_', '.';
            // squash anything else in the machine name to '-'.
            var sb = new StringBuilder();
            foreach (char c in Environment.MachineName.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
            string suffix = sb.ToString().Trim('-');
            return suffix.Length == 0 ? "pc" : suffix;
        }

        /// <summary>This machine's dedicated repo name (for UI display).</summary>
        public static string PerPcRepoName { get; } = $"{SharedRepoName}-{MachineSuffix}";

        /// <summary>The repo currently in use (for UI display).</summary>
        public static string EffectiveRepoName => RepoName;

        /// <summary>
        /// The library.db filename THIS machine reads/writes in its repository. The
        /// per-machine name predates per-PC repositories (it kept machines apart inside
        /// one shared repository) and stays so the layout matches the Windows app's.
        /// </summary>
        public static string DbRepoFileName { get; } = $"library.{MachineSuffix}.db";

        private static readonly HttpClient Http = CreateHttp(TimeSpan.FromSeconds(30));
        // Files over 1 MB have to be fetched as raw bytes (see DownloadFileAsync); those
        // transfers can be up to 100 MB, so they get a longer timeout of their own.
        private static readonly HttpClient RawHttp = CreateHttp(TimeSpan.FromMinutes(10));
        private static HttpClient CreateHttp(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic-CloudSync");
            return http;
        }

        private volatile string? _token;
        private volatile string? _passphrase;   // encryption passphrase as restored or saved this session
        private string? _username;
        private readonly ConcurrentDictionary<string, string> _shaCache = new();
        private SyncManifest _manifestCache = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gameLocks = new();

        public bool IsAuthenticated => !string.IsNullOrEmpty(_token);
        public bool IsConfigured => !string.IsNullOrEmpty(ClientId);
        public string? Username => _username;
        public SyncManifest ManifestCache => _manifestCache;

        /// <summary>Why the saved sign-in or passphrase could not be restored this session, or
        /// null. Preferences shows it instead of a bare "Not signed in".</summary>
        public string? RestoreProblem { get; private set; }

        /// <summary>True when the sign-in lives in the desktop keyring rather than config.json.</summary>
        public static bool TokenInKeyring =>
            App.Configuration?.GetCloudSyncConfiguration().GitHubTokenProtected == KeyringSentinel;

        public SemaphoreSlim GetGameLock(string romHash)
            => _gameLocks.GetOrAdd(romHash, _ => new SemaphoreSlim(1, 1));

        // ── Auth: OAuth device flow ──────────────────────────────────────────

        public sealed record DeviceFlowStart(
            string DeviceCode, string UserCode, string VerificationUri,
            int ExpiresIn, int Interval);

        public async Task<DeviceFlowStart> BeginDeviceFlowAsync(CancellationToken ct = default)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("scope", "repo")
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            req.Content = content;
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new DeviceFlowStart(
                root.GetProperty("device_code").GetString()!,
                root.GetProperty("user_code").GetString()!,
                root.GetProperty("verification_uri").GetString()!,
                root.GetProperty("expires_in").GetInt32(),
                root.GetProperty("interval").GetInt32());
        }

        public async Task<bool> PollForTokenAsync(string deviceCode, int intervalSec,
            int expiresInSec, CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(expiresInSec);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false);

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("device_code", deviceCode),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                });
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
                req.Content = content;
                req.Headers.Accept.ParseAdd("application/json");

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("access_token", out var tokenProp))
                {
                    _token = tokenProp.GetString();
                    await ValidateTokenAsync(ct).ConfigureAwait(false);
                    await SaveTokenToConfigAsync().ConfigureAwait(false);
                    return true;
                }
                if (root.TryGetProperty("error", out var err))
                {
                    string error = err.GetString() ?? "";
                    if (error == "authorization_pending") continue;
                    if (error == "slow_down") { intervalSec += 5; continue; }
                    if (error == "expired_token" || error == "access_denied") return false;
                }
            }
            return false;
        }

        public async Task<bool> ValidateTokenAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token)) return false;
            try
            {
                using var req = AuthedRequest(HttpMethod.Get, $"{ApiBase}/user");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.Unauthorized) { _token = null; return false; }
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    _username = doc.RootElement.GetProperty("login").GetString();
                }
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public void SignOut()
        {
            _token = null;
            _username = null;
            RestoreProblem = null;
            _shaCache.Clear();
            _manifestCache = new SyncManifest();
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg != null && App.Configuration != null)
            {
                bool inKeyring = cfg.GitHubTokenProtected == KeyringSentinel;
                cfg.GitHubTokenProtected = "";
                cfg.GitHubUsername = "";
                cfg.Enabled = false;
                App.Configuration.SetCloudSyncConfiguration(cfg);
                App.Configuration.ScheduleSave();
                if (inKeyring)
                    _ = OnKeyringQueue(() =>
                    {
                        if (!Emutastic.Platform.SecretStore.TryClear(TokenCredential, out string? err))
                            CloudSyncLog.Write($"Keyring: could not remove the sign-in: {err}");
                        return true;
                    });
            }
        }

        // ── Credentials at rest (Linux delta) ────────────────────────────────
        // Upstream keeps the token and passphrase in config under DPAPI. Here they live in the
        // desktop keyring (Secret Service through libsecret, where gh keeps its GitHub token on
        // Linux too) and config.json holds only KeyringSentinel. Without a keyring (libsecret
        // missing, nothing on the session bus) the value itself stays in config.json, which
        // JsonConfigurationService writes owner-only. Keyring calls block — an unlock prompt
        // can hold one indefinitely — so they run on the thread pool, one at a time and in the
        // order requested: a sign-out's delete can never land after the next sign-in's store.

        /// <summary>Config value meaning "stored in the desktop keyring".</summary>
        internal const string KeyringSentinel = "secret-service";
        internal const string TokenCredential = "github-token";
        internal const string PassphraseCredential = "sync-passphrase";

        private const string KeyringUnreadable =
            "Your saved sign-in is in the system keyring, which could not be read (locked or not running). Unlock it and restart Emutastic, or sign in again.";
        private const string TokenMissing =
            "Your saved sign-in is no longer in the system keyring. Sign in again.";
        private const string PassphraseMissing =
            "Encryption is on, but its passphrase is missing from the system keyring. Enter it again to resume syncing.";

        private static readonly object _keyringGate = new();
        private static Task _keyringTail = Task.CompletedTask;
        private Task<bool>? _restore;

        private static Task<T> OnKeyringQueue<T>(Func<T> op)
        {
            lock (_keyringGate)
            {
                var next = _keyringTail.ContinueWith(_ => op(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
                _keyringTail = next;
                return next;
            }
        }

        /// <summary>Completes once the saved sign-in has been restored, or found absent.</summary>
        public Task RestoreTask => _restore ?? Task.CompletedTask;

        /// <summary>
        /// Restores the saved sign-in and encryption passphrase: from the keyring, or from
        /// config.json, moving them into the keyring when one is available now. Queued on the
        /// thread pool and returns at once; runs once per process. True = signed in.
        /// </summary>
        public Task<bool> RestoreSessionAsync()
        {
            lock (_keyringGate) return _restore ??= OnKeyringQueue(RestoreSession);
        }

        private bool RestoreSession()
        {
            try
            {
                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                if (cfg == null || App.Configuration == null) return false;

                // Passphrase first: once the token is set a sync may start, and it needs this.
                var pass = ReadStoredSecret(cfg.PassphraseProtected, PassphraseCredential);
                _passphrase = pass.Value;
                var token = ReadStoredSecret(cfg.GitHubTokenProtected, TokenCredential);
                if (!string.IsNullOrEmpty(token.Value))
                {
                    _username = string.IsNullOrEmpty(cfg.GitHubUsername) ? null : cfg.GitHubUsername;
                    _token = token.Value;
                }

                if (token.Unreadable || pass.Unreadable)
                    RestoreProblem = KeyringUnreadable;
                else if (cfg.GitHubTokenProtected == KeyringSentinel && token.Value == null)
                    RestoreProblem = TokenMissing;
                else if (cfg.EncryptionEnabled && cfg.PassphraseProtected == KeyringSentinel && pass.Value == null)
                    RestoreProblem = PassphraseMissing;

                if (pass.MovedToKeyring || token.MovedToKeyring)
                {
                    if (pass.MovedToKeyring) cfg.PassphraseProtected = KeyringSentinel;
                    if (token.MovedToKeyring) cfg.GitHubTokenProtected = KeyringSentinel;
                    App.Configuration.SetCloudSyncConfiguration(cfg);
                    App.Configuration.ScheduleSave();
                }
                return IsAuthenticated;
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Restoring the saved sign-in failed: {ex.Message}");
                return false;
            }
        }

        private readonly record struct StoredSecret(string? Value, bool Unreadable, bool MovedToKeyring);

        /// <summary>
        /// One stored credential field: "" = nothing saved; the sentinel = read the keyring;
        /// anything else = the value itself (an older build, or a session without a keyring),
        /// which moves into the keyring when one is available now.
        /// </summary>
        private static StoredSecret ReadStoredSecret(string stored, string credential)
        {
            if (string.IsNullOrEmpty(stored)) return default;
            if (stored == KeyringSentinel)
            {
                if (!Emutastic.Platform.SecretStore.TryLookup(credential, out string? secret, out string? err))
                {
                    CloudSyncLog.Write($"Keyring: could not read {credential}: {err}");
                    return new StoredSecret(null, Unreadable: true, MovedToKeyring: false);
                }
                if (secret == null) CloudSyncLog.Write($"Keyring: no {credential} stored");
                return new StoredSecret(secret, Unreadable: false, MovedToKeyring: false);
            }
            bool moved = Emutastic.Platform.SecretStore.TryStore(credential, stored, LabelFor(credential), out string? storeErr);
            CloudSyncLog.Write(moved
                ? $"Keyring: moved {credential} out of config.json"
                : $"Keyring unavailable, {credential} stays in config.json: {storeErr}");
            return new StoredSecret(stored, Unreadable: false, MovedToKeyring: moved);
        }

        private async Task SaveTokenToConfigAsync()
        {
            string token = _token ?? "";
            string field = await OnKeyringQueue(() => StoreSecretField(TokenCredential, token)).ConfigureAwait(false);
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null || App.Configuration == null) return;
            cfg.GitHubTokenProtected = field;
            cfg.GitHubUsername = _username ?? "";
            cfg.Enabled = true;
            if (RestoreProblem != PassphraseMissing) RestoreProblem = null;
            App.Configuration.SetCloudSyncConfiguration(cfg);
            App.Configuration.ScheduleSave();
        }

        /// <summary>
        /// Saves the encryption passphrase: keyring first, config.json without one. It takes
        /// effect in memory at once, so a sync that starts before the keyring write finishes
        /// already encrypts with it. True when it went into the keyring.
        /// </summary>
        public async Task<bool> SetPassphraseAsync(string passphrase)
        {
            _passphrase = passphrase;
            string field = await OnKeyringQueue(() => StoreSecretField(PassphraseCredential, passphrase)).ConfigureAwait(false);
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null || App.Configuration == null) return false;
            cfg.PassphraseProtected = field;
            if (RestoreProblem == PassphraseMissing) RestoreProblem = null;
            App.Configuration.SetCloudSyncConfiguration(cfg);
            App.Configuration.ScheduleSave();
            return field == KeyringSentinel;
        }

        // Keyring first; the value itself (config.json, owner-only) when there is no keyring.
        private static string StoreSecretField(string credential, string secret)
        {
            if (Emutastic.Platform.SecretStore.TryStore(credential, secret, LabelFor(credential), out string? err))
                return KeyringSentinel;
            CloudSyncLog.Write($"Keyring unavailable, {credential} saved in config.json instead: {err}");
            return secret;
        }

        private static string LabelFor(string credential) => credential == TokenCredential
            ? "Emutastic cloud sync: GitHub sign-in"
            : "Emutastic cloud sync: encryption passphrase";

        private HttpRequestMessage AuthedRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            return req;
        }

        // ── Repo bootstrap ───────────────────────────────────────────────────

        public async Task EnsureRepoExistsAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return;
            try
            {
                using var checkReq = AuthedRequest(HttpMethod.Get, $"{ApiBase}/repos/{_username}/{RepoName}");
                using var check = await Http.SendAsync(checkReq, ct).ConfigureAwait(false);
                if (check.IsSuccessStatusCode) return;
            }
            catch { }

            try
            {
                string body = JsonSerializer.Serialize(new
                {
                    name = RepoName,
                    @private = true,
                    description = "Emutastic cloud saves",
                    auto_init = false
                });
                using var req = AuthedRequest(HttpMethod.Post, $"{ApiBase}/user/repos");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
                    CloudSyncLog.Write("Repo already exists (422)");
                else
                    resp.EnsureSuccessStatusCode();
                CloudSyncLog.Write($"Created repo {_username}/{RepoName}");
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Repo creation failed: {ex.Message}");
            }
        }

        // ── Contents API file I/O ────────────────────────────────────────────

        public async Task<bool> UploadFileAsync(string repoPath, byte[] fileBytes,
            CancellationToken ct = default, bool isRetry = false)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return false;
            try
            {
                string base64 = Convert.ToBase64String(fileBytes);
                _shaCache.TryGetValue(repoPath, out string? existingSha);

                var payload = new Dictionary<string, object>
                {
                    ["message"] = $"sync {repoPath}",
                    ["content"] = base64
                };
                if (!string.IsNullOrEmpty(existingSha)) payload["sha"] = existingSha;

                string body = JsonSerializer.Serialize(payload);
                using var req = AuthedRequest(HttpMethod.Put, $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

                // 409/422 = our SHA is stale (file changed by another machine);
                // refresh the cache and retry once.
                if ((resp.StatusCode == HttpStatusCode.Conflict
                     || resp.StatusCode == HttpStatusCode.UnprocessableEntity) && !isRetry)
                {
                    await RefreshShaCacheAsync(ct).ConfigureAwait(false);
                    return await UploadFileAsync(repoPath, fileBytes, ct, isRetry: true).ConfigureAwait(false);
                }

                if (resp.IsSuccessStatusCode)
                {
                    string respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(respJson);
                    if (doc.RootElement.TryGetProperty("content", out var c)
                        && c.TryGetProperty("sha", out var newSha))
                        _shaCache[repoPath] = newSha.GetString() ?? "";
                    // The freshly-uploaded variant is now canonical — remove its
                    // encryption-toggle counterpart so exactly one variant of each
                    // file ever exists remotely. Without this, toggling encryption
                    // leaves stale .enc/.srm shadows that a later toggle-back would
                    // resurrect over newer saves (silent rollback on fresh installs).
                    await DeleteCounterpartVariantAsync(repoPath, ct).ConfigureAwait(false);
                    return true;
                }

                CloudSyncLog.Write($"Upload failed {repoPath}: {FailureText(resp)}");
                return false;
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Upload exception {repoPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes a file from the sync repo. Requires the blob sha, which is
        /// taken from the sha cache — returns false (no-op) when the path isn't
        /// cached. Git history retains the blob, so deletion is recoverable.
        /// </summary>
        public async Task<bool> DeleteFileAsync(string repoPath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return false;
            if (!_shaCache.TryGetValue(repoPath, out string? sha) || string.IsNullOrEmpty(sha))
                return false;

            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["message"] = $"remove {repoPath}",
                    ["sha"] = sha
                };
                using var req = AuthedRequest(HttpMethod.Delete,
                    $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}");
                req.Content = new StringContent(JsonSerializer.Serialize(payload),
                    Encoding.UTF8, "application/json");

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _shaCache.TryRemove(repoPath, out _);
                    _manifestCache.Files.TryRemove(repoPath, out _);
                    return true;
                }

                CloudSyncLog.Write($"Delete failed {repoPath}: {FailureText(resp)}");
                return false;
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Delete exception {repoPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes the encryption-toggle counterpart of a just-uploaded path
        /// ("X.srm" ↔ "X.srm.enc", "manifest.json" ↔ "manifest.json.enc") so the
        /// repo converges to a single variant per file. If the remote delete
        /// can't run (sha not cached), the manifest entry is still dropped so
        /// the stale variant stops being advertised to download passes; the
        /// blob itself gets cleaned up by a later sync once the sha cache
        /// knows it.
        /// </summary>
        private async Task DeleteCounterpartVariantAsync(string repoPath, CancellationToken ct)
        {
            string counterpart = repoPath.EndsWith(".enc", StringComparison.Ordinal)
                ? repoPath[..^4]
                : repoPath + ".enc";

            bool known = _shaCache.ContainsKey(counterpart)
                || _manifestCache.Files.ContainsKey(counterpart);
            if (!known) return;

            if (await DeleteFileAsync(counterpart, ct).ConfigureAwait(false))
                CloudSyncLog.Write($"Removed stale variant: {counterpart}");
            else
                _manifestCache.Files.TryRemove(counterpart, out _);
        }

        /// <param name="quietIfMissing">A 404 is an expected answer for this caller (a manifest before
        /// the first sync, a game with no cloud save yet) rather than a failure worth logging.</param>
        public async Task<byte[]?> DownloadFileAsync(string repoPath, CancellationToken ct = default, bool quietIfMissing = false)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return null;
            try
            {
                string url = $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}";
                using var req = AuthedRequest(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (!(quietIfMissing && resp.StatusCode == HttpStatusCode.NotFound))
                        CloudSyncLog.Write($"Download failed {repoPath}: {FailureText(resp)}");
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string base64 = (root.GetProperty("content").GetString() ?? "")
                    .Replace("\n", "").Replace("\r", "");
                if (root.TryGetProperty("sha", out var shaProp))
                    _shaCache[repoPath] = shaProp.GetString() ?? "";

                // The Contents API inlines files only up to 1 MB. A bigger one (up to 100 MB) comes
                // back with an empty "content" and encoding "none" and has to be fetched as raw bytes;
                // decoding the empty string instead returned zero bytes, which callers skipped silently.
                if (base64.Length == 0 && root.TryGetProperty("size", out var sizeProp) && sizeProp.GetInt64() > 0)
                    return await DownloadRawAsync(url, repoPath, ct).ConfigureAwait(false);
                return Convert.FromBase64String(base64);
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Download exception {repoPath}: {ex.Message}");
                return null;
            }
        }

        private async Task<byte[]?> DownloadRawAsync(string url, string repoPath, CancellationToken ct)
        {
            using var req = AuthedRequest(HttpMethod.Get, url);
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/vnd.github.raw");
            using var resp = await RawHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                CloudSyncLog.Write($"Download failed {repoPath} (raw): {FailureText(resp)}");
                return null;
            }
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        // Status of a failed response, plus GitHub's rate-limit headers when they are the reason.
        private static string FailureText(HttpResponseMessage resp)
        {
            var text = new StringBuilder($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
                    text.Append($", rate limit remaining {string.Join(",", remaining)}");
                if (resp.Headers.RetryAfter?.Delta is { } retry)
                    text.Append($", retry after {retry.TotalSeconds:0} s");
            }
            return text.ToString();
        }

        public async Task RefreshShaCacheAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return;
            try
            {
                using var req = AuthedRequest(HttpMethod.Get,
                    $"{ApiBase}/repos/{_username}/{RepoName}/git/trees/HEAD?recursive=1");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;   // empty repo has no HEAD yet

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tree", out var tree)) return;
                _shaCache.Clear();
                foreach (var item in tree.EnumerateArray())
                {
                    if (item.GetProperty("type").GetString() != "blob") continue;
                    string path = item.GetProperty("path").GetString() ?? "";
                    string sha = item.GetProperty("sha").GetString() ?? "";
                    if (path.Length > 0) _shaCache[path] = sha;
                }
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"SHA cache refresh failed: {ex.Message}");
            }
        }

        // ── Protection-at-rest ───────────────────────────────────────────────
        // Upstream decrypts the passphrase from config (DPAPI) at every call site. Here it is
        // resolved once per session (see RestoreSession) and every sync operation asks this gate.

        /// <summary>
        /// The passphrase one sync operation encrypts with. Null = encryption off, or on with no
        /// passphrase ever saved (upstream syncs in the clear then). False = encryption is on but
        /// its passphrase is not known this session — a keyring that could not be read, or a
        /// stored value not restored yet. The caller must not sync then: it would upload saves
        /// under the wrong key and fail to read the real ones.
        /// </summary>
        internal static bool ResolvePassphrase(bool encryptionEnabled, string storedField, string? known, out string? passphrase)
        {
            passphrase = null;
            if (!encryptionEnabled) return true;
            if (string.IsNullOrEmpty(known)) return string.IsNullOrEmpty(storedField);
            passphrase = known;
            return true;
        }

        private bool TryGetPassphrase(CloudSyncConfiguration? cfg, out string? passphrase)
        {
            if (ResolvePassphrase(cfg?.EncryptionEnabled == true, cfg?.PassphraseProtected ?? "", _passphrase, out passphrase))
                return true;
            CloudSyncLog.Write("Encryption is on but its passphrase is not available (keyring locked or unreadable) — sync skipped");
            return false;
        }

        // ── Encryption (AES-256-GCM, PBKDF2-SHA256 key) ──────────────────────

        public static byte[] DeriveKey(string passphrase, string githubUsername)
        {
            byte[] salt = Encoding.UTF8.GetBytes($"emutastic-sync-{githubUsername}");
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(passphrase), salt, 100_000,
                HashAlgorithmName.SHA256, 32);
        }

        public static byte[] Encrypt(byte[] plaintext, byte[] key)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            var result = new byte[12 + 16 + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, 12);
            Buffer.BlockCopy(tag, 0, result, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);
            return result;
        }

        public static byte[] Decrypt(byte[] blob, byte[] key)
        {
            if (blob.Length < 28) throw new CryptographicException("Invalid encrypted data");
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[blob.Length - 28];
            Buffer.BlockCopy(blob, 0, nonce, 0, 12);
            Buffer.BlockCopy(blob, 12, tag, 0, 16);
            Buffer.BlockCopy(blob, 28, ciphertext, 0, ciphertext.Length);
            byte[] plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }

        // ── Manifest ─────────────────────────────────────────────────────────

        public async Task LoadManifestAsync(CancellationToken ct = default)
        {
            try
            {
                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                if (!TryGetPassphrase(cfg, out string? passphrase)) return;
                bool encrypted = passphrase != null;
                string path = encrypted ? "manifest.json.enc" : "manifest.json";

                byte[]? data = await DownloadFileAsync(path, ct, quietIfMissing: true).ConfigureAwait(false);
                if (data == null || data.Length == 0) { _manifestCache = new SyncManifest(); return; }

                if (encrypted)
                {
                    byte[] key = DeriveKey(passphrase!, _username ?? "");
                    data = Decrypt(data, key);
                }
                string json = Encoding.UTF8.GetString(data);
                _manifestCache = JsonSerializer.Deserialize<SyncManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Manifest load failed: {ex.Message}");
                _manifestCache = new SyncManifest();
            }
        }

        public async Task SaveManifestAsync(CancellationToken ct = default)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(_manifestCache, new JsonSerializerOptions { WriteIndented = true }));

                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                if (!TryGetPassphrase(cfg, out string? passphrase)) return;
                bool encrypted = passphrase != null;
                if (encrypted)
                {
                    byte[] key = DeriveKey(passphrase!, _username ?? "");
                    data = Encrypt(data, key);
                }
                string path = encrypted ? "manifest.json.enc" : "manifest.json";
                await UploadFileAsync(path, data, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Manifest save failed: {ex.Message}");
            }
        }

        // ── Last-synced db hash (local side-car) ────────────────────────────
        // Hash of the library.db snapshot this MACHINE last uploaded or adopted.
        // Deliberately local (not in the shared manifest): it answers "did *I*
        // change since *my* last sync?", which is per-machine state. Lives in
        // DataRoot so portable installs carry it with their data.

        // Keyed by repo name, so the side-car written while this PC still synced to the
        // shared repository never passes for the state of its own repository.
        private static string DbStatePath
            => Path.Combine(AppPaths.DataRoot, $"cloudsync_dbstate_{RepoName}.txt");

        private static string? LoadLastSyncedDbHash()
        {
            try
            {
                string p = DbStatePath;
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch { return null; }
        }

        private static void SaveLastSyncedDbHash(string hash)
        {
            try { File.WriteAllText(DbStatePath, hash); }
            catch { /* non-fatal — worst case one redundant upload next sync */ }
        }

        // ── Local save mapping (Linux delta) ─────────────────────────────────
        // The session writes battery saves per-console: Saves/<Console>/<romstem>.srm
        // (SaveLayoutMigrator moved any legacy flat saves into place). The REPO keeps
        // upstream's hash-keyed layout (BatterySaves/<Console>/<RomHash>.srm), so a
        // repository looks the same whichever app wrote it.
        //
        // Compress = gzip the payload before upload (used for console-managed saves,
        // which are often mostly-empty fixed-size cards that shrink hugely).

        public sealed record LocalSave(string RepoPath, string LocalPath, DateTime LastModifiedUtc, long SizeBytes, bool Compress = false);

        /// <summary>ROM-hack-aware: hacked entries share the base ROM file, so the
        /// session keys their .srm by stem + first 8 hash chars (EmulatorSession's rule) —
        /// mirror it here or sync would read/write the BASE game's save for a hack entry.
        /// Saves live under Saves/&lt;Console&gt;/ to match EmulatorSession's save_directory.</summary>
        public static string LocalSrmPathFor(string console, string romPath, bool hasPatch, string? romHash)
        {
            string stem = Path.GetFileNameWithoutExtension(romPath);
            if (hasPatch && !string.IsNullOrEmpty(romHash))
                stem += "." + romHash[..Math.Min(8, romHash.Length)];
            return Path.Combine(AppPaths.GetFolder("Saves", console), stem + ".srm");
        }

        public static string RepoPathFor(string console, string romHash)
            => $"BatterySaves/{console}/{romHash}.srm";

        private static List<LocalSave> BuildLocalSaveMap(DatabaseService db)
        {
            var result = new List<LocalSave>();
            foreach (var g in db.GetGamesSyncMap())
            {
                if (string.IsNullOrEmpty(g.RomHash) || string.IsNullOrEmpty(g.Console)) continue;
                string local = LocalSrmPathFor(g.Console, g.RomPath, g.HasPatch, g.RomHash);
                if (!File.Exists(local)) continue;
                var fi = new FileInfo(local);
                result.Add(new LocalSave(RepoPathFor(g.Console, g.RomHash), local, fi.LastWriteTimeUtc, fi.Length));
            }
            return result;
        }

        // ── Console-managed saves (memory cards, VMUs, save trees) ──────────────
        // BuildLocalSaveMap above only covers frontend-managed SRAM (.srm). Cores like
        // PCSX2, PPSSPP, Dolphin, flycast and Azahar write their OWN memory cards / save
        // trees into the save directory (= Saves/<Console>/). Those are synced here,
        // keyed by relative path (shared / console-level, not per-game), gzip-compressed,
        // with caches, shader caches, save-states and unsupported consoles excluded.
        //
        // Repo paths keep upstream's "BatterySaves/<Console>/<rel>" convention (local "Saves/" ↔
        // repo "BatterySaves/"), so a repository looks the same whichever app wrote it.

        private static readonly HashSet<string> UnsupportedSaveConsoles =
            new(StringComparer.OrdinalIgnoreCase) { "DOS" };

        private static bool IsUnsupportedConsole(string console)
            => UnsupportedSaveConsoles.Contains(console);

        // Path segments that are never battery saves: emulator caches, shader caches,
        // save-states (synced separately), dumps, logs, screenshots, and HD texture packs —
        // HdPackService installs PSP packs into <saves>/PSP/PSP/TEXTURES, where they synced
        // as if they were saves (a single pack runs to thousands of files and hundreds of MB).
        private static readonly HashSet<string> ExcludedSaveSegments =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Cache", "Shaders", "ShaderCache", "StateSaves", "PPSSPP_STATE",
                "Dump", "Logs", "ScreenShots", "Screenshots", "Triforce", "WFS",
                "TEXTURES"
            };

        // A cloud path under BatterySaves/<Console>/ that the exclusions keep out of sync (counted
        // for the log, so skipped texture packs are visible rather than silently ignored).
        private static bool IsExcludedRepoPath(string repoPath)
        {
            string p = repoPath.EndsWith(".enc", StringComparison.Ordinal) ? repoPath[..^4] : repoPath;
            if (!p.StartsWith("BatterySaves/", StringComparison.Ordinal)
                || p.EndsWith(".srm", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = p["BatterySaves/".Length..];
            int slash = rest.IndexOf('/');
            return slash > 0 && IsExcludedSavePath(rest[(slash + 1)..]);
        }

        // BIOS files, by the names System Files knows them under. They belong in the System folder,
        // but copies land in save trees too (GameCubeHandler mirrors IPL.bin into Dolphin's
        // User/GC/<region> at launch), and a backup of saves is no place for any of them.
        private static readonly HashSet<string> BiosFileNames =
            KnownBios.All.Select(b => Path.GetFileName(b.Filename)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static bool IsExcludedSavePath(string rel)
        {
            if (rel.EndsWith(".srm", StringComparison.OrdinalIgnoreCase)) return true;
            string[] segs = rel.Split('/', '\\');
            if (BiosFileNames.Contains(segs[^1])) return true;
            bool underTitle = false;
            for (int i = 0; i < segs.Length; i++)
            {
                if (ExcludedSaveSegments.Contains(segs[i])) return true;
                if (i == segs.Length - 1) break;   // the rules below match folders, never a save's own name
                // Console system files: installed title content (3DS system archives and installed
                // titles in Azahar's nand/ and sdmc/, Wii channels in Dolphin's User/Wii) and Azahar's
                // ticket database. A title's saves sit beside its content, under title/.../data.
                if (underTitle && segs[i].Equals("content", StringComparison.OrdinalIgnoreCase)) return true;
                if (segs[i].Equals("title", StringComparison.OrdinalIgnoreCase)) underTitle = true;
                if (i > 0 && segs[i].Equals("dbs", StringComparison.OrdinalIgnoreCase)
                    && segs[i - 1].Equals("nand", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Sprawling emulator trees where only specific subfolders are saves; the rest
        // (Dolphin's User/Cache, Azahar's shaders) must never be uploaded. Null = sync
        // the whole console folder minus the excludes.
        private static string[]? SaveAllowlist(string console) => console switch
        {
            "GameCube" => new[] { "User/GC", "User/Wii" },        // Dolphin memcards + Wii NAND
            "3DS"      => new[] { "Azahar/nand", "Azahar/sdmc" }, // 3DS save data
            _          => null,
        };

        /// <summary>
        /// Every console-managed save file on disk (memory cards, VMUs, PSP/3DS/GC save
        /// trees, arcade nvram, …) as repo-pathed, gzip-flagged entries. The per-game
        /// ".srm" files are handled by <see cref="BuildLocalSaveMap"/>.
        /// </summary>
        public static List<LocalSave> BuildExtraSaveMap()
        {
            var result = new List<LocalSave>();
            string root = AppPaths.GetFolder("Saves");
            if (!Directory.Exists(root)) return result;

            foreach (string consoleDir in Directory.EnumerateDirectories(root))
            {
                string console = Path.GetFileName(consoleDir);
                if (IsUnsupportedConsole(console)) continue;

                string[]? allow = SaveAllowlist(console);
                IEnumerable<string> bases = allow == null
                    ? new[] { consoleDir }
                    : allow.Select(a => Path.Combine(
                               consoleDir, a.Replace('/', Path.DirectorySeparatorChar)))
                           .Where(Directory.Exists);

                foreach (string baseDir in bases)
                {
                    foreach (string full in Directory.EnumerateFiles(
                                 baseDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(consoleDir, full);
                        if (IsExcludedSavePath(rel)) continue;

                        var fi = new FileInfo(full);
                        string repoPath = $"BatterySaves/{console}/{rel.Replace('\\', '/')}";
                        result.Add(new LocalSave(
                            repoPath, full, fi.LastWriteTimeUtc, fi.Length, Compress: true));
                    }
                }
            }
            return result;
        }

        // Map a remote extra-save repo path back to its local path even when the file
        // doesn't exist on this PC yet (second-machine restore). Rejects per-game ".srm"
        // (handled elsewhere), unsupported consoles, and excludes. Repo "BatterySaves/"
        // maps to local "Saves/".
        private static bool TryResolveExtraSaveLocalPath(string repoPath, bool encrypted, out string localPath)
        {
            localPath = "";
            string p = repoPath;
            if (encrypted && p.EndsWith(".enc", StringComparison.Ordinal)) p = p[..^4];
            if (!p.StartsWith("BatterySaves/", StringComparison.Ordinal)) return false;
            if (p.EndsWith(".srm", StringComparison.OrdinalIgnoreCase)) return false;

            string rest = p["BatterySaves/".Length..];
            int slash = rest.IndexOf('/');
            if (slash <= 0) return false;
            string console = rest[..slash];
            string rel = rest[(slash + 1)..];
            if (rel.Length == 0 || IsUnsupportedConsole(console) || IsExcludedSavePath(rel)) return false;

            localPath = Path.Combine(
                AppPaths.GetFolder("Saves", console),
                rel.Replace('/', Path.DirectorySeparatorChar));
            return true;
        }

        private static byte[] GzipCompress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(
                       ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] GzipDecompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gz = new System.IO.Compression.GZipStream(
                input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        // ── Full bidirectional sync ("Sync Now") ─────────────────────────────

        public sealed record SyncResult(int Uploaded, int Downloaded, int Errors);

        public enum SyncPhase { Checking, Uploading, Downloading, Library, Finishing }

        /// <summary>One report from a running full sync. Done/Total count files within the current
        /// phase; Library and Finishing are single steps.</summary>
        public sealed record SyncProgress(SyncPhase Phase, int Done, int Total, int Uploaded, int Downloaded, int Errors);

        /// <summary>Raised from the sync's own thread: on every phase change and phase end, and at
        /// most every 100 ms in between.</summary>
        public event Action<SyncProgress>? SyncProgressChanged;

        /// <summary>The latest report of the running full sync, or null when none is running.</summary>
        public SyncProgress? CurrentProgress { get; private set; }

        /// <summary>The result of the last full sync that finished this session, or null.</summary>
        public SyncResult? LastResult { get; private set; }

        public static string DescribeProgress(SyncProgress p) => p.Phase switch
        {
            SyncPhase.Checking    => "checking what changed…",
            SyncPhase.Uploading   => $"uploading {p.Done:N0} of {p.Total:N0}",
            SyncPhase.Downloading => $"downloading {p.Done:N0} of {p.Total:N0}",
            SyncPhase.Library     => "syncing the library database…",
            _                     => "saving the sync manifest…",
        };

        public static string DescribeResult(SyncResult r) => r.Errors > 0
            ? $"{r.Uploaded:N0} up, {r.Downloaded:N0} down, {r.Errors:N0} failed (details in Logs/cloudsync.log)"
            : $"{r.Uploaded:N0} up, {r.Downloaded:N0} down";

        private static string Megabytes(long bytes) =>
            bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.#} MB" : $"{bytes / 1000.0:0.#} KB";

        // Keeps progress events at a UI-friendly rate — every phase change and phase end goes out,
        // otherwise at most one report per 100 ms — and leaves a trail in cloudsync.log every 250
        // files or 30 seconds, so a long sync is never silent.
        private sealed class ProgressThrottle
        {
            private readonly GitHubSyncService _owner;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private long _lastEventMs = -1000, _lastLogMs;
            private SyncPhase? _phase;
            private int _lastLoggedDone;

            public ProgressThrottle(GitHubSyncService owner) => _owner = owner;

            public void Report(SyncProgress p)
            {
                long now = _clock.ElapsedMilliseconds;
                bool phaseChanged = _phase != p.Phase;
                if (phaseChanged) { _phase = p.Phase; _lastLogMs = now; _lastLoggedDone = 0; }
                bool phaseEnd = p.Total > 0 && p.Done >= p.Total;
                _owner.CurrentProgress = p;
                if (phaseChanged || phaseEnd || now - _lastEventMs >= 100)
                {
                    _lastEventMs = now;
                    try { _owner.SyncProgressChanged?.Invoke(p); } catch { }
                }
                if (p.Phase is SyncPhase.Uploading or SyncPhase.Downloading && p.Done > _lastLoggedDone
                    && (phaseEnd || p.Done - _lastLoggedDone >= 250 || now - _lastLogMs >= 30_000))
                {
                    CloudSyncLog.Write($"{p.Phase}: {p.Done:N0} of {p.Total:N0} ({p.Errors:N0} failed so far)");
                    _lastLoggedDone = p.Done;
                    _lastLogMs = now;
                }
            }
        }

        private readonly object _fullSyncGate = new();
        private Task<SyncResult>? _fullSync;

        /// <summary>
        /// Runs a full two-way sync. While one is already running this returns THAT sync, so a second
        /// caller (Sync Now during the sync started at sign-in or launch) waits for its real result —
        /// it used to get an empty "0 up, 0 down" back at once.
        /// </summary>
        public Task<SyncResult> FullSyncAsync(DatabaseService db, CancellationToken ct = default)
        {
            if (!IsAuthenticated) return Task.FromResult(new SyncResult(0, 0, 0));
            lock (_fullSyncGate)
            {
                if (_fullSync is { IsCompleted: false }) return _fullSync;
                return _fullSync = Task.Run(() => RunFullSyncAsync(db, ct));
            }
        }

        private async Task<SyncResult> RunFullSyncAsync(DatabaseService db, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            var progress = new ProgressThrottle(this);
            int uploaded = 0, downloaded = 0, errors = 0;
            try { SyncStateChanged?.Invoke(true); } catch { }
            progress.Report(new SyncProgress(SyncPhase.Checking, 0, 0, 0, 0, 0));
            try
            {
                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                if (!TryGetPassphrase(cfg, out string? passphrase)) return Finish(new SyncResult(0, 0, 1));
                bool encrypted = passphrase != null;
                byte[]? encKey = encrypted ? DeriveKey(passphrase!, _username ?? "") : null;
                string encSuffix = encrypted ? ".enc" : "";
                CloudSyncLog.Write($"Full sync started: {_username}/{RepoName}{(encrypted ? " (encrypted)" : "")}");

                // This PC's repository may not exist yet (a sign-in from before every PC had its own).
                await EnsureRepoExistsAsync(ct).ConfigureAwait(false);
                await RefreshShaCacheAsync(ct).ConfigureAwait(false);
                await LoadManifestAsync(ct).ConfigureAwait(false);
                CloudSyncLog.Write($"Cloud manifest lists {_manifestCache.Files.Count:N0} file(s)");

                // Converge the repo to one variant per file. An encryption toggle
                // re-uploads everything under the other suffix but historically left
                // the old variant behind; when BOTH X and X.enc exist remotely, drop
                // the one that doesn't match the current mode. Both-exist is required:
                // an opposite-variant file with no counterpart is the only surviving
                // copy of that save and must stay downloadable after a toggle-back.
                foreach (var stale in _shaCache.Keys.ToList())
                {
                    if (ct.IsCancellationRequested) break;
                    bool isEnc = stale.EndsWith(".enc", StringComparison.Ordinal);
                    if (isEnc == encrypted) continue;              // matches current mode — keep
                    string counterpart = isEnc ? stale[..^4] : stale + ".enc";
                    if (!_shaCache.ContainsKey(counterpart)) continue; // lone copy — keep
                    if (await DeleteFileAsync(stale, ct).ConfigureAwait(false))
                        CloudSyncLog.Write($"Removed stale variant: {stale}");
                }

                // UPLOAD: local saves newer than the manifest. Per-game .srm plus the
                // console-managed memory cards / save trees (gzip-flagged via Compress).
                var localSaves = BuildLocalSaveMap(db);
                localSaves.AddRange(BuildExtraSaveMap());
                var toUpload = new List<LocalSave>();
                foreach (var local in localSaves)
                {
                    if (_manifestCache.Files.TryGetValue(local.RepoPath + encSuffix, out var entry)
                        && DateTime.TryParse(entry.LastModifiedUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime)
                        && local.LastModifiedUtc <= remoteMtime)
                        continue;
                    toUpload.Add(local);
                }
                CloudSyncLog.Write($"Upload: {toUpload.Count:N0} of {localSaves.Count:N0} local save file(s) are new or newer here " +
                                   $"({Megabytes(toUpload.Sum(l => l.SizeBytes))})");

                int done = 0;
                progress.Report(new SyncProgress(SyncPhase.Uploading, 0, toUpload.Count, uploaded, downloaded, errors));
                foreach (var local in toUpload)
                {
                    if (ct.IsCancellationRequested) break;
                    string repoPath = local.RepoPath + encSuffix;
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(local.LocalPath);
                        if (local.Compress) bytes = GzipCompress(bytes);
                        if (encrypted && encKey != null) bytes = Encrypt(bytes, encKey);
                        if (await UploadFileAsync(repoPath, bytes, ct).ConfigureAwait(false))
                        {
                            _manifestCache.Files[repoPath] = new SyncFileEntry
                            {
                                LastModifiedUtc = local.LastModifiedUtc.ToString("o"),
                                SizeBytes = local.SizeBytes
                            };
                            uploaded++;
                        }
                        else errors++;   // UploadFileAsync logged why
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        CloudSyncLog.Write($"Upload failed {repoPath}: {ex.Message}");
                    }
                    progress.Report(new SyncProgress(SyncPhase.Uploading, ++done, toUpload.Count, uploaded, downloaded, errors));
                }

                // DOWNLOAD: remote saves newer than local (or missing locally). Build a
                // lookup from repo path → (local path, gzip?) for ALL games (including
                // never-played), plus the console-managed extra saves.
                var repoToLocalPath = new Dictionary<string, (string LocalPath, bool Compressed)>();
                foreach (var g in db.GetGamesSyncMap())
                {
                    if (string.IsNullOrEmpty(g.RomHash) || string.IsNullOrEmpty(g.Console)) continue;
                    repoToLocalPath[RepoPathFor(g.Console, g.RomHash) + encSuffix] =
                        (LocalSrmPathFor(g.Console, g.RomPath, g.HasPatch, g.RomHash), false);
                }
                foreach (var extra in BuildExtraSaveMap())
                    repoToLocalPath[extra.RepoPath + encSuffix] = (extra.LocalPath, true);

                // Covers per-game .srm (via repoToLocalPath) and console-managed extra
                // saves — including ones missing locally (restoring this PC's backup after a reinstall).
                var toDownload = new List<(string RepoPath, string TargetPath, bool Compressed,
                                           bool HasRemoteMtime, DateTime RemoteMtime, long SizeBytes)>();
                int skippedNonSave = 0;
                foreach (var (repoPath, entry) in _manifestCache.Files)
                {
                    if (!repoPath.StartsWith("BatterySaves/")) continue;

                    string targetPath;
                    bool compressed;
                    if (repoToLocalPath.TryGetValue(repoPath, out var mapped))
                    {
                        targetPath = mapped.LocalPath;
                        compressed = mapped.Compressed;
                    }
                    else if (TryResolveExtraSaveLocalPath(repoPath, encrypted, out var resolved))
                    {
                        targetPath = resolved;
                        compressed = true;
                    }
                    else
                    {
                        if (IsExcludedRepoPath(repoPath)) skippedNonSave++;
                        continue;
                    }

                    bool hasRemoteMtime = DateTime.TryParse(entry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime);
                    bool shouldDownload = !File.Exists(targetPath)
                        || (hasRemoteMtime && remoteMtime > File.GetLastWriteTimeUtc(targetPath));
                    if (shouldDownload)
                        toDownload.Add((repoPath, targetPath, compressed, hasRemoteMtime, remoteMtime, entry.SizeBytes));
                }
                CloudSyncLog.Write($"Download: {toDownload.Count:N0} cloud file(s) are new or newer than this PC's copy " +
                                   $"({Megabytes(toDownload.Sum(d => d.SizeBytes))})" +
                                   (skippedNonSave > 0 ? $"; {skippedNonSave:N0} skipped as non-save data (texture packs, BIOS and system files, caches)" : ""));

                done = 0;
                progress.Report(new SyncProgress(SyncPhase.Downloading, 0, toDownload.Count, uploaded, downloaded, errors));
                foreach (var d in toDownload)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        byte[]? data = await DownloadFileAsync(d.RepoPath, ct).ConfigureAwait(false);
                        if (data == null)
                            errors++;   // listed in the manifest but not fetched; DownloadFileAsync logged why
                        else if (data.Length > 0)
                        {
                            if (encrypted && encKey != null) data = Decrypt(data, encKey);
                            if (d.Compressed) data = GzipDecompress(data);
                            Directory.CreateDirectory(Path.GetDirectoryName(d.TargetPath)!);
                            File.WriteAllBytes(d.TargetPath, data);
                            // Stamp the manifest's mtime back onto the file — WriteAllBytes
                            // sets "now", which is newer than the manifest entry, so the NEXT
                            // full sync would see every save we just downloaded as locally
                            // modified and re-upload the lot (the "90 up with no changes" bug).
                            if (d.HasRemoteMtime) File.SetLastWriteTimeUtc(d.TargetPath, d.RemoteMtime);
                            downloaded++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        CloudSyncLog.Write($"Download failed {d.RepoPath}: {ex.Message}");
                    }
                    progress.Report(new SyncProgress(SyncPhase.Downloading, ++done, toDownload.Count, uploaded, downloaded, errors));
                }

                progress.Report(new SyncProgress(SyncPhase.Library, 0, 1, uploaded, downloaded, errors));
                // LIBRARY DB: VACUUM INTO for a consistent snapshot
                // (raw File.ReadAllBytes on a WAL-mode DB risks partial checkpoint reads).
                //
                // The db needs a THREE-WAY decision, not a mine-vs-remote compare. Two
                // machines' databases legitimately differ (play history, caches), so
                // "is my content different from remote?" is always yes and alternating
                // syncs ping-pong uploads forever. Instead each machine remembers the
                // hash it last synced at (local side-car file, NOT the shared manifest):
                //   - my db changed since last sync            → upload (last-writer-wins)
                //   - only remote changed                      → download and adopt it
                //   - neither changed                          → quiet
                // mtime is useless here in all cases: the sync's own VACUUM connection
                // checkpoints the WAL on close, rewriting library.db's mtime every sync.
                try
                {
                    string dbPath = Path.Combine(AppPaths.DataRoot, "library.db");
                    // Per-machine remote filename (library.<host>.db), kept from the shared
                    // repository so the layout matches the Windows app's. The LOCAL path is
                    // always library.db.
                    string dbRepoPath = DbRepoFileName + encSuffix;
                    string? lastSyncedHash = LoadLastSyncedDbHash();
                    string? myHash = null;

                    if (File.Exists(dbPath))
                    {
                        string tempDb = Path.Combine(Path.GetTempPath(), $"emutastic_sync_{Guid.NewGuid():N}.db");
                        try
                        {
                            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
                            {
                                conn.Open();
                                var cmd = conn.CreateCommand();
                                cmd.CommandText = $"VACUUM INTO '{tempDb.Replace("'", "''")}'";
                                cmd.ExecuteNonQuery();
                            }

                            var snapInfo = new FileInfo(tempDb);
                            byte[] dbBytes = File.ReadAllBytes(tempDb);
                            // Hash the PLAINTEXT snapshot — encryption uses a random IV,
                            // so ciphertext never compares equal even for identical content.
                            myHash = Convert.ToHexString(SHA256.HashData(dbBytes));

                            _manifestCache.Files.TryGetValue(dbRepoPath, out var dbEntry);
                            string? remoteHash = dbEntry?.Sha256;

                            bool localChanged = !string.Equals(myHash, lastSyncedHash,
                                StringComparison.OrdinalIgnoreCase);
                            // Upload when I changed (and remote doesn't already have my
                            // exact content), or to seed the hash on a legacy manifest
                            // entry written by a pre-hash build.
                            bool dbNeedsUpload =
                                (localChanged || string.IsNullOrEmpty(remoteHash))
                                && !string.Equals(myHash, remoteHash, StringComparison.OrdinalIgnoreCase);

                            if (dbNeedsUpload)
                            {
                                if (encrypted && encKey != null) dbBytes = Encrypt(dbBytes, encKey);
                                if (await UploadFileAsync(dbRepoPath, dbBytes, ct).ConfigureAwait(false))
                                {
                                    _manifestCache.Files[dbRepoPath] = new SyncFileEntry
                                    {
                                        LastModifiedUtc = DateTime.UtcNow.ToString("o"),
                                        SizeBytes = snapInfo.Length,
                                        Sha256 = myHash
                                    };
                                    SaveLastSyncedDbHash(myHash);
                                    lastSyncedHash = myHash;
                                    uploaded++;
                                    CloudSyncLog.Write("Database uploaded");
                                }
                                else errors++;
                            }
                            else if (!localChanged && string.Equals(myHash, remoteHash, StringComparison.OrdinalIgnoreCase)
                                     && !string.Equals(myHash, lastSyncedHash, StringComparison.OrdinalIgnoreCase))
                            {
                                // Remote already matches me but my side-car is stale
                                // (e.g. first run after updating) — just record it.
                                SaveLastSyncedDbHash(myHash);
                                lastSyncedHash = myHash;
                            }
                        }
                        finally
                        {
                            try { File.Delete(tempDb); } catch { }
                        }
                    }

                    // Download the remote DB when it changed and I didn't (restoring this
                    // PC's backup after a reinstall).
                    if (_manifestCache.Files.TryGetValue(dbRepoPath, out var remoteDbEntry)
                        && DateTime.TryParse(remoteDbEntry.LastModifiedUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var remoteDbMtime))
                    {
                        var localDbInfo = File.Exists(dbPath) ? new FileInfo(dbPath) : null;
                        string? remoteHash = remoteDbEntry.Sha256;

                        bool shouldDownload;
                        if (localDbInfo == null)
                        {
                            shouldDownload = true;
                        }
                        else if (!string.IsNullOrEmpty(remoteHash) && myHash != null)
                        {
                            bool localChanged = !string.Equals(myHash, lastSyncedHash,
                                StringComparison.OrdinalIgnoreCase);
                            // Adopt remote only when I have no local edits of my own and
                            // remote genuinely differs from me. If BOTH sides changed,
                            // the upload above already won (last-writer-wins) and the
                            // manifest now carries my hash, so this stays false.
                            shouldDownload = !localChanged
                                && !string.Equals(remoteHash, myHash, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            // Legacy manifest entry without a hash — old mtime rule.
                            shouldDownload = remoteDbMtime > localDbInfo.LastWriteTimeUtc;
                        }

                        if (shouldDownload)
                        {
                            byte[]? remoteDb = await DownloadFileAsync(dbRepoPath, ct).ConfigureAwait(false);
                            if (remoteDb != null && remoteDb.Length > 0)
                            {
                                if (encrypted && encKey != null) remoteDb = Decrypt(remoteDb, encKey);
                                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                                File.WriteAllBytes(dbPath, remoteDb);
                                // Same mtime-echo fix as the save download above.
                                File.SetLastWriteTimeUtc(dbPath, remoteDbMtime);
                                // Record what we adopted so the next sync sees "unchanged"
                                // (hash the bytes we wrote — covers legacy entries too).
                                SaveLastSyncedDbHash(Convert.ToHexString(SHA256.HashData(remoteDb)));
                                downloaded++;
                                CloudSyncLog.Write("Database downloaded from remote");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    CloudSyncLog.Write($"Database sync failed: {ex.Message}");
                    errors++;
                }

                progress.Report(new SyncProgress(SyncPhase.Finishing, 0, 1, uploaded, downloaded, errors));
                await SaveManifestAsync(ct).ConfigureAwait(false);
                return Finish(new SyncResult(uploaded, downloaded, errors));
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Full sync failed: {ex.Message}");
                return Finish(new SyncResult(uploaded, downloaded, errors + 1));
            }
            finally
            {
                CurrentProgress = null;
                try { SyncStateChanged?.Invoke(false); } catch { }
            }

            SyncResult Finish(SyncResult result)
            {
                LastResult = result;
                CloudSyncLog.Write($"Full sync: {result.Uploaded} up, {result.Downloaded} down, {result.Errors} errors " +
                                   $"in {(int)clock.Elapsed.TotalMinutes}m {clock.Elapsed.Seconds:00}s");
                return result;
            }
        }

        // ── Per-game hooks (called by GameHostLauncher / session end) ────────

        /// <summary>Pull the remote save before launch when it's newer than local
        /// (or local is missing). Bounded by the per-game lock + upstream's 5s wait.</summary>
        public async Task PullSaveBeforeLaunchAsync(Models.Game game, CancellationToken ct = default)
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (!IsAuthenticated || cfg is not { Enabled: true }) return;
            if (string.IsNullOrEmpty(game.RomHash) || string.IsNullOrEmpty(game.Console)) return;
            if (!TryGetPassphrase(cfg, out string? passphrase)) return;

            var gameLock = GetGameLock(game.RomHash);
            if (!await gameLock.WaitAsync(5000, ct).ConfigureAwait(false))
            {
                CloudSyncLog.Write("Lock timeout — skipping download, using local save");
                return;
            }
            try
            {
                bool encrypted = passphrase != null;
                string repoPath = RepoPathFor(game.Console!, game.RomHash) + (encrypted ? ".enc" : "");
                string localPath = LocalSrmPathFor(game.Console!, AppPaths.FromStoragePath(game.RomPath), game.HasPatch, game.RomHash);

                DateTime remoteMtime = default;
                bool hasRemoteMtime = _manifestCache.Files.TryGetValue(repoPath, out var mEntry)
                    && DateTime.TryParse(mEntry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out remoteMtime);
                // Newest-wins, no clobber: pull only when we have no local save yet, or the
                // remote is KNOWN to be strictly newer than ours. Mirrors FullSync's download
                // rule — never overwrite a local save that's newer or equal, and never
                // overwrite an existing local save when the remote mtime is unknown (a stale
                // or not-yet-loaded manifest must not clobber a fresh local save). Pulling the
                // other machine's newer save is the full sync's job (startup + periodic).
                bool shouldDownload = !File.Exists(localPath)
                    || (hasRemoteMtime && remoteMtime > File.GetLastWriteTimeUtc(localPath));

                byte[]? remote = shouldDownload ? await DownloadFileAsync(repoPath, ct, quietIfMissing: true).ConfigureAwait(false) : null;
                if (remote != null && remote.Length > 0)
                {
                    if (encrypted)
                    {
                        byte[] key = DeriveKey(passphrase!, _username ?? "");
                        remote = Decrypt(remote, key);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    File.WriteAllBytes(localPath, remote);
                    // Same mtime-echo fix as FullSync's download phase: without this the
                    // next full sync re-uploads a save we only ever downloaded.
                    if (hasRemoteMtime) File.SetLastWriteTimeUtc(localPath, remoteMtime);
                    CloudSyncLog.Write($"Downloaded remote save: {repoPath}");
                }
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Pre-launch download failed: {ex.Message}");
            }
            finally { gameLock.Release(); }
        }

        /// <summary>Upload the battery save after a session ends (fire-and-forget
        /// at the call site, like upstream's game-close hook).</summary>
        public async Task UploadSaveAfterSessionAsync(Models.Game game, CancellationToken ct = default)
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (!IsAuthenticated || cfg is not { Enabled: true }) return;
            if (cfg.SyncTiming == "manual") return;
            if (string.IsNullOrEmpty(game.RomHash) || string.IsNullOrEmpty(game.Console)) return;

            string localPath = LocalSrmPathFor(game.Console!, AppPaths.FromStoragePath(game.RomPath), game.HasPatch, game.RomHash);
            if (!File.Exists(localPath)) return;
            if (!TryGetPassphrase(cfg, out string? passphrase)) return;

            try
            {
                bool encrypted = passphrase != null;
                string repoPath = RepoPathFor(game.Console!, game.RomHash) + (encrypted ? ".enc" : "");

                // Newest-wins, no clobber: don't replace a newer (or equal) remote save with
                // our older local one — e.g. the game was opened and closed without writing a
                // save while the other OS had already uploaded newer progress. Mirrors the
                // FullSync / console-managed upload rule. (After actually playing, the local
                // .srm mtime is "now" and wins; a launch-without-save keeps the remote mtime
                // it was stamped with on pull, so this correctly no-ops.)
                if (_manifestCache.Files.TryGetValue(repoPath, out var existing)
                    && DateTime.TryParse(existing.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime)
                    && remoteMtime >= File.GetLastWriteTimeUtc(localPath))
                {
                    CloudSyncLog.Write($"Skipped save upload (remote is newer/same): {repoPath}");
                    return;
                }

                byte[] srmBytes = File.ReadAllBytes(localPath);
                if (encrypted)
                {
                    byte[] key = DeriveKey(passphrase!, _username ?? "");
                    srmBytes = Encrypt(srmBytes, key);
                }
                if (await UploadFileAsync(repoPath, srmBytes, ct).ConfigureAwait(false))
                {
                    _manifestCache.Files[repoPath] = new SyncFileEntry
                    {
                        LastModifiedUtc = File.GetLastWriteTimeUtc(localPath).ToString("o"),
                        SizeBytes = new FileInfo(localPath).Length
                    };
                    await SaveManifestAsync(ct).ConfigureAwait(false);
                    CloudSyncLog.Write($"Uploaded save: {repoPath}");
                }
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Post-session upload failed: {ex.Message}");
            }
        }

        // ── Console-managed save hooks (memory cards / save trees) ───────────
        // The .srm hooks above only cover frontend SRAM. These cover the cards/trees
        // cores write themselves (PS2/PSP/GameCube/Dreamcast/3DS/Saturn/DS, …), which
        // have no per-game .srm at all.

        /// <summary>
        /// Uploads this console's changed console-managed saves (memory cards, save
        /// trees). Per-console counterpart to <see cref="FullSyncAsync"/>, called on
        /// game close alongside the .srm upload. Fire-and-forget safe.
        /// </summary>
        public async Task<int> UploadConsoleExtraSavesAsync(string console, CancellationToken ct = default)
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (!IsAuthenticated || cfg is not { Enabled: true }) return 0;
            if (cfg.SyncTiming == "manual" || string.IsNullOrEmpty(console)) return 0;
            if (!TryGetPassphrase(cfg, out string? passphrase)) return 0;

            bool encrypted = passphrase != null;
            byte[]? key = encrypted ? DeriveKey(passphrase!, _username ?? "") : null;
            string encSuffix = encrypted ? ".enc" : "";
            string prefix = $"BatterySaves/{console}/";

            int n = 0;
            foreach (var local in BuildExtraSaveMap())
            {
                if (ct.IsCancellationRequested) break;
                if (!local.RepoPath.StartsWith(prefix, StringComparison.Ordinal)) continue;

                string repoPath = local.RepoPath + encSuffix;
                if (_manifestCache.Files.TryGetValue(repoPath, out var entry)
                    && DateTime.TryParse(entry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var rm)
                    && local.LastModifiedUtc <= rm)
                    continue;

                try
                {
                    byte[] bytes = GzipCompress(File.ReadAllBytes(local.LocalPath));
                    if (encrypted && key != null) bytes = Encrypt(bytes, key);
                    if (await UploadFileAsync(repoPath, bytes, ct).ConfigureAwait(false))
                    {
                        _manifestCache.Files[repoPath] = new SyncFileEntry
                        {
                            LastModifiedUtc = local.LastModifiedUtc.ToString("o"),
                            SizeBytes = local.SizeBytes
                        };
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    CloudSyncLog.Write($"Extra-save upload failed {repoPath}: {ex.Message}");
                }
            }
            if (n > 0)
            {
                await SaveManifestAsync(ct).ConfigureAwait(false);
                CloudSyncLog.Write($"Uploaded {n} {console} memory-card/save file(s)");
            }
            return n;
        }

        /// <summary>
        /// Downloads this console's console-managed saves that are newer remotely than
        /// local (or missing locally). MUST complete before the core boots, since cores
        /// read memory cards / save trees from disk at init. Called on game launch — a
        /// fast no-op once the startup background sync has already pulled them.
        /// </summary>
        public async Task<int> DownloadConsoleExtraSavesAsync(string console, CancellationToken ct = default)
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (!IsAuthenticated || cfg is not { Enabled: true } || string.IsNullOrEmpty(console)) return 0;
            if (!TryGetPassphrase(cfg, out string? passphrase)) return 0;

            bool encrypted = passphrase != null;
            byte[]? key = encrypted ? DeriveKey(passphrase!, _username ?? "") : null;
            string prefix = $"BatterySaves/{console}/";

            int n = 0;
            foreach (var (repoPath, entry) in _manifestCache.Files)
            {
                if (ct.IsCancellationRequested) break;
                if (!repoPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!TryResolveExtraSaveLocalPath(repoPath, encrypted, out var targetPath)) continue;

                bool hasMtime = DateTime.TryParse(entry.LastModifiedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime);
                bool shouldDownload = !File.Exists(targetPath)
                    || (hasMtime && remoteMtime > File.GetLastWriteTimeUtc(targetPath));
                if (!shouldDownload) continue;

                try
                {
                    byte[]? data = await DownloadFileAsync(repoPath, ct).ConfigureAwait(false);
                    if (data != null && data.Length > 0)
                    {
                        if (encrypted && key != null) data = Decrypt(data, key);
                        data = GzipDecompress(data);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        File.WriteAllBytes(targetPath, data);
                        if (hasMtime) File.SetLastWriteTimeUtc(targetPath, remoteMtime);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    CloudSyncLog.Write($"Extra-save download failed {repoPath}: {ex.Message}");
                }
            }
            if (n > 0) CloudSyncLog.Write($"Downloaded {n} {console} memory-card/save file(s)");
            return n;
        }

        // ── Background sync (app startup + initial login) ────────────────────
        // Runs a full sync OFF the UI thread so saves are already local by the time a
        // game launches — the per-game launch hook then just does a quick local check
        // instead of a multi-MB download. Fires SyncStateChanged so the main window can
        // show a "Syncing saves…" banner.

        /// <summary>True while a full sync is in flight (background or Sync Now).</summary>
        public bool IsSyncing => _fullSync is { IsCompleted: false };

        /// <summary>Raised with true when a full sync starts and false when it ends (after
        /// <see cref="LastResult"/> is set).</summary>
        public event Action<bool>? SyncStateChanged;

        /// <summary>
        /// Kicks off a full sync on a background thread (no-op if one is already running
        /// or the user isn't signed in). Called at app startup and right after device-flow
        /// login completes. Pass a FRESH DatabaseService so the background thread never
        /// shares the UI's connection.
        /// </summary>
        public void StartBackgroundSync(DatabaseService db)
        {
            if (!IsAuthenticated) { CloudSyncLog.Write("Background sync skipped: not signed in"); return; }
            if (App.Configuration?.GetCloudSyncConfiguration() is not { Enabled: true })
            {
                CloudSyncLog.Write("Background sync skipped: sync is turned off");
                return;
            }
            if (IsSyncing) { CloudSyncLog.Write("Background sync skipped: a sync is already running"); return; }
            CloudSyncLog.Write("Background sync starting");
            _ = FullSyncAsync(db);   // reports its own progress, result and failures
        }

        /// <summary>
        /// Ensures this console's memory cards / save trees are on disk before the core
        /// boots. Prefers letting the in-flight background sync finish (bounded so a
        /// stalled sync never hangs launch) over starting a competing download; then does
        /// a targeted per-console pull, a fast no-op once the background sync fetched them.
        /// </summary>
        public async Task EnsureConsoleSavesReadyAsync(string console, CancellationToken ct = default)
        {
            if (!IsAuthenticated || string.IsNullOrEmpty(console)) return;

            var bg = _fullSync;
            if (bg is { IsCompleted: false })
            {
                try { await bg.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
                catch { /* timeout or fault — fall through to a targeted pull */ }
            }
            await DownloadConsoleExtraSavesAsync(console, ct).ConfigureAwait(false);
        }
    }
}
