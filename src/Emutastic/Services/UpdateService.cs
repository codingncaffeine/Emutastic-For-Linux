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
    ///   Deb — exe lives under /usr/ AND dpkg registered it (the emutastic .deb):
    ///       download the .deb → `pkexec dpkg -i` (GUI auth prompt) → relaunch script.
    ///   PackageManaged — exe lives under /usr/ but dpkg doesn't own it (the AUR's
    ///       emutastic-bin, a distro package): never touched here — dpkg would fail,
    ///       or overwrite another package manager's files behind its back. The user
    ///       updates through that package manager.
    ///   Dev — running from a build tree (bin/Release|Debug): self-update is
    ///       wrong here; the About tab says "update via git".
    ///   ReadOnly — unwritable dir outside the package-managed tree (e.g. /opt by
    ///       root): no managed flow; the About tab falls back to the release page.
    ///
    /// Every step is recorded in [DataRoot]/Logs/update.log (see UpdateLog).
    /// EMUTASTIC_UPDATE_API overrides the releases/latest endpoint so the
    /// whole pipeline can be integration-tested against a local mock server.
    /// </summary>
    public static class UpdateService
    {
        public const string DefaultLatestApi =
            "https://api.github.com/repos/codingncaffeine/Emutastic-For-Linux/releases/latest";

        public static string LatestApi =>
            Environment.GetEnvironmentVariable("EMUTASTIC_UPDATE_API") ?? DefaultLatestApi;

        public enum InstallKind { Dev, Deb, PackageManaged, SelfContained, ReadOnly }

        private const string DpkgInfoDir = "/var/lib/dpkg/info";

        public static InstallKind DetectInstallKind() => DetectInstallKind(AppPaths.GetExeFolder(), DpkgInfoDir);

        /// <summary>
        /// Classifies the install in <paramref name="exeFolder"/>. /usr belongs to the system
        /// package manager (/usr/local excepted, which is the admin's own), and only the copy
        /// dpkg itself installed may be replaced through dpkg: on Arch the same path belongs to
        /// pacman even when dpkg is present as a tool. <paramref name="dpkgInfoDir"/> is dpkg's
        /// per-package file lists, a parameter so the self-test can supply its own.
        /// </summary>
        public static InstallKind DetectInstallKind(string exeFolder, string dpkgInfoDir)
        {
            string dir = exeFolder.Replace('\\', '/').TrimEnd('/');
            if (dir.Contains("/bin/Release/") || dir.Contains("/bin/Debug/")
                || dir.EndsWith("/bin/Release") || dir.EndsWith("/bin/Debug"))
                return InstallKind.Dev;
            if (dir.StartsWith("/usr/") && !dir.StartsWith("/usr/local/"))
                return DpkgOwns(dir + "/Emutastic", dpkgInfoDir) ? InstallKind.Deb : InstallKind.PackageManaged;
            try
            {
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return InstallKind.SelfContained;
            }
            catch { return InstallKind.ReadOnly; }
        }

        // The emutastic .deb registers its files in dpkg's database as emutastic.list (or
        // emutastic:<arch>.list); a line naming our apphost proves dpkg installed this copy.
        private static bool DpkgOwns(string file, string dpkgInfoDir)
        {
            try
            {
                if (!Directory.Exists(dpkgInfoDir)) return false;
                foreach (var list in Directory.EnumerateFiles(dpkgInfoDir, "emutastic*.list"))
                {
                    string package = Path.GetFileNameWithoutExtension(list);
                    if (package != "emutastic" && !package.StartsWith("emutastic:", StringComparison.Ordinal)) continue;
                    foreach (var line in File.ReadLines(list))
                        if (line == file) return true;
                }
            }
            catch { /* unreadable database: not provably dpkg's */ }
            return false;
        }

        /// <summary>What to tell a user whose copy cannot self-update.</summary>
        public static string ExplainNoSelfUpdate(InstallKind kind) => kind switch
        {
            InstallKind.Dev => "Development build — update via git.",
            InstallKind.PackageManaged => "This copy was installed by your package manager — update it there "
                                          + "(on Arch, for example: yay -Syu emutastic-bin).",
            InstallKind.ReadOnly => "This install location isn't writable — update from the releases page.",
            _ => "Open the release on GitHub to update.",
        };

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
                if (prefs?.CheckForUpdates == false) { UpdateLog.Write("startup check skipped: disabled in Preferences"); return null; }

                var kind = DetectInstallKind();
                UpdateLog.Write($"startup check: kind={kind} exe={AppPaths.GetExeFolder()} api={LatestApi}");
                if (kind is not (InstallKind.Deb or InstallKind.SelfContained))
                {
                    UpdateLog.Write($"startup check: this install does not self-update — {ExplainNoSelfUpdate(kind)}");
                    return null;
                }

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/updater");
                string json = await http.GetStringAsync(LatestApi, ct).ConfigureAwait(false);

                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                string tag = obj.Value<string>("tag_name") ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V').Trim(), out var remote)) return null;
                var local = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (local == null) return null;
                bool newer = new Version(remote.Major, remote.Minor, remote.Build)
                        .CompareTo(new Version(local.Major, local.Minor, local.Build)) > 0;
                UpdateLog.Write($"latest {tag} vs installed {local.Major}.{local.Minor}.{local.Build}: {(newer ? "newer" : "not newer")}");
                if (!newer) return null;

                var assets = new System.Collections.Generic.List<ReleaseAsset>();
                if (obj["assets"] is Newtonsoft.Json.Linq.JArray arr)
                    foreach (var a in arr)
                        assets.Add(new ReleaseAsset(
                            a.Value<string>("name") ?? "",
                            a.Value<string>("browser_download_url") ?? "",
                            a.Value<long?>("size") ?? 0,
                            a.Value<string>("digest")));   // "sha256:…" once GitHub has computed it

                var asset = PickAsset(kind, assets);
                UpdateLog.Write(asset == null
                    ? $"no asset for {kind} among {assets.Count} release asset(s)"
                    : $"update offered: {asset.Name} ({asset.Size} bytes)");
                return asset == null ? null : new AppUpdate(tag, asset, kind);
            }
            catch (Exception ex)
            {
                UpdateLog.Write($"startup check failed: {ex.Message}");
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
                if (kind is not (InstallKind.Deb or InstallKind.SelfContained))
                {
                    UpdateLog.Write($"apply refused: kind={kind}");
                    return ExplainNoSelfUpdate(kind);
                }
                string tmp = Path.Combine(Path.GetTempPath(), $"emutastic-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tmp);
                string file = Path.Combine(tmp, asset.Name);
                UpdateLog.Write($"apply: {asset.Name} ({asset.Size} bytes) kind={kind}, downloading to {file}");

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
                        UpdateLog.Write($"digest mismatch: expected {expected}, got {actual}");
                        return "Update integrity check failed — the download didn't match the "
                             + "expected checksum, so nothing was installed. Try again, or update "
                             + "from the releases page.";
                    }
                    UpdateLog.Write("SHA-256 digest verified");
                }
                else
                {
                    UpdateLog.Write("no SHA-256 digest published for this asset — skipping verification");
                }

                return kind switch
                {
                    InstallKind.SelfContained => await ApplyTarballAsync(file, progress, ct),
                    InstallKind.Deb           => await ApplyDebAsync(file, progress, ct),
                    _ => "This installation can't self-update.",
                };
            }
            catch (OperationCanceledException) { UpdateLog.Write("cancelled"); return "Update cancelled."; }
            catch (Exception ex)
            {
                UpdateLog.Write($"failed: {ex}");
                return $"Update failed: {ex.Message} (details in {UpdateLog.PathForDisplay})";
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

            UpdateLog.Write($"extracting to {staging}");
            var tar = Process.Start(new ProcessStartInfo("tar", $"-xzf \"{tarball}\" -C \"{staging}\"")
            { UseShellExecute = false, RedirectStandardError = true })!;
            var tarErrTask = tar.StandardError.ReadToEndAsync(ct);
            await tar.WaitForExitAsync(ct);
            string tarErr = (await tarErrTask).Trim();
            UpdateLog.Write($"tar exit {tar.ExitCode}{(tarErr.Length > 0 ? ": " + tarErr : "")}");
            if (tar.ExitCode != 0) return $"Archive extraction failed (details in {UpdateLog.PathForDisplay}).";
            if (!File.Exists(Path.Combine(staging, "Emutastic")))
            {
                UpdateLog.Write("staging folder has no Emutastic apphost");
                return "Archive doesn't look like an Emutastic release.";
            }

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
            UpdateLog.Write($"relaunch script {script} started; exiting so it can swap the files");

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
            var psi = new ProcessStartInfo("pkexec")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("dpkg"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(deb);
            UpdateLog.Write($"pkexec dpkg -i {deb}");
            var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            string output = ((await outTask) + "\n" + (await errTask)).Trim();
            UpdateLog.Write($"dpkg exit {p.ExitCode}{(output.Length > 0 ? ": " + output.Replace("\n", " | ") : "")}");
            if (p.ExitCode == 126 || p.ExitCode == 127) return "Authorization was cancelled.";
            if (p.ExitCode != 0) return $"Package install failed (dpkg exit {p.ExitCode}; details in {UpdateLog.PathForDisplay}).";

            string script = Path.Combine(Path.GetTempPath(), $"emutastic-apply-{Environment.ProcessId}.sh");
            await File.WriteAllTextAsync(script, $"""
                #!/bin/sh
                tail --pid={Environment.ProcessId} -f /dev/null
                rm -f "{deb}"
                exec /usr/lib/emutastic/Emutastic
                """, ct);
            Process.Start(new ProcessStartInfo("setsid", $"bash \"{script}\"") { UseShellExecute = false });
            UpdateLog.Write($"package installed; relaunch script {script} started, exiting");

            progress.Report((100, "Restarting…"));
            await Task.Delay(400, ct);
            Environment.Exit(0);
            return null;
        }
    }
}
