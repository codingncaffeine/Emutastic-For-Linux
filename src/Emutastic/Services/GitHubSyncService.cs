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
    /// GitHub Cloud Sync (port of upstream GitHubSyncService): battery saves +
    /// library.db synced through a private "emutastic-saves" repo via the
    /// Contents API. OAuth device flow (no client secret), manifest-based
    /// last-write-wins, optional AES-256-GCM with a PBKDF2-derived key.
    ///
    /// Linux deltas from upstream:
    ///  - DPAPI (ProtectedData) doesn't exist here; Protect/UnprotectString are
    ///    pass-throughs. The token/passphrase rest in config.json with the same
    ///    protection level as the RA token and ScreenScraper password already do.
    ///  - Local battery saves live at Saves/&lt;romstem&gt;.srm (the session's
    ///    RetroArch-style scheme), not upstream's BatterySaves/&lt;Console&gt;/ tree.
    ///    The REPO layout keeps upstream's hash-keyed convention so Windows and
    ///    Linux installs can share one repo.
    /// </summary>
    public sealed class GitHubSyncService
    {
        public static GitHubSyncService Instance { get; } = new();

        private const string SharedRepoName = "emutastic-saves";
        private const string ApiBase = "https://api.github.com";
        private static string ClientId => Emutastic.Secrets.GitHubOAuthClientId;

        // Active repo: the shared one by default, or this machine's own when
        // the per-PC toggle is on. Read from config on every access so a
        // toggle flip takes effect on the very next operation.
        private static string RepoName =>
            App.Configuration?.GetCloudSyncConfiguration() is { UsePerPcRepo: true }
                ? PerPcRepoName
                : SharedRepoName;

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
        /// The library.db filename THIS machine reads/writes in the sync repo.
        /// Namespaced per machine so several OSes/boxes can share ONE repo without
        /// ever clobbering each other's library: library.db is non-portable anyway
        /// (it stores absolute, OS-specific ROM paths and back-/forward-slash art
        /// paths), so each machine keeps its own. Game saves stay SHARED — they're
        /// keyed by ROM hash and synced as an additive union, untouched by this.
        /// </summary>
        public static string DbRepoFileName { get; } = $"library.{MachineSuffix}.db";

        /// <summary>
        /// Drops every piece of state bound to the previous repo (sha cache,
        /// manifest). Call when the per-PC toggle flips so the next sync
        /// starts clean against the newly selected repo. The db side-car is
        /// per-repo by filename and needs no reset.
        /// </summary>
        public void ResetRepoBinding()
        {
            _shaCache.Clear();
            _manifestCache = new SyncManifest();
        }

        private static readonly HttpClient Http = CreateHttp();
        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic-CloudSync");
            return http;
        }

        private volatile string? _token;
        private string? _username;
        private readonly ConcurrentDictionary<string, string> _shaCache = new();
        private SyncManifest _manifestCache = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gameLocks = new();

        public bool IsAuthenticated => !string.IsNullOrEmpty(_token);
        public bool IsConfigured => !string.IsNullOrEmpty(ClientId);
        public string? Username => _username;
        public SyncManifest ManifestCache => _manifestCache;

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
                    SaveTokenToConfig();
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
            _shaCache.Clear();
            _manifestCache = new SyncManifest();
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg != null && App.Configuration != null)
            {
                cfg.GitHubTokenProtected = "";
                cfg.GitHubUsername = "";
                cfg.Enabled = false;
                App.Configuration.SetCloudSyncConfiguration(cfg);
                App.Configuration.ScheduleSave();
            }
        }

        public void LoadFromConfig()
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null) return;
            string token = UnprotectString(cfg.GitHubTokenProtected);
            if (!string.IsNullOrEmpty(token))
            {
                _token = token;
                _username = string.IsNullOrEmpty(cfg.GitHubUsername) ? null : cfg.GitHubUsername;
            }
        }

        private void SaveTokenToConfig()
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null || App.Configuration == null) return;
            cfg.GitHubTokenProtected = ProtectString(_token ?? "");
            cfg.GitHubUsername = _username ?? "";
            cfg.Enabled = true;
            App.Configuration.SetCloudSyncConfiguration(cfg);
            App.Configuration.ScheduleSave();
        }

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

                CloudSyncLog.Write($"Upload failed {repoPath}: {resp.StatusCode}");
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

                CloudSyncLog.Write($"Delete failed {repoPath}: {resp.StatusCode}");
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

        public async Task<byte[]?> DownloadFileAsync(string repoPath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return null;
            try
            {
                using var req = AuthedRequest(HttpMethod.Get, $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string base64 = (root.GetProperty("content").GetString() ?? "")
                    .Replace("\n", "").Replace("\r", "");
                if (root.TryGetProperty("sha", out var shaProp))
                    _shaCache[repoPath] = shaProp.GetString() ?? "";
                return Convert.FromBase64String(base64);
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Download exception {repoPath}: {ex.Message}");
                return null;
            }
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
        // Upstream used Windows DPAPI (ProtectedData, CurrentUser scope). Linux
        // has no OS-blessed equivalent without a keyring dependency; the values
        // rest in config.json like this port's other credentials (RA token,
        // ScreenScraper password). The names survive so ported call sites and
        // the config field names stay upstream-identical.

        public static string ProtectString(string plaintext) => plaintext ?? "";
        public static string UnprotectString(string protectedValue) => protectedValue ?? "";

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
                bool encrypted = cfg is { EncryptionEnabled: true }
                    && !string.IsNullOrEmpty(cfg.PassphraseProtected);
                string path = encrypted ? "manifest.json.enc" : "manifest.json";

                byte[]? data = await DownloadFileAsync(path, ct).ConfigureAwait(false);
                if (data == null || data.Length == 0) { _manifestCache = new SyncManifest(); return; }

                if (encrypted)
                {
                    byte[] key = DeriveKey(UnprotectString(cfg!.PassphraseProtected), _username ?? "");
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
                bool encrypted = cfg is { EncryptionEnabled: true }
                    && !string.IsNullOrEmpty(cfg.PassphraseProtected);
                if (encrypted)
                {
                    byte[] key = DeriveKey(UnprotectString(cfg!.PassphraseProtected), _username ?? "");
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

        // Keyed by repo name so flipping the per-PC toggle back and forth
        // keeps an accurate "what did I last sync HERE" per repository.
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
        // upstream's hash-keyed layout (BatterySaves/<Console>/<RomHash>.srm) so one
        // repo serves Windows and Linux installs alike.
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
        // Repo paths keep upstream's "BatterySaves/<Console>/<rel>" convention so a
        // Windows and a Linux install share one repo (local "Saves/" ↔ repo "BatterySaves/").

        private static readonly HashSet<string> UnsupportedSaveConsoles =
            new(StringComparer.OrdinalIgnoreCase) { "DOS" };

        private static bool IsUnsupportedConsole(string console)
            => UnsupportedSaveConsoles.Contains(console);

        // Path segments that are never battery saves: emulator caches, shader caches,
        // save-states (synced separately), dumps, logs, screenshots.
        private static readonly HashSet<string> ExcludedSaveSegments =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Cache", "Shaders", "ShaderCache", "StateSaves", "PPSSPP_STATE",
                "Dump", "Logs", "ScreenShots", "Screenshots", "Triforce", "WFS"
            };

        private static bool IsExcludedSavePath(string rel)
        {
            if (rel.EndsWith(".srm", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var seg in rel.Split('/', '\\'))
                if (ExcludedSaveSegments.Contains(seg)) return true;
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

        private readonly SemaphoreSlim _fullSyncLock = new(1, 1);

        public async Task<SyncResult> FullSyncAsync(DatabaseService db, CancellationToken ct = default)
        {
            if (!IsAuthenticated) return new SyncResult(0, 0, 0);
            if (!await _fullSyncLock.WaitAsync(0, ct).ConfigureAwait(false))
                return new SyncResult(0, 0, 0);   // a sync is already running

            int uploaded = 0, downloaded = 0, errors = 0;
            try
            {
                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                bool encrypted = cfg is { EncryptionEnabled: true }
                    && !string.IsNullOrEmpty(cfg.PassphraseProtected);
                byte[]? encKey = encrypted
                    ? DeriveKey(UnprotectString(cfg!.PassphraseProtected), _username ?? "")
                    : null;
                string encSuffix = encrypted ? ".enc" : "";

                await RefreshShaCacheAsync(ct).ConfigureAwait(false);
                await LoadManifestAsync(ct).ConfigureAwait(false);

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
                foreach (var local in localSaves)
                {
                    if (ct.IsCancellationRequested) break;
                    string repoPath = local.RepoPath + encSuffix;
                    bool shouldUpload = true;
                    if (_manifestCache.Files.TryGetValue(repoPath, out var entry)
                        && DateTime.TryParse(entry.LastModifiedUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime))
                        shouldUpload = local.LastModifiedUtc > remoteMtime;

                    if (!shouldUpload) continue;
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
                        else errors++;
                    }
                    catch { errors++; }
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
                // saves — including ones never seen on this PC (second-machine restore).
                foreach (var (repoPath, entry) in _manifestCache.Files)
                {
                    if (ct.IsCancellationRequested) break;
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
                    else continue;

                    bool hasRemoteMtime = DateTime.TryParse(entry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime);
                    bool shouldDownload = !File.Exists(targetPath)
                        || (hasRemoteMtime && remoteMtime > File.GetLastWriteTimeUtc(targetPath));
                    if (!shouldDownload) continue;
                    try
                    {
                        byte[]? data = await DownloadFileAsync(repoPath, ct).ConfigureAwait(false);
                        if (data != null && data.Length > 0)
                        {
                            if (encrypted && encKey != null) data = Decrypt(data, encKey);
                            if (compressed) data = GzipDecompress(data);
                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                            File.WriteAllBytes(targetPath, data);
                            // Stamp the manifest's mtime back onto the file — WriteAllBytes
                            // sets "now", which is newer than the manifest entry, so the NEXT
                            // full sync would see every save we just downloaded as locally
                            // modified and re-upload the lot (the "90 up with no changes" bug).
                            if (hasRemoteMtime) File.SetLastWriteTimeUtc(targetPath, remoteMtime);
                            downloaded++;
                        }
                    }
                    catch { errors++; }
                }

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
                    // Per-machine remote filename (library.<host>.db): each OS/box owns
                    // its own DB in the shared repo, so last-writer-wins can never clobber
                    // another machine's library. The LOCAL path is always library.db.
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

                    // Download the remote DB when it changed and I didn't (second-PC
                    // restore + continuous adoption of the other machine's db).
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

                await SaveManifestAsync(ct).ConfigureAwait(false);
                CloudSyncLog.Write($"Full sync: {uploaded} up, {downloaded} down, {errors} errors");
                return new SyncResult(uploaded, downloaded, errors);
            }
            finally { _fullSyncLock.Release(); }
        }

        // ── Per-game hooks (called by GameHostLauncher / session end) ────────

        /// <summary>Pull the remote save before launch when it's newer than local
        /// (or local is missing). Bounded by the per-game lock + upstream's 5s wait.</summary>
        public async Task PullSaveBeforeLaunchAsync(Models.Game game, CancellationToken ct = default)
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (!IsAuthenticated || cfg is not { Enabled: true }) return;
            if (string.IsNullOrEmpty(game.RomHash) || string.IsNullOrEmpty(game.Console)) return;

            var gameLock = GetGameLock(game.RomHash);
            if (!await gameLock.WaitAsync(5000, ct).ConfigureAwait(false))
            {
                CloudSyncLog.Write("Lock timeout — skipping download, using local save");
                return;
            }
            try
            {
                bool encrypted = cfg.EncryptionEnabled && !string.IsNullOrEmpty(cfg.PassphraseProtected);
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

                byte[]? remote = shouldDownload ? await DownloadFileAsync(repoPath, ct).ConfigureAwait(false) : null;
                if (remote != null && remote.Length > 0)
                {
                    if (encrypted)
                    {
                        byte[] key = DeriveKey(UnprotectString(cfg.PassphraseProtected), _username ?? "");
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

            try
            {
                bool encrypted = cfg.EncryptionEnabled && !string.IsNullOrEmpty(cfg.PassphraseProtected);
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
                    byte[] key = DeriveKey(UnprotectString(cfg.PassphraseProtected), _username ?? "");
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

            bool encrypted = cfg.EncryptionEnabled && !string.IsNullOrEmpty(cfg.PassphraseProtected);
            byte[]? key = encrypted
                ? DeriveKey(UnprotectString(cfg.PassphraseProtected), _username ?? "") : null;
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

            bool encrypted = cfg.EncryptionEnabled && !string.IsNullOrEmpty(cfg.PassphraseProtected);
            byte[]? key = encrypted
                ? DeriveKey(UnprotectString(cfg.PassphraseProtected), _username ?? "") : null;
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

        private Task? _backgroundSync;

        /// <summary>True while a background full-sync is in flight.</summary>
        public bool IsSyncing => _backgroundSync is { IsCompleted: false };

        /// <summary>Raised with true when a background sync starts, false when it ends.</summary>
        public event Action<bool>? SyncStateChanged;

        /// <summary>
        /// Kicks off a full sync on a background thread (no-op if one is already running
        /// or the user isn't signed in). Called at app startup and right after device-flow
        /// login completes. Pass a FRESH DatabaseService so the background thread never
        /// shares the UI's connection.
        /// </summary>
        public void StartBackgroundSync(DatabaseService db)
        {
            if (!IsAuthenticated) return;
            if (App.Configuration?.GetCloudSyncConfiguration() is not { Enabled: true }) return;
            if (_backgroundSync is { IsCompleted: false }) return;

            SyncStateChanged?.Invoke(true);
            _backgroundSync = Task.Run(async () =>
            {
                try { await FullSyncAsync(db).ConfigureAwait(false); }
                catch (Exception ex) { CloudSyncLog.Write($"Background sync failed: {ex.Message}"); }
                finally { SyncStateChanged?.Invoke(false); }
            });
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

            var bg = _backgroundSync;
            if (bg is { IsCompleted: false })
            {
                try { await bg.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
                catch { /* timeout or fault — fall through to a targeted pull */ }
            }
            await DownloadConsoleExtraSavesAsync(console, ct).ConfigureAwait(false);
        }
    }
}
