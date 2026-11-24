using System;
using System.IO;
using System.Threading;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// `Emutastic --selftest-c64-harddrive CORE.so [--portable]`: makes a 20 MB hard drive for the BASIC entry
    /// through C64HardDrive (the same calls the New Blank Hard Drive window makes) with the System folder's
    /// files, then checks the file, its name and its size; and that cancelling removes a half-made drive.
    /// Needs the C64 ROMs, the CMD HD boot ROM and utilities disk in the System folder. Exit 0 = pass.
    /// </summary>
    public static class C64HardDriveSelfTest
    {
        public static int Run(string corePath)
        {
            int fails = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
            Console.WriteLine("=== C64 hard drive self-test ===");
            string? missing = C64HardDrive.Missing(corePath);
            Check(missing == "", $"the core can make drives and nothing is missing ({missing ?? "no export"})");
            if (missing != "") return 1;
            var game = new Game { Title = C64BasicEntry.Title, Console = "C64", RomPath = C64BasicEntry.MarkerPath };
            string path = C64HardDrive.PathFor(game);
            Check(Path.GetFileName(path) == "Kernal64.dhd" && path.Contains(Path.Combine("Saves", "C64")),
                  $"the BASIC entry's drive is Saves/C64/Kernal64.dhd ({path})");
            try { File.Delete(path); } catch { }
            int last = -1;
            int parts = C64HardDrive.Make(corePath, path, 20L << 20, p => last = p, CancellationToken.None);
            Check(parts == 2, $"a 20 MB drive gets 2 partitions (got {parts})");
            Check(last == 100, "progress reached 100");
            Check(File.Exists(path) && new FileInfo(path).Length == 20L << 20, "the file is there, 20 MB long");
            string half = path + ".cancel";
            try
            {
                using var cts = new CancellationTokenSource();
                C64HardDrive.Make(corePath, half, 20L << 20, p => { if (p >= 20) cts.Cancel(); }, cts.Token);
                Check(false, "cancelling stops the making");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("cancel"), $"cancelling stops the making ({ex.Message})"); }
            Check(!File.Exists(half), "a cancelled drive leaves no file");
            Console.WriteLine(fails == 0 ? "=== PASS ===" : $"=== FAIL ({fails}) ===");
            return fails == 0 ? 0 : 1;
        }
    }
}
