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
        public static int Run()
        {
            var kind = UpdateService.DetectInstallKind();
            Console.WriteLine($"[update-selftest] kind={kind} api={UpdateService.LatestApi}");

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
