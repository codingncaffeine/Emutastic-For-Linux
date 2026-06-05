using System;
using System.Text;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// `EMUTASTIC_SYNC_TOKEN=… Emutastic --selftest-cloudsync`: exercises the cloud
    /// sync engine against the REAL GitHub API with an injected token (bypasses the
    /// device flow, which needs the OAuth client id). Validates: token → username,
    /// repo bootstrap, plain + encrypted upload/download round-trips, manifest
    /// save/load. Exit 0 = all stages byte-identical.
    /// </summary>
    internal static class CloudSyncSelfTest
    {
        public static int Run()
        {
            return RunAsync().GetAwaiter().GetResult();
        }

        private static async Task<int> RunAsync()
        {
            string? token = Environment.GetEnvironmentVariable("EMUTASTIC_SYNC_TOKEN");
            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("[cloudsync-selftest] FAIL: set EMUTASTIC_SYNC_TOKEN");
                return 1;
            }

            var svc = GitHubSyncService.Instance;
            // Inject the token through the same field LoadFromConfig fills.
            typeof(GitHubSyncService).GetField("_token",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(svc, token);

            if (!await svc.ValidateTokenAsync()) { Console.WriteLine("[cloudsync-selftest] FAIL: token invalid"); return 2; }
            Console.WriteLine($"[cloudsync-selftest] authenticated as {svc.Username}");

            await svc.EnsureRepoExistsAsync();
            await svc.RefreshShaCacheAsync();
            Console.WriteLine("[cloudsync-selftest] repo ensured");

            // Plain round-trip
            byte[] payload = Encoding.UTF8.GetBytes($"selftest {Guid.NewGuid():N}");
            const string testPath = "BatterySaves/SelfTest/roundtrip.srm";
            if (!await svc.UploadFileAsync(testPath, payload)) { Console.WriteLine("[cloudsync-selftest] FAIL: upload"); return 3; }
            byte[]? echoed = await svc.DownloadFileAsync(testPath);
            if (echoed == null || !payload.AsSpan().SequenceEqual(echoed)) { Console.WriteLine("[cloudsync-selftest] FAIL: round-trip mismatch"); return 4; }
            Console.WriteLine($"[cloudsync-selftest] plain round-trip OK ({payload.Length} bytes)");

            // Encrypted round-trip (crypto layer only — config-independent)
            byte[] key = GitHubSyncService.DeriveKey("selftest-passphrase", svc.Username ?? "");
            byte[] enc = GitHubSyncService.Encrypt(payload, key);
            byte[] dec = GitHubSyncService.Decrypt(enc, key);
            if (!payload.AsSpan().SequenceEqual(dec)) { Console.WriteLine("[cloudsync-selftest] FAIL: crypto mismatch"); return 5; }
            Console.WriteLine($"[cloudsync-selftest] AES-256-GCM round-trip OK (blob {enc.Length} bytes)");

            // Manifest round-trip
            svc.ManifestCache.Files[testPath] = new Configuration.SyncFileEntry
            { LastModifiedUtc = DateTime.UtcNow.ToString("o"), SizeBytes = payload.Length };
            await svc.SaveManifestAsync();
            svc.ManifestCache.Files.Clear();
            await svc.LoadManifestAsync();
            if (!svc.ManifestCache.Files.ContainsKey(testPath)) { Console.WriteLine("[cloudsync-selftest] FAIL: manifest round-trip"); return 6; }
            Console.WriteLine($"[cloudsync-selftest] manifest round-trip OK ({svc.ManifestCache.Files.Count} entries)");

            Console.WriteLine("[cloudsync-selftest] PASS");
            return 0;
        }
    }
}
