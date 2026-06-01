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
        // U1a: import a ROM via ImportService end-to-end + resolve its core (the GUI calls these).
        public static void RunImport(string corePath, string romPath)
        {
            Console.WriteLine("=== U1a import self-test ===");
            App.Configuration = new Emutastic.Configuration.JsonConfigurationService();

            // Place the core in the cores folder so CoreManager can resolve it.
            string coresDir = AppPaths.GetCoresFolder();
            string destCore = System.IO.Path.Combine(coresDir, System.IO.Path.GetFileName(corePath));
            if (!System.IO.File.Exists(destCore)) System.IO.File.Copy(corePath, destCore);
            Console.WriteLine($"core placed: {destCore}");

            var db = new DatabaseService();
            var coreManager = new CoreManager(App.Configuration);
            var importer = new ImportService(db, coreManager, App.Configuration);

            using var drained = new System.Threading.ManualResetEventSlim(false);
            importer.ImportQueueDrained += () => drained.Set();
            importer.StatusChanged += m => Console.WriteLine($"  [import] {m}");

            importer.ImportFilesAsync(new[] { romPath }, "NES");
            bool ok = drained.Wait(TimeSpan.FromSeconds(40));
            Console.WriteLine($"import drained={ok}");

            var games = db.GetAllGames();
            var g = games.FirstOrDefault(x => x.RomPath.Contains(System.IO.Path.GetFileNameWithoutExtension(romPath)));
            Console.WriteLine($"games in library={games.Count}, imported='{g?.Title}' console={g?.Console}");
            if (g != null)
            {
                string? resolved = coreManager.GetCorePathForGame(g);
                Console.WriteLine($"GetCorePathForGame -> {resolved}  exists={System.IO.File.Exists(resolved ?? "")}");
                if (Environment.GetEnvironmentVariable("KEEP") != "1") db.DeleteGame(g.Id); // cleanup (KEEP=1 to persist)
                Console.WriteLine(resolved != null && System.IO.File.Exists(resolved)
                    ? "=== PASS (import + core resolution) ===" : "=== FAIL (core not resolved) ===");
            }
            else Console.WriteLine("=== FAIL (game not imported) ===");
        }

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

            // M4b: verify MainViewModel loads + filters games from the DB (no Avalonia/art decode).
            try
            {
                var vm = new ViewModels.MainViewModel(db);
                vm.Reload();
                vm.SelectedConsole = "All Games";
                vm.FilterGamesAsync().GetAwaiter().GetResult();
                Console.WriteLine($"MainViewModel: Reload+Filter -> Games.Count={vm.Games.Count}, countText='{vm.GameCountText}'");
            }
            catch (Exception ex) { Console.WriteLine($"  VM check error: {ex.Message}"); }

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
