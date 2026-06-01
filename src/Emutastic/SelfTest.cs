using System;
using System.IO;
using System.Linq;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic
{
    // Headless runtime self-test for the M3 data layer — invoked via
    // `Emutastic --selftest-library <rom>`. Proves ROM identification + the SQLite
    // library round-trip work at runtime (no Avalonia, no network).
    internal static class SelfTest
    {
        public static void RunLibrary(string? romPath)
        {
            Console.WriteLine("=== M3 library self-test ===");

            if (!string.IsNullOrEmpty(romPath) && File.Exists(romPath))
            {
                Console.WriteLine($"ROM: {romPath}");
                Console.WriteLine($"  RomService.DetectConsole = {RomService.DetectConsole(romPath)}");
                Console.WriteLine($"  RomService.HashRom       = {RomService.HashRom(romPath)}");
            }
            else Console.WriteLine("(no ROM passed — testing DB round-trip only)");

            var db = new DatabaseService();
            int before = db.GetAllGames().Count;

            var g = new Game { Title = "SelfTest Game", Console = "NES", RomPath = romPath ?? "/tmp/selftest.nes" };
            db.InsertGame(g);

            var all = db.GetAllGames();
            var inserted = all.FirstOrDefault(x => x.Title == "SelfTest Game");
            Console.WriteLine($"games: before={before}, after insert={all.Count}");
            Console.WriteLine($"  inserted: id={inserted?.Id}, title='{inserted?.Title}', console={inserted?.Console}");

            if (inserted != null)
            {
                db.DeleteGame(inserted.Id);
                Console.WriteLine($"  cleaned up; games now={db.GetAllGames().Count}");
            }

            Console.WriteLine($"db file: {Path.Combine(AppPaths.GetFolder(), "library.db")}");
            bool ok = inserted != null && inserted.Console == "NES";
            Console.WriteLine(ok ? "=== PASS (identify + DB round-trip) ===" : "=== FAIL ===");
        }
    }
}
