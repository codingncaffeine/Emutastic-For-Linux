using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// In-app updater (Linux). Consumes the GitHub release artifacts that
    /// packaging/build-release.sh produces — the asset names are a contract:
    ///   Emutastic-&lt;ver&gt;-linux-x64.tar.gz          self-contained tarball
    ///   emutastic_&lt;ver&gt;_amd64.deb                 system package
    ///
    /// Apply strategy depends on how THIS copy is installed:
    ///   SelfContained — exe dir is user-writable (portable or plain tarball
    ///       extract): download tarball → extract to .update-staging → spawn a
    ///       detached script that waits for our exit, copies staging over the
    ///       install (replacing the running binary only after we're gone —
    ///       avoids ETXTBSY), and relaunches. portable.txt survives (copy
    ///       never deletes extra files).
    ///   Deb — exe lives under /usr/: download the .deb → `pkexec dpkg -i`
    ///       (GUI auth prompt) → relaunch script.
    ///   Dev — running from a build tree (bin/Release|Debug): self-update is
    ///       wrong here; the About tab says "update via git".
    ///   ReadOnly — non-/usr unwritable dir (e.g. /opt by root): no managed
    ///       flow; the About tab falls back to the release page.
    ///
    /// EMUTASTIC_UPDATE_API overrides the releases/latest endpoint so the
    /// whole pipeline can be integration-tested against a local mock server.
    /// </summary>
    public static class UpdateService
    {
        public const string DefaultLatestApi =
            "https://api.github.com/repos/codingncaffeine/Emutastic-For-Linux/releases/latest";

        public static string LatestApi =>
            Environment.GetEnvironmentVariable("EMUTASTIC_UPDATE_API") ?? DefaultLatestApi;

        public enum InstallKind { Dev, Deb, SelfContained, ReadOnly }

        public static InstallKind DetectInstallKind()
        {
            string dir = AppPaths.GetExeFolder();
            string norm = dir.Replace('\\', '/');
            if (norm.Contains("/bin/Release/") || norm.Contains("/bin/Debug/")
                || norm.EndsWith("/bin/Release") || norm.EndsWith("/bin/Debug"))
                return InstallKind.Dev;
            if (norm.StartsWith("/usr/")) return InstallKind.Deb;
            try
            {
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return InstallKind.SelfContained;
            }
            catch { return InstallKind.ReadOnly; }
        }

        public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest = null);

        /// <summary>A newer self-installable release found by <see cref="CheckAsync"/>.</summary>
        public sealed record AppUpdate(string Tag, ReleaseAsset Asset, InstallKind Kind);

        /// <summary>
        /// Startup app-update probe (port of upstream MainWindow's post-core-check call).
        /// Honors <c>UserPreferences.CheckForUpdates</c>; returns null unless a strictly newer
        /// release exists AND this install kind can self-update. Never throws.
        /// </summary>
        public static async Task<AppUpdate?> CheckAsync(CancellationToken ct)
        {
            try
            {
                var prefs = App.Configuration?.GetUserPreferences();
                if (prefs?.CheckForUpdates == false) return null;

                var kind = DetectInstallKind();
                if (kind is not (InstallKind.Deb or InstallKind.SelfContained)) return null;

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/updater");
                string json = await http.GetStringAsync(LatestApi, ct).ConfigureAwait(false);

                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                string tag = obj.Value<string>("tag_name") ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V').Trim(), out var remote)) return null;
                var local = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (local == null) return null;
                if (new Version(remote.Major, remote.Minor, remote.Build)
                        .CompareTo(new Version(local.Major, local.Minor, local.Build)) <= 0) return null;

                var assets = new System.Collections.Generic.List<ReleaseAsset>();
                if (obj["assets"] is Newtonsoft.Json.Linq.JArray arr)
                    foreach (var a in arr)
                        assets.Add(new ReleaseAsset(
                            a.Value<string>("name") ?? "",
                            a.Value<string>("browser_download_url") ?? "",
                            a.Value<long?>("size") ?? 0,
                            a.Value<string>("digest")));   // "sha256:…" once GitHub has computed it

                var asset = PickAsset(kind, assets);
                return asset == null ? null : new AppUpdate(tag, asset, kind);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Update] startup check failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Picks the right asset for this install kind, or null.</summary>
        public static ReleaseAsset? PickAsset(InstallKind kind, System.Collections.Generic.IReadOnlyList<ReleaseAsset> assets)
        {
            foreach (var a in assets)
            {
                bool deb = a.Name.StartsWith("emutastic_", StringComparison.OrdinalIgnoreCase)
                           && a.Name.EndsWith("_amd64.deb", StringComparison.OrdinalIgnoreCase);
                // Plain tarball only — never the -portable variant: the existing
                // portable.txt (or its absence) is the user's choice and survives.
                bool tar = a.Name.StartsWith("Emutastic-", StringComparison.OrdinalIgnoreCase)
                           && a.Name.EndsWith("-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase);
                if (kind == InstallKind.Deb && deb) return a;
                if (kind == InstallKind.SelfContained && tar) return a;
            }
            return null;
        }

        /// <summary>
        /// Downloads the asset with progress (0..100 + status text) and applies it.
        /// On success the APP EXITS (the relaunch script takes over); returns an
        /// error string on failure, never throws.
        /// </summary>
        public static async Task<string?> DownloadAndApplyAsync(
            ReleaseAsset asset, InstallKind kind, IProgress<(int pct, string msg)> progress, CancellationToken ct)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"emutastic-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tmp);
                string file = Path.Combine(tmp, asset.Name);

                using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/updater");
                    using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? asset.Size;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(file);
                    var buf = new byte[1 << 16];
                    long done = 0; int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                        if (total > 0)
                            progress.Report(((int)(done * 100 / total), $"Downloading… {done / 1048576} / {total / 1048576} MB"));
                    }
                }

                // Integrity gate: verify the downloaded artifact against GitHub's
                // published SHA-256 digest BEFORE we extract it over our own binary
                // or hand it to `pkexec dpkg -i`. A mismatch means the download was
                // corrupted or tampered with — abort rather than execute it.
                if (!string.IsNullOrEmpty(asset.Digest))
                {
                    progress.Report((100, "Verifying…"));
                    string expected = asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? asset.Digest[7..] : asset.Digest;
                    string actual = await Sha256HexAsync(file, ct);
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.WriteLine($"[Update] digest mismatch: expected {expected}, got {actual}");
                        return "Update integrity check failed — the download didn't match the "
                             + "expected checksum, so nothing was installed. Try again, or update "
                             + "from the releases page.";
                    }
                    Trace.WriteLine("[Update] SHA-256 digest verified");
                }
                else
                {
                    Trace.WriteLine("[Update] no SHA-256 digest published for this asset — skipping verification");
                }

                return kind switch
                {
                    InstallKind.SelfContained => await ApplyTarballAsync(file, progress, ct),
                    InstallKind.Deb           => await ApplyDebAsync(file, progress, ct),
                    _ => "This installation can't self-update.",
                };
            }
            catch (OperationCanceledException) { return "Update cancelled."; }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Update] failed: {ex}");
                return $"Update failed: {ex.Message}";
            }
        }

        private static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
        {
            await using var fs = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static async Task<string?> ApplyTarballAsync(string tarball, IProgress<(int, string)> progress, CancellationToken ct)
        {
            string install = AppPaths.GetExeFolder();
            string staging = Path.Combine(install, ".update-staging");
            progress.Report((100, "Extracting…"));
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var tar = Process.Start(new ProcessStartInfo("tar", $"-xzf \"{tarball}\" -C \"{staging}\"")
            { UseShellExecute = false, RedirectStandardError = true })!;
            await tar.WaitForExitAsync(ct);
            if (tar.ExitCode != 0) return "Archive extraction failed.";
            if (!File.Exists(Path.Combine(staging, "Emutastic"))) return "Archive doesn't look like an Emutastic release.";

            // The staging tarball may carry no portable marker by design; the
            // install's existing portable.txt is preserved by `cp -a` (it never
            // deletes files that only exist in the destination).
            string script = Path.Combine(Path.GetTempPath(), $"emutastic-apply-{Environment.ProcessId}.sh");
            string relaunchArgs = AppPaths.IsPortable ? "--portable" : "";
            await File.WriteAllTextAsync(script, $"""
                #!/bin/sh
                # Emutastic self-update: wait for the app to exit, swap files, relaunch.
                tail --pid={Environment.ProcessId} -f /dev/null
                cp -a "{staging}/." "{install}/"
                rm -rf "{staging}"
                rm -f "{tarball}"
                exec "{install}/Emutastic" {relaunchArgs}
                """, ct);
            Process.Start(new ProcessStartInfo("setsid", $"bash \"{script}\"")
            { UseShellExecute = false });

            progress.Report((100, "Restarting…"));
            await Task.Delay(400, ct);
            Environment.Exit(0);
            return null; // unreachable
        }

        private static async Task<string?> ApplyDebAsync(string deb, IProgress<(int, string)> progress, CancellationToken ct)
        {
            progress.Report((100, "Waiting for authorization…"));
            // pkexec pops the desktop's GUI auth prompt; dpkg replaces /usr/lib/emutastic
            // while we're still running (fine — our pages stay mapped until exit).
            var psi = new ProcessStartInfo("pkexec", $"dpkg -i \"{deb}\"") { UseShellExecute = false };
            var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode == 126 || p.ExitCode == 127) return "Authorization was cancelled.";
            if (p.ExitCode != 0) return $"Package install failed (dpkg exit {p.ExitCode}).";

            string script = Path.Combine(Path.GetTempPath(), $"emutastic-apply-{Environment.ProcessId}.sh");
            await File.WriteAllTextAsync(script, $"""
                #!/bin/sh
                tail --pid={Environment.ProcessId} -f /dev/null
                rm -f "{deb}"
                exec /usr/lib/emutastic/Emutastic
                """, ct);
            Process.Start(new ProcessStartInfo("setsid", $"bash \"{script}\"") { UseShellExecute = false });

            progress.Report((100, "Restarting…"));
            await Task.Delay(400, ct);
            Environment.Exit(0);
            return null;
        }
    }
}
