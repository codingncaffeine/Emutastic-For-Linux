using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public class CoreEntry
    {
        public string FileName    { get; init; } = "";   // e.g. "snes9x_libretro.so"
        public string DisplayName { get; init; } = "";
        public string[] Systems   { get; init; } = [];
        public bool Recommended   { get; init; } = true;
    }

    public enum CoreStatus { NotInstalled, Installed, UpdateAvailable }

    public class CoreDownloadService
    {
        // ── Catalog ───────────────────────────────────────────────────────────
        public static readonly IReadOnlyList<CoreEntry> Catalog = new List<CoreEntry>
        {
            new() { FileName = "nestopia_libretro.so",         DisplayName = "Nestopia",              Systems = ["NES", "FDS"],                        Recommended = true  },
            new() { FileName = "quicknes_libretro.so",         DisplayName = "QuickNES",              Systems = ["NES"],                               Recommended = false },
            new() { FileName = "fceumm_libretro.so",           DisplayName = "FCE Ultra MM",          Systems = ["NES"],                               Recommended = false },
            new() { FileName = "snes9x_libretro.so",           DisplayName = "Snes9x",                Systems = ["SNES"],                              Recommended = true  },
            new() { FileName = "bsnes_libretro.so",            DisplayName = "bsnes",                 Systems = ["SNES"],                              Recommended = false },
            new() { FileName = "mupen64plus_next_libretro.so", DisplayName = "Mupen64Plus-Next",      Systems = ["N64"],                               Recommended = true  },
            new() { FileName = "parallel_n64_libretro.so",     DisplayName = "Parallel N64",          Systems = ["N64"],                               Recommended = false },
            new() { FileName = "dolphin_libretro.so",          DisplayName = "Dolphin",               Systems = ["GameCube"],                          Recommended = true  },
            new() { FileName = "mgba_libretro.so",             DisplayName = "mGBA",                  Systems = ["GB", "GBC", "GBA"],                  Recommended = true  },
            new() { FileName = "gambatte_libretro.so",         DisplayName = "Gambatte",              Systems = ["GB", "GBC"],                         Recommended = false },
            new() { FileName = "sameboy_libretro.so",          DisplayName = "SameBoy",               Systems = ["GB", "GBC"],                         Recommended = false },
            new() { FileName = "desmume_libretro.so",          DisplayName = "DeSmuME",               Systems = ["NDS"],                               Recommended = true  },
            new() { FileName = "melonds_libretro.so",          DisplayName = "melonDS",               Systems = ["NDS"],                               Recommended = false },
            new() { FileName = "azahar_libretro.so",          DisplayName = "Azahar (3DS)",          Systems = ["3DS"],                               Recommended = true  },
            new() { FileName = "mednafen_vb_libretro.so",      DisplayName = "Mednafen Virtual Boy",  Systems = ["VirtualBoy"],                        Recommended = true  },
            new() { FileName = "genesis_plus_gx_libretro.so",  DisplayName = "Genesis Plus GX",       Systems = ["Genesis", "SegaCD", "SMS", "GameGear", "SG1000"], Recommended = true  },
            new() { FileName = "picodrive_libretro.so",        DisplayName = "PicoDrive",             Systems = ["Genesis", "Sega32X", "SMS"],         Recommended = true  },  // ONLY Sega 32X core → must be in "Download All Recommended" (diverges from upstream Windows, which leaves 32X uncovered)
            new() { FileName = "mednafen_saturn_libretro.so",  DisplayName = "Mednafen Saturn (Beetle)", Systems = ["Saturn"],                         Recommended = true  },
            new() { FileName = "kronos_libretro.so",           DisplayName = "Kronos",                Systems = ["Saturn"],                            Recommended = false },
            new() { FileName = "yabause_libretro.so",          DisplayName = "Yabause",               Systems = ["Saturn"],                            Recommended = false },
            new() { FileName = "mednafen_psx_hw_libretro.so",  DisplayName = "Mednafen PSX HW (Beetle)", Systems = ["PS1"],                            Recommended = true  },
            new() { FileName = "mednafen_psx_libretro.so",     DisplayName = "Mednafen PSX (Beetle)", Systems = ["PS1"],                               Recommended = false },
            new() { FileName = "pcsx2_libretro.so",            DisplayName = "LRPS2 (PCSX2)",         Systems = ["PS2"],                               Recommended = true  },
            new() { FileName = "ppsspp_libretro.so",           DisplayName = "PPSSPP",                Systems = ["PSP"],                               Recommended = true  },
            new() { FileName = "mednafen_pce_libretro.so",     DisplayName = "Mednafen PCE",          Systems = ["TG16", "TGCD"],                      Recommended = true  },
            new() { FileName = "mednafen_pce_fast_libretro.so",DisplayName = "Mednafen PCE Fast",     Systems = ["TG16", "TGCD"],                      Recommended = false },
            new() { FileName = "mednafen_ngp_libretro.so",     DisplayName = "Mednafen Neo Geo Pocket",Systems = ["NGP"],                              Recommended = true  },
            new() { FileName = "stella_libretro.so",           DisplayName = "Stella",                Systems = ["Atari2600"],                         Recommended = true  },

            new() { FileName = "prosystem_libretro.so",        DisplayName = "ProSystem",             Systems = ["Atari7800"],                         Recommended = true  },
            new() { FileName = "virtualjaguar_libretro.so",    DisplayName = "Virtual Jaguar",        Systems = ["Jaguar"],                            Recommended = true  },
            new() { FileName = "gearcoleco_libretro.so",       DisplayName = "GearColeco",            Systems = ["ColecoVision"],                      Recommended = true  },
            new() { FileName = "bluemsx_libretro.so",          DisplayName = "BlueMSX",               Systems = ["ColecoVision"],                      Recommended = false },

            new() { FileName = "vecx_libretro.so",             DisplayName = "Vecx",                  Systems = ["Vectrex"],                           Recommended = true  },
            new() { FileName = "opera_libretro.so",            DisplayName = "Opera (3DO)",            Systems = ["3DO"],                               Recommended = true  },
            new() { FileName = "same_cdi_libretro.so",         DisplayName = "SAME CDi",              Systems = ["CDi"],                               Recommended = true  },
            new() { FileName = "flycast_libretro.so",          DisplayName = "Flycast",               Systems = ["Dreamcast"],                         Recommended = true  },

            // Arcade
            new() { FileName = "fbneo_libretro.so",            DisplayName = "FBNeo (Final Burn Neo)", Systems = ["Arcade"],                            Recommended = true  },
            new() { FileName = "mame2003_plus_libretro.so",    DisplayName = "MAME 2003-Plus",         Systems = ["Arcade"],                            Recommended = true  },
            new() { FileName = "geolith_libretro.so",         DisplayName = "Geolith (Neo Geo / CD)", Systems = ["NeoGeo", "NeoCD"],                   Recommended = true  },
        };

        // ── Infrastructure ────────────────────────────────────────────────────
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private const string BuildbotBase = "https://buildbot.libretro.com/nightly/linux/x86_64/latest/";

        private string ZipUrl(string fileName) => BuildbotBase + fileName + ".zip";

        // ── Status check ──────────────────────────────────────────────────────

        /// <summary>
        /// Threshold for flagging a core as "update available". The buildbot
        /// publishes nightlies, so any installed file is technically older
        /// than the latest by hours — flagging on every drift would train
        /// users to ignore the badge. Only flag when the remote is meaningfully
        /// newer than the local file.
        /// </summary>
        public static readonly TimeSpan StalenessThreshold = TimeSpan.FromDays(14);

        /// <summary>
        /// Returns Installed / UpdateAvailable / NotInstalled for a single core.
        /// Uses HTTP HEAD to get Last-Modified and compares against local file write-time.
        /// </summary>
        public async Task<CoreStatus> CheckAsync(CoreEntry entry, string coresFolder,
            CancellationToken ct = default)
        {
            string localPath = Path.Combine(coresFolder, entry.FileName);
            if (!File.Exists(localPath)) return CoreStatus.NotInstalled;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, ZipUrl(entry.FileName));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.Content.Headers.LastModified is DateTimeOffset remote)
                {
                    var local = File.GetLastWriteTimeUtc(localPath);
                    if (remote.UtcDateTime > local.Add(StalenessThreshold))
                        return CoreStatus.UpdateAvailable;
                }
            }
            catch { /* network unavailable — treat as installed */ }

            return CoreStatus.Installed;
        }

        /// <summary>
        /// Fans out CheckAsync across every catalog entry that's actually
        /// installed locally. Returns the subset whose remote build is at
        /// least <see cref="StalenessThreshold"/> newer than the local file.
        /// Network failures are swallowed (returned as Installed in CheckAsync),
        /// so an offline run produces an empty list rather than throwing.
        /// </summary>
        public async Task<List<CoreEntry>> CheckAllForUpdatesAsync(string coresFolder,
            CancellationToken ct = default)
        {
            var installed = Catalog
                .Where(e => File.Exists(Path.Combine(coresFolder, e.FileName)))
                .ToList();

            // Parallel HEAD requests — buildbot is a CDN, this is fine.
            var tasks = installed.Select(async e =>
            {
                var status = await CheckAsync(e, coresFolder, ct).ConfigureAwait(false);
                return (entry: e, status);
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results
                .Where(r => r.status == CoreStatus.UpdateAvailable)
                .Select(r => r.entry)
                .ToList();
        }

        // ── Backup / Revert ───────────────────────────────────────────────────
        public static string BackupPath(string coresFolder, string fileName)
            => Path.Combine(coresFolder, fileName + ".bak");

        public static bool HasBackup(string coresFolder, string fileName)
            => File.Exists(BackupPath(coresFolder, fileName));

        /// <summary>Restores the .bak file, replacing the current .dll.</summary>
        public static void Revert(string coresFolder, string fileName)
        {
            string live   = Path.Combine(coresFolder, fileName);
            string backup = BackupPath(coresFolder, fileName);
            if (!File.Exists(backup))
                throw new FileNotFoundException("No backup found for " + fileName);
            if (File.Exists(live)) File.Delete(live);
            File.Move(backup, live);
        }

        // ── Download ──────────────────────────────────────────────────────────
        /// <summary>
        /// Downloads the zip, backs up the existing .dll to .dll.bak, then extracts.
        /// Reports 0–100 progress.
        /// </summary>
        // A freshly-downloaded core must be a non-empty ELF shared object before we
        // accept it (magic bytes 0x7F 'E' 'L' 'F'). Cheap fail-safe so a corrupt or
        // wrong-content download is never left in place to be dlopen'd.
        private static bool IsElfSharedObject(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 1024) return false;   // a real core is tens of KB+
                Span<byte> magic = stackalloc byte[4];
                using var fs = File.OpenRead(path);
                if (fs.Read(magic) < 4) return false;
                return magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F';
            }
            catch { return false; }
        }

        public async Task DownloadAsync(CoreEntry entry, string coresFolder,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(coresFolder);

            string localPath = Path.Combine(coresFolder, entry.FileName);
            string url       = ZipUrl(entry.FileName);
            // Cores are native code we load in-process — only ever fetch them over
            // TLS from the official libretro buildbot. Fail closed if the base URL is
            // ever misconfigured to plain http.
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to download a core over a non-HTTPS URL: {url}");
            string zipPath   = Path.Combine(Path.GetTempPath(), entry.FileName + ".zip");
            string phase     = "init";

            void Trace(string msg)
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [CoreDownload] {entry.FileName} {msg}";
                System.Diagnostics.Trace.WriteLine(line);
                try
                {
                    string logDir = AppPaths.GetFolder("Logs");
                    string logPath = Path.Combine(logDir, "cores.log");
                    LogRotation.RotateIfLarge(logPath);
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
                catch { /* non-fatal */ }
            }

            try
            {
                Trace($"start url={url} localPath={localPath}");

                // ── Download zip ──
                phase = "http-get";
                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    Trace($"http-status={(int)resp.StatusCode} {resp.StatusCode} content-length={resp.Content.Headers.ContentLength}");
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    long downloaded = 0;

                    phase = "http-stream";
                    await using var src  = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dest = File.Create(zipPath);

                    byte[] buf = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dest.WriteAsync(buf.AsMemory(0, read), ct);
                        downloaded += read;
                        if (total > 0)
                            progress?.Report((int)(downloaded * 100 / total));
                    }
                    Trace($"downloaded={downloaded} bytes to {zipPath}");
                }

                progress?.Report(99);

                // ── Back up existing dll before overwriting ──
                phase = "backup";
                if (File.Exists(localPath))
                {
                    string backup = BackupPath(coresFolder, entry.FileName);
                    File.Copy(localPath, backup, overwrite: true);
                    Trace($"backup created at {backup}");
                }

                // ── Extract dll ──
                phase = "extract";
                bool extracted = false;
                string[] zipEntryNames;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    zipEntryNames = zip.Entries.Select(e => e.FullName).ToArray();
                    foreach (var entry2 in zip.Entries)
                    {
                        if (entry2.Name.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase))
                        {
                            entry2.ExtractToFile(localPath, overwrite: true);
                            extracted = true;
                            break;
                        }
                    }
                }

                if (!extracted)
                {
                    string contents = string.Join(", ", zipEntryNames);
                    Trace($"NO MATCHING ENTRY in zip — entries: [{contents}]");
                    throw new InvalidDataException(
                        $"Zip did not contain '{entry.FileName}'. Contents: [{contents}]");
                }

                // Semantic gate: the HTTPS transfer + zip CRC already fail safe on a
                // corrupted download, but verify the extracted file is actually a
                // non-empty ELF shared object before we keep it. On failure, restore
                // the previous core from the backup we just made so a bad download
                // never replaces a working core. (Authenticity against a buildbot
                // compromise would need libretro-signed cores, which don't exist —
                // HTTPS to the official buildbot is the trust anchor.)
                phase = "validate";
                if (!IsElfSharedObject(localPath))
                {
                    Trace("extracted file is not a valid ELF shared object — rejecting");
                    try { File.Delete(localPath); } catch { }
                    string bak = BackupPath(coresFolder, entry.FileName);
                    if (File.Exists(bak))
                    {
                        try { File.Copy(bak, localPath, overwrite: true); Trace("restored previous core from backup"); }
                        catch (Exception ex) { Trace($"backup restore failed: {ex.Message}"); }
                    }
                    throw new InvalidDataException(
                        $"Downloaded '{entry.FileName}' is not a valid shared object — kept the previous version.");
                }

                // ZipArchiveEntry.ExtractToFile preserves the entry's internal
                // LastWriteTime — stamp with now so the 14-day staleness check
                // is grounded in install time.
                phase = "stamp-mtime";
                try { File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow); }
                catch (Exception ex)
                {
                    Trace($"could not stamp mtime: {ex.Message}");
                }

                Trace($"extracted=true mtimeUtc={File.GetLastWriteTimeUtc(localPath):o}");

                phase = "cleanup";
                try { File.Delete(zipPath); } catch { }

                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Trace($"FAILED in phase={phase} type={ex.GetType().FullName} msg={ex.Message}");
                if (ex.InnerException != null)
                    Trace($"  inner type={ex.InnerException.GetType().FullName} msg={ex.InnerException.Message}");
                throw;
            }
        }
    }
}
