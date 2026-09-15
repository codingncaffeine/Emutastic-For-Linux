using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    /// <summary>
    /// `Emutastic --selftest-cloudsync-offline --portable`: a whole two-way sync against an
    /// in-process fake of the GitHub Contents API, so the sync engine runs end to end with no
    /// account, no network and no risk to a real repository. Run it from a copy of the build
    /// output whose PortableData has never been used. Checks that every PC gets its own
    /// repository (created before the first upload), that saves go up and come down intact,
    /// that texture packs, BIOS files and console system files stay out in both directions, that files over 1 MB download through the
    /// raw fallback, that progress reports carry the right totals, that a second FullSyncAsync
    /// joins the running one, that a repeat sync transfers no saves, and that the log narrates it.
    /// Exit 0 = pass, 1 = a check failed, 2 = incomplete.
    /// </summary>
    internal static class CloudSyncOfflineSelfTest
    {
        private const string RepoName = "emutastic-saves-offline";

        public static int Run() => RunAsync().GetAwaiter().GetResult();

        private static async Task<int> RunAsync()
        {
            int failures = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
                if (!ok) failures++;
            }

            Console.WriteLine("=== cloud sync offline self-test ===");
            string root = AppPaths.DataRoot;
            if (!AppPaths.IsPortable
                || File.Exists(Path.Combine(root, "config.json"))
                || File.Exists(Path.Combine(root, "library.db"))
                || Directory.Exists(Path.Combine(root, "Saves")))
            {
                Console.WriteLine($"  [SKIP] needs --portable and an unused PortableData ({root}); run a fresh copy of the build output");
                Console.WriteLine("=== INCOMPLETE ===");
                return 2;
            }

            var config = new JsonConfigurationService();
            await config.LoadAsync();
            var cloud = config.GetCloudSyncConfiguration();
            cloud.Enabled = true;
            cloud.UsePerPcRepo = false;   // the Windows toggle; must not matter here
            config.SetCloudSyncConfiguration(cloud);
            App.Configuration = config;

            Console.WriteLine("--- one repository per PC");
            Check(GitHubSyncService.EffectiveRepoName == GitHubSyncService.PerPcRepoName
                  && GitHubSyncService.PerPcRepoName.StartsWith("emutastic-saves-", StringComparison.Ordinal)
                  && GitHubSyncService.PerPcRepoName.Length > "emutastic-saves-".Length,
                  $"this PC syncs to its own repository even with the Windows toggle off ({GitHubSyncService.EffectiveRepoName})");

            using var fake = new FakeGitHub();
            GitHubSyncService.ApiBase = fake.BaseUrl;
            GitHubSyncService.RepoNameOverride = RepoName;
            var svc = GitHubSyncService.Instance;
            typeof(GitHubSyncService).GetField("_token", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(svc, "offline-token");
            Check(await svc.ValidateTokenAsync() && svc.Username == "tester", "signed in against the fake API");

            // Cloud side: two saves (one over 1 MB, which GitHub does not inline), a texture pack, a
            // GameCube BIOS copy and a 3DS system archive.
            DateTime cloudTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            byte[] smallSave = RandomNumberGenerator.GetBytes(4096);
            byte[] bigSave = RandomNumberGenerator.GetBytes(1_500_000);   // random, so it stays over 1 MB gzipped
            byte[] cloudTexture = RandomNumberGenerator.GetBytes(2048);
            const string SmallPath = "BatterySaves/PSP/PSP/SAVEDATA/ULUS00001/DATA.BIN";
            const string BigPath = "BatterySaves/PSP/PSP/SAVEDATA/ULUS00002/BIG.BIN";
            const string CloudTexturePath = "BatterySaves/PSP/PSP/TEXTURES/ULUS00001/tex.png";
            const string CloudBiosPath = "BatterySaves/GameCube/User/GC/EUR/IPL.bin";
            const string CloudSystemPath = "BatterySaves/3DS/Azahar/nand/title/0004009b/00010202/content/00000000.app";
            byte[] cloudBios = RandomNumberGenerator.GetBytes(2048);
            byte[] cloudSystem = RandomNumberGenerator.GetBytes(2048);
            fake.Seed(SmallPath, Gzip(smallSave));
            fake.Seed(BigPath, Gzip(bigSave));
            fake.Seed(CloudTexturePath, Gzip(cloudTexture));
            fake.Seed(CloudBiosPath, Gzip(cloudBios));
            fake.Seed(CloudSystemPath, Gzip(cloudSystem));
            fake.SeedManifest(new Dictionary<string, (DateTime, long)>
            {
                [SmallPath] = (cloudTime, smallSave.Length),
                [BigPath] = (cloudTime, bigSave.Length),
                [CloudTexturePath] = (cloudTime, cloudTexture.Length),
                [CloudBiosPath] = (cloudTime, cloudBios.Length),
                [CloudSystemPath] = (cloudTime, cloudSystem.Length),
            });

            // This PC: one save of its own and a texture pack.
            string saves = AppPaths.GetFolder("Saves");
            string localSave = Path.Combine(saves, "PSP", "PSP", "SAVEDATA", "ULUS00003", "LOCAL.BIN");
            string localTexture = Path.Combine(saves, "PSP", "PSP", "TEXTURES", "ULUS00003", "local.png");
            Directory.CreateDirectory(Path.GetDirectoryName(localSave)!);
            File.WriteAllBytes(localSave, RandomNumberGenerator.GetBytes(3000));
            Directory.CreateDirectory(Path.GetDirectoryName(localTexture)!);
            File.WriteAllBytes(localTexture, RandomNumberGenerator.GetBytes(3000));
            // GameCube: a memory card (a save, inside the allowlisted User/GC) beside a BIOS copy.
            string localCard = Path.Combine(saves, "GameCube", "User", "GC", "USA", "Card A", "01-TEST-save.gci");
            string localBios = Path.Combine(saves, "GameCube", "User", "GC", "USA", "IPL.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(localCard)!);
            File.WriteAllBytes(localCard, RandomNumberGenerator.GetBytes(3000));
            File.WriteAllBytes(localBios, RandomNumberGenerator.GetBytes(3000));
            // A PlayStation BIOS in a console folder that syncs whole, and Azahar's NAND and SD card:
            // a system archive and its ticket stay out, while a game save under title/.../data and
            // the system settings under nand/data are saves.
            const string Id = "00000000000000000000000000000000";
            string localPs1Bios = Path.Combine(saves, "PS1", "scph5501.bin");
            string local3dsSystem = Path.Combine(saves, "3DS", "Azahar", "nand", "title", "0004009b", "00014002", "content", "00000000.app");
            string local3dsTicket = Path.Combine(saves, "3DS", "Azahar", "nand", "dbs", "ticket.db", "0004009B00014002.0000000000000000.tik");
            string local3dsSave = Path.Combine(saves, "3DS", "Azahar", "sdmc", "Nintendo 3DS", Id, Id, "title", "00040000", "00012300", "data", "00000001", "main");
            string local3dsSettings = Path.Combine(saves, "3DS", "Azahar", "nand", "data", Id, "sysdata", "00010017", "00000000", "config");
            foreach (string file in new[] { localPs1Bios, local3dsSystem, local3dsTicket, local3dsSave, local3dsSettings })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(3000));
            }

            var reports = new ConcurrentQueue<GitHubSyncService.SyncProgress>();
            var states = new ConcurrentQueue<bool>();
            svc.SyncProgressChanged += reports.Enqueue;
            svc.SyncStateChanged += states.Enqueue;

            Console.WriteLine("--- first sync");
            var first = svc.FullSyncAsync(new DatabaseService());
            var joined = svc.FullSyncAsync(new DatabaseService());
            Check(ReferenceEquals(first, joined), "a second FullSyncAsync while one runs joins it instead of returning an empty result");
            Check(svc.IsSyncing, "IsSyncing is true while it runs");
            var result = await first;

            Check(fake.RepoCreated && fake.RejectedBeforeCreate == 0, "this PC's repository was created before anything was uploaded");
            Check(result == new GitHubSyncService.SyncResult(5, 2, 0),
                  $"5 up (four saves and the library), 2 down, 0 errors — got {result.Uploaded} up, {result.Downloaded} down, {result.Errors} errors");
            Check(fake.Has("BatterySaves/PSP/PSP/SAVEDATA/ULUS00003/LOCAL.BIN"), "this PC's save was uploaded");
            Check(!fake.PutPaths.Any(p => p.Contains("/TEXTURES/")), "no texture pack was uploaded");
            Check(fake.Has("BatterySaves/GameCube/User/GC/USA/Card A/01-TEST-save.gci"), "a GameCube memory card was uploaded");
            Check(!fake.PutPaths.Any(p => p.EndsWith("/IPL.bin", StringComparison.OrdinalIgnoreCase)), "no GameCube BIOS copy was uploaded");
            Check(!fake.PutPaths.Any(p => p.EndsWith("/scph5501.bin", StringComparison.OrdinalIgnoreCase)), "no PlayStation BIOS was uploaded");
            Check(fake.Has($"BatterySaves/3DS/Azahar/sdmc/Nintendo 3DS/{Id}/{Id}/title/00040000/00012300/data/00000001/main")
                  && fake.Has($"BatterySaves/3DS/Azahar/nand/data/{Id}/sysdata/00010017/00000000/config"),
                  "a 3DS game save and the 3DS system settings were uploaded");
            Check(!fake.PutPaths.Any(p => p.Contains("/content/") || p.Contains("/nand/dbs/")), "no 3DS system archive or ticket was uploaded");
            string downloadedSmall = Path.Combine(saves, "PSP", "PSP", "SAVEDATA", "ULUS00001", "DATA.BIN");
            string downloadedBig = Path.Combine(saves, "PSP", "PSP", "SAVEDATA", "ULUS00002", "BIG.BIN");
            Check(File.Exists(downloadedSmall) && File.ReadAllBytes(downloadedSmall).SequenceEqual(smallSave), "a small cloud save came down intact");
            Check(File.Exists(downloadedBig) && File.ReadAllBytes(downloadedBig).SequenceEqual(bigSave), "a cloud save over 1 MB came down intact");
            Check(fake.RawRequests.Contains(BigPath), "…fetched raw, because the JSON response carried no content");
            Check(!File.Exists(Path.Combine(saves, "PSP", "PSP", "TEXTURES", "ULUS00001", "tex.png")), "the cloud texture pack was not downloaded");
            Check(!File.Exists(Path.Combine(saves, "GameCube", "User", "GC", "EUR", "IPL.bin")), "the cloud BIOS copy was not downloaded");
            Check(!File.Exists(Path.Combine(saves, "3DS", "Azahar", "nand", "title", "0004009b", "00010202", "content", "00000000.app")),
                  "the cloud 3DS system archive was not downloaded");
            Check(File.Exists(downloadedSmall) && File.GetLastWriteTimeUtc(downloadedSmall) == cloudTime, "a downloaded save carries the cloud's modified time");
            Check(fake.ManifestKeys().Contains("BatterySaves/PSP/PSP/SAVEDATA/ULUS00003/LOCAL.BIN"), "the manifest was saved with the upload in it");

            var list = reports.ToList();
            var up = list.Where(p => p.Phase == GitHubSyncService.SyncPhase.Uploading).ToList();
            var down = list.Where(p => p.Phase == GitHubSyncService.SyncPhase.Downloading).ToList();
            Check(list.Count > 0 && list[0].Phase == GitHubSyncService.SyncPhase.Checking, "progress starts in the checking phase");
            Check(up.Count > 0 && up.All(p => p.Total == 4) && up[0].Done == 0 && up[^1].Done == 4,
                  $"upload progress runs 0 → 4 of 4 ({up.Count} report(s))");
            Check(down.Count > 0 && down.All(p => p.Total == 2) && down[0].Done == 0 && down[^1].Done == 2,
                  $"download progress runs 0 → 2 of 2 ({down.Count} report(s))");
            Check(down.Zip(down.Skip(1)).All(z => z.Second.Done >= z.First.Done), "download progress never goes backwards");
            Check(list.Any(p => p.Phase == GitHubSyncService.SyncPhase.Library) && list.Any(p => p.Phase == GitHubSyncService.SyncPhase.Finishing),
                  "the library and manifest steps report too");
            Check(states.SequenceEqual(new[] { true, false }), $"SyncStateChanged fired true, then false ({string.Join(", ", states)})");
            Check(svc.LastResult == result && svc.CurrentProgress == null && !svc.IsSyncing, "afterwards LastResult is set and nothing reports as running");

            string logPath = Path.Combine(AppPaths.GetFolder("Logs"), "cloudsync.log");
            string log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
            Check(log.Contains($"Full sync started: tester/{RepoName}"), "the log records the start and the repository");
            Check(log.Contains("Upload: 4 of 4 local save file(s)"), "the log records the upload plan");
            Check(log.Contains("Download: 2 cloud file(s)") && log.Contains("3 skipped as non-save data"),
                  "the log records the download plan and the skipped texture pack, BIOS copy and 3DS system archive");
            Check(log.Contains("Full sync: 5 up, 2 down, 0 errors"), "the log records the result");

            Console.WriteLine("--- repeat sync");
            int putsBefore = fake.PutPaths.Count(p => p.StartsWith("BatterySaves/", StringComparison.Ordinal));
            var again = await svc.FullSyncAsync(new DatabaseService());
            int putsAfter = fake.PutPaths.Count(p => p.StartsWith("BatterySaves/", StringComparison.Ordinal));
            Check(again.Downloaded == 0 && again.Errors == 0 && putsAfter == putsBefore,
                  $"nothing changed, so no save moves either way (got {again.Downloaded} down, {putsAfter - putsBefore} save upload(s), {again.Errors} errors)");

            Console.WriteLine(failures == 0 ? "=== PASS ===" : $"=== FAIL ({failures} check(s)) ===");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] Gzip(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        /// <summary>The handful of GitHub endpoints the sync engine calls, in memory. The repository
        /// starts out missing, like a PC's repository before its first sync.</summary>
        private sealed class FakeGitHub : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly ConcurrentDictionary<string, byte[]> _files = new();
            private readonly CancellationTokenSource _stop = new();
            private volatile bool _repoCreated;
            private int _rejectedBeforeCreate;

            public string BaseUrl { get; }
            public ConcurrentQueue<string> PutPaths { get; } = new();
            public ConcurrentQueue<string> RawRequests { get; } = new();
            public bool RepoCreated => _repoCreated;
            public int RejectedBeforeCreate => _rejectedBeforeCreate;

            public FakeGitHub()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                BaseUrl = $"http://127.0.0.1:{port}";
                _listener.Prefixes.Add(BaseUrl + "/");
                _listener.Start();
                _ = Task.Run(ServeAsync);
            }

            public void Seed(string path, byte[] bytes) => _files[path] = bytes;
            public bool Has(string path) => _files.ContainsKey(path);

            public void SeedManifest(IDictionary<string, (DateTime Modified, long Size)> entries)
            {
                var manifest = new SyncManifest();
                foreach (var (path, (modified, size)) in entries)
                    manifest.Files[path] = new SyncFileEntry { LastModifiedUtc = modified.ToString("o"), SizeBytes = size };
                _files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
            }

            public HashSet<string> ManifestKeys() =>
                _files.TryGetValue("manifest.json", out var bytes)
                    ? JsonSerializer.Deserialize<SyncManifest>(bytes)!.Files.Keys.ToHashSet()
                    : new HashSet<string>();

            private async Task ServeAsync()
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    _ = Task.Run(() => HandleAsync(ctx));
                }
            }

            private async Task HandleAsync(HttpListenerContext ctx)
            {
                try
                {
                    await Task.Delay(15);   // keeps a sync running long enough for a second caller to join it
                    string path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
                    string repo = $"/repos/tester/{RepoName}";
                    if (path == "/user") { await JsonAsync(ctx, 200, new { login = "tester" }); return; }
                    if (path == "/user/repos" && ctx.Request.HttpMethod == "POST")
                    {
                        _repoCreated = true;
                        await JsonAsync(ctx, 201, new { name = RepoName });
                        return;
                    }
                    if (!_repoCreated)
                    {
                        if (path.StartsWith(repo + "/contents/", StringComparison.Ordinal) && ctx.Request.HttpMethod == "PUT")
                            Interlocked.Increment(ref _rejectedBeforeCreate);
                        await JsonAsync(ctx, 404, new { message = "Not Found" });
                        return;
                    }
                    if (path == repo) { await JsonAsync(ctx, 200, new { name = RepoName }); return; }
                    if (path == repo + "/git/trees/HEAD")
                    {
                        var tree = _files.Select(kv => new { path = kv.Key, type = "blob", sha = Sha(kv.Value) }).ToArray();
                        await JsonAsync(ctx, 200, new { tree });
                        return;
                    }
                    string contents = repo + "/contents/";
                    if (path.StartsWith(contents, StringComparison.Ordinal))
                    {
                        string repoPath = path[contents.Length..];
                        switch (ctx.Request.HttpMethod)
                        {
                            case "GET":
                                if (!_files.TryGetValue(repoPath, out var bytes))
                                {
                                    await JsonAsync(ctx, 404, new { message = "Not Found" });
                                    return;
                                }
                                if ((ctx.Request.Headers["Accept"] ?? "").Contains("raw", StringComparison.Ordinal))
                                {
                                    RawRequests.Enqueue(repoPath);
                                    ctx.Response.StatusCode = 200;
                                    ctx.Response.ContentLength64 = bytes.Length;
                                    await ctx.Response.OutputStream.WriteAsync(bytes);
                                    ctx.Response.Close();
                                    return;
                                }
                                bool inline = bytes.Length <= 1_000_000;   // GitHub inlines files up to 1 MB only
                                await JsonAsync(ctx, 200, new
                                {
                                    path = repoPath, sha = Sha(bytes), size = bytes.Length,
                                    encoding = inline ? "base64" : "none",
                                    content = inline ? Convert.ToBase64String(bytes) : "",
                                });
                                return;
                            case "PUT":
                            {
                                using var reader = new StreamReader(ctx.Request.InputStream);
                                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                                byte[] body = Convert.FromBase64String(doc.RootElement.GetProperty("content").GetString() ?? "");
                                _files[repoPath] = body;
                                PutPaths.Enqueue(repoPath);
                                await JsonAsync(ctx, 201, new { content = new { sha = Sha(body) } });
                                return;
                            }
                            case "DELETE":
                                _files.TryRemove(repoPath, out _);
                                await JsonAsync(ctx, 200, new { });
                                return;
                        }
                    }
                    await JsonAsync(ctx, 404, new { message = "Not Found" });
                }
                catch
                {
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                }
            }

            private static async Task JsonAsync(HttpListenerContext ctx, int status, object body)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }

            private static string Sha(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

            public void Dispose()
            {
                _stop.Cancel();
                try { _listener.Stop(); _listener.Close(); } catch { }
            }
        }
    }
}
