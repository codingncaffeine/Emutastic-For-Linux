using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;

namespace Emutastic.Services
{
    /// <summary>
    /// `Emutastic --selftest-update`: exercises the updater pipeline headlessly —
    /// detect install kind, fetch the latest-release JSON (EMUTASTIC_UPDATE_API
    /// override honored), pick the asset, download and APPLY. On the tarball
    /// path this process exits and the relaunch script starts the swapped
    /// binary; observing the new install is the test's assertion.
    /// </summary>
    internal static class UpdateSelfTest
    {
        static int _fail;

        static void Check(string what, bool ok)
        {
            if (!ok) _fail++;
            Console.WriteLine($"[update-selftest] {(ok ? "ok  " : "FAIL")} {what}");
        }

        /// <summary>Install-kind classification against a throwaway dpkg database: no network,
        /// nothing outside a temp folder is touched.</summary>
        static void ClassificationChecks()
        {
            const string usr = "/usr/lib/emutastic";
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"emutastic-update-selftest-{Environment.ProcessId}");
            try
            {
                string dpkg = System.IO.Path.Combine(root, "dpkg-info");
                System.IO.Directory.CreateDirectory(dpkg);
                Check("a build tree is Dev",
                    UpdateService.DetectInstallKind("/home/u/src/Emutastic/bin/Release/net10.0", dpkg) == UpdateService.InstallKind.Dev);
                Check("/usr/lib/emutastic with no dpkg record is PackageManaged (the AUR case)",
                    UpdateService.DetectInstallKind(usr, dpkg) == UpdateService.InstallKind.PackageManaged);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dpkg, "emutastic.list"), new[] { usr, usr + "/Emutastic" });
                Check("/usr/lib/emutastic listed in emutastic.list is Deb",
                    UpdateService.DetectInstallKind(usr, dpkg) == UpdateService.InstallKind.Deb);
                Check("a trailing slash on the folder does not change the answer",
                    UpdateService.DetectInstallKind(usr + "/", dpkg) == UpdateService.InstallKind.Deb);
                Check("another /usr folder is not covered by that list",
                    UpdateService.DetectInstallKind("/usr/lib/emutastic-other", dpkg) == UpdateService.InstallKind.PackageManaged);
                System.IO.File.Delete(System.IO.Path.Combine(dpkg, "emutastic.list"));
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dpkg, "emutastic:amd64.list"), new[] { usr + "/Emutastic" });
                Check("emutastic:amd64.list also proves dpkg ownership",
                    UpdateService.DetectInstallKind(usr, dpkg) == UpdateService.InstallKind.Deb);
                System.IO.File.Delete(System.IO.Path.Combine(dpkg, "emutastic:amd64.list"));
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dpkg, "emutastic-extras.list"), new[] { usr + "/Emutastic" });
                Check("a different package's list (emutastic-extras) does not count",
                    UpdateService.DetectInstallKind(usr, dpkg) == UpdateService.InstallKind.PackageManaged);
                Check("a missing dpkg database means PackageManaged, never Deb",
                    UpdateService.DetectInstallKind(usr, System.IO.Path.Combine(root, "no-such-dir")) == UpdateService.InstallKind.PackageManaged);
                string writable = System.IO.Path.Combine(root, "portable");
                System.IO.Directory.CreateDirectory(writable);
                Check("a writable folder is SelfContained",
                    UpdateService.DetectInstallKind(writable, dpkg) == UpdateService.InstallKind.SelfContained);
                Check("/usr/local is the admin's own: it takes the write probe, not the package rule",
                    UpdateService.DetectInstallKind("/usr/local/lib/emutastic-no-such-folder", dpkg) == UpdateService.InstallKind.ReadOnly);
                Check("PackageManaged has package-manager wording",
                    UpdateService.ExplainNoSelfUpdate(UpdateService.InstallKind.PackageManaged).Contains("package manager"));
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        public static int Run()
        {
            ClassificationChecks();
            if (_fail > 0)
            {
                Console.WriteLine($"[update-selftest] {_fail} classification check(s) FAILED");
                return 1;
            }
            Console.WriteLine("[update-selftest] classification checks PASS");

            var kind = UpdateService.DetectInstallKind();
            Console.WriteLine($"[update-selftest] kind={kind} api={UpdateService.LatestApi} log={UpdateLog.PathForDisplay}");

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/update-selftest");
                string json = http.GetStringAsync(UpdateService.LatestApi).GetAwaiter().GetResult();
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                string tag = obj.Value<string>("tag_name") ?? "";
                var assets = new List<UpdateService.ReleaseAsset>();
                if (obj["assets"] is Newtonsoft.Json.Linq.JArray arr)
                    foreach (var a in arr)
                        assets.Add(new UpdateService.ReleaseAsset(
                            a.Value<string>("name") ?? "",
                            a.Value<string>("browser_download_url") ?? "",
                            a.Value<long?>("size") ?? 0));
                Console.WriteLine($"[update-selftest] tag={tag} assets={assets.Count}");

                var asset = UpdateService.PickAsset(kind, assets);
                if (asset == null) { Console.WriteLine("[update-selftest] FAIL: no matching asset"); return 2; }
                Console.WriteLine($"[update-selftest] picked {asset.Name} ({asset.Size} bytes)");

                int lastPct = -1;
                var progress = new Progress<(int pct, string msg)>(p =>
                {
                    if (p.pct == lastPct) return;   // one line per percent, not per 64KB chunk
                    lastPct = p.pct;
                    Console.WriteLine($"[update-selftest] {p.msg}");
                });
                string? err = UpdateService.DownloadAndApplyAsync(asset, kind, progress, CancellationToken.None)
                    .GetAwaiter().GetResult();
                // Tarball path never returns on success (Environment.Exit in apply).
                Console.WriteLine($"[update-selftest] FAIL: {err ?? "apply returned unexpectedly"}");
                return 3;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[update-selftest] FAIL: {ex.Message}");
                return 4;
            }
        }
    }
}
