using System;
using System.IO;
using System.Threading.Tasks;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Platform
{
    /// <summary>
    /// `Emutastic --selftest-secrets --portable`: cloud-sync credential storage against the desktop
    /// keyring this session really has. Three stages:
    ///   1. the passphrase gate that keeps a sync from running under the wrong key (pure);
    ///   2. a store / lookup / replace / clear round trip on a throwaway credential;
    ///   3. the session restore on a fresh portable config.json holding a plaintext token and
    ///      passphrase: both must move into the keyring, and the file on disk must keep neither
    ///      and be owner-only.
    /// Stage 3 needs --portable so it can never touch ~/.config/Emutastic/config.json, and its
    /// keyring items carry the PortableData profile, so the installed app's sign-in is never read
    /// or replaced either. Run the apphost rather than `dotnet Emutastic.dll`, or PortableData
    /// lands beside the dotnet host. Everything created is removed again, by name.
    /// Exit 0 = pass, 1 = a check failed, 2 = incomplete (no keyring reachable, or no --portable).
    /// </summary>
    internal static class SecretStoreSelfTest
    {
        public static int Run() => RunAsync().GetAwaiter().GetResult();

        private static async Task<int> RunAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("=== INCOMPLETE (Linux only) ===");
                return 2;
            }

            int failures = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
                if (!ok) failures++;
            }
            int Finish(bool complete)
            {
                string verdict = failures > 0 ? $"FAIL ({failures} check(s))" : complete ? "PASS" : "INCOMPLETE";
                Console.WriteLine($"=== {verdict} ===");
                return failures > 0 ? 1 : complete ? 0 : 2;
            }

            Console.WriteLine("=== secret store self-test ===");
            const string Sentinel = GitHubSyncService.KeyringSentinel;

            Console.WriteLine("--- passphrase gate");
            Check(GitHubSyncService.ResolvePassphrase(false, Sentinel, null, out string? p1) && p1 == null,
                  "encryption off: syncs in the clear, whatever is stored");
            Check(GitHubSyncService.ResolvePassphrase(true, "", null, out string? p2) && p2 == null,
                  "encryption on, no passphrase ever saved: syncs in the clear (upstream behaviour)");
            Check(!GitHubSyncService.ResolvePassphrase(true, Sentinel, null, out _),
                  "encryption on, passphrase in a keyring that could not be read: refuses");
            Check(!GitHubSyncService.ResolvePassphrase(true, "stored-not-restored", null, out _),
                  "encryption on, stored passphrase not restored yet: refuses");
            Check(GitHubSyncService.ResolvePassphrase(true, Sentinel, "pw", out string? p5) && p5 == "pw",
                  "encryption on, passphrase known: uses it");
            Check(GitHubSyncService.ResolvePassphrase(true, "", "pw", out string? p6) && p6 == "pw",
                  "encryption on, saved this session with the keyring write still pending: uses it");

            Console.WriteLine("--- keyring round trip (throwaway credential)");
            string credential = $"selftest-{Guid.NewGuid():N}";
            if (!SecretStore.TryLookup(credential, out string? absent, out string? probe))
            {
                Console.WriteLine($"  [SKIP] no keyring reachable: {probe}");
                return Finish(complete: false);
            }
            const string label = "Emutastic self-test (safe to delete)";
            const string first = "pässwörd 🔑 one", second = "pässwörd 🔑 two";
            try
            {
                Check(absent == null, "an unknown credential reads as absent, not as an error");
                Check(SecretStore.TryStore(credential, first, label, out string? storeErr),
                      "store" + (storeErr != null ? $": {storeErr}" : ""));
                Check(SecretStore.TryLookup(credential, out string? read1, out _) && read1 == first,
                      "lookup returns the stored value, UTF-8 intact");
                Check(SecretStore.TryStore(credential, second, label, out _)
                      && SecretStore.TryLookup(credential, out string? read2, out _) && read2 == second,
                      "storing again replaces the value");
                Check(SecretStore.TryClear(credential, out string? clearErr),
                      "clear" + (clearErr != null ? $": {clearErr}" : ""));
                Check(SecretStore.TryLookup(credential, out string? read3, out _) && read3 == null,
                      "lookup after clear reads as absent");
            }
            finally
            {
                SecretStore.TryClear(credential, out _);
            }

            Console.WriteLine("--- session restore moves plaintext out of config.json");
            if (!AppPaths.IsPortable)
            {
                Console.WriteLine("  [SKIP] needs --portable (must never touch the real config.json)");
                return Finish(complete: false);
            }
            string configPath = Path.Combine(AppPaths.DataRoot, "config.json");
            if (File.Exists(configPath))
            {
                Console.WriteLine($"  [SKIP] {configPath} already exists; not overwriting it");
                return Finish(complete: false);
            }

            string token = $"selftest-token-{Guid.NewGuid():N}";
            string passphrase = $"selftest-passphrase-{Guid.NewGuid():N}";
            var config = new JsonConfigurationService();
            try
            {
                await config.LoadAsync();   // no file yet: writes a fresh config.json
                var cloud = config.GetCloudSyncConfiguration();
                cloud.GitHubTokenProtected = token;
                cloud.GitHubUsername = "selftest";
                cloud.EncryptionEnabled = true;
                cloud.PassphraseProtected = passphrase;
                config.SetCloudSyncConfiguration(cloud);
                App.Configuration = config;

                bool signedIn = await GitHubSyncService.Instance.RestoreSessionAsync();
                // The restore schedules a debounced save; let it land before the explicit one
                // below, so nothing writes config.json again after the cleanup deletes it.
                await Task.Delay(1000);

                Check(signedIn && GitHubSyncService.Instance.Username == "selftest",
                      "restore signs in from the plaintext token");
                Check(cloud.GitHubTokenProtected == Sentinel && cloud.PassphraseProtected == Sentinel,
                      "both config fields now hold only the sentinel");
                Check(SecretStore.TryLookup(GitHubSyncService.TokenCredential, out string? kToken, out _) && kToken == token,
                      "the token is in the keyring");
                Check(SecretStore.TryLookup(GitHubSyncService.PassphraseCredential, out string? kPass, out _) && kPass == passphrase,
                      "the passphrase is in the keyring");
                Check(GitHubSyncService.Instance.RestoreProblem == null, "no restore problem reported");

                await config.SaveAsync();
                string onDisk = await File.ReadAllTextAsync(configPath);
                Check(!onDisk.Contains(token) && !onDisk.Contains(passphrase), "config.json on disk carries neither secret");
                UnixFileMode mode = File.GetUnixFileMode(configPath);
                Check(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                      $"config.json is owner-only (mode {Convert.ToString((int)mode, 8)})");
            }
            finally
            {
                SecretStore.TryClear(GitHubSyncService.TokenCredential, out _);
                SecretStore.TryClear(GitHubSyncService.PassphraseCredential, out _);
                try { File.Delete(configPath); } catch { }
            }
            return Finish(complete: true);
        }
    }
}
