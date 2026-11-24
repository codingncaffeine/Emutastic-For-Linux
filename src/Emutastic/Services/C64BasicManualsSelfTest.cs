using System;
using System.IO;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>`Emutastic --selftest-c64-manuals --portable` (network): downloads both Commodore 64 BASIC manuals
    /// through C64BasicManuals, as the right-click items do, and checks the files. Exit 0 = pass.</summary>
    public static class C64BasicManualsSelfTest
    {
        public static int Run()
        {
            int fails = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }
            Console.WriteLine("=== C64 BASIC manuals self-test ===");
            Check(C64BasicManuals.IsBasicEntry(new Game { Console = "C64", RomHash = C64BasicEntry.Hash }), "the BASIC entry is recognized");
            Check(!C64BasicManuals.IsBasicEntry(new Game { Console = "C64", RomHash = "abc" }), "another C64 game is not");
            foreach (var m in C64BasicManuals.All)
            {
                try { File.Delete(C64BasicManuals.PathFor(m)); } catch { }
                Check(!C64BasicManuals.Has(m), $"{m.Title}: not there before");
                double last = 0;
                try { C64BasicManuals.DownloadAsync(m, p => last = p).GetAwaiter().GetResult(); }
                catch (Exception ex) { Check(false, $"{m.Title}: download ({ex.Message})"); continue; }
                var f = new FileInfo(C64BasicManuals.PathFor(m));
                Check(C64BasicManuals.Has(m) && f.Length > 1_000_000, $"{m.Title}: downloaded, {f.Length / 1048576.0:0.0} MB ({f.FullName})");
                Check(last >= 99.9, $"{m.Title}: progress reached 100 ({last:0})");
                Check(!File.Exists(f.FullName + ".part"), $"{m.Title}: no partial file left");
            }
            Console.WriteLine(fails == 0 ? "=== PASS ===" : $"=== FAIL ({fails}) ===");
            return fails == 0 ? 0 : 1;
        }
    }
}
