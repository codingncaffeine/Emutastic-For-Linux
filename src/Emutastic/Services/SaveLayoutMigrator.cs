using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emutastic.Services
{
    /// <summary>
    /// One-time migration from the old FLAT save layout (every core dumped its
    /// battery saves into a single Saves/ folder) to the per-console layout
    /// (Saves/&lt;Console&gt;/), the Linux analog of upstream's BatterySaves/&lt;Console&gt;/.
    ///
    /// Why: cloud sync now backs up console-managed memory cards / save trees, and
    /// it attributes each file to a console by its top-level folder. A flat folder
    /// has no console attribution, so saves are reorganized per console — which also
    /// just keeps the folder tidy. EmulatorSession now points each core's
    /// save_directory at Saves/&lt;Console&gt;/, so the on-disk files must move to match
    /// or cores would start from blank saves.
    ///
    /// What moves (everything else is left untouched, never deleted):
    ///  - Known core save-tree roots (PSP/, User/ = Dolphin, dc/ = flycast,
    ///    Azahar/ = 3DS, same_cdi/, opera/, fbneo/) → Saves/&lt;mapped console&gt;/&lt;root&gt;.
    ///  - Loose per-game battery files (&lt;romstem&gt;.srm/.dsv/.mcr/.brm/… at the flat
    ///    root) → Saves/&lt;that game's console&gt;/, resolved via the library DB by stem.
    ///
    /// Files we can't confidently attribute (e.g. shared Sega backup-RAM carts like
    /// 4Mbit_cart.brm) are left at the root and logged — the user keeps the data and
    /// can sort it by hand. The pass is guarded by a marker file so it runs once.
    /// </summary>
    public static class SaveLayoutMigrator
    {
        // Marker written after a successful pass; its presence means "already per-console".
        private const string MarkerName = ".per-console-layout";

        // Core save-tree root folder → Emutastic console name. These folders are
        // named by the CORE (not the console), so the same root always belongs to
        // one console regardless of which game wrote it.
        private static readonly Dictionary<string, string> RootToConsole =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["PSP"]      = "PSP",        // PPSSPP save tree
                ["User"]     = "GameCube",  // Dolphin memcards + Wii NAND
                ["dc"]       = "Dreamcast", // flycast VMUs
                ["Azahar"]   = "3DS",       // Azahar NAND/SDMC
                ["same_cdi"] = "CDi",       // same_cdi nvram/cfg
                ["opera"]    = "3DO",       // 4DO/Opera per-game NVRAM
                ["fbneo"]    = "Arcade",    // FBNeo arcade NVRAM (NeoGeo also uses fbneo;
                                            // its existing saves land under Arcade and are logged)
            };

        /// <summary>
        /// Runs the migration once (no-op if the marker exists or there's nothing to
        /// move). Safe to call unconditionally at startup; never throws.
        /// </summary>
        public static void RunOnce()
        {
            try
            {
                string root = AppPaths.GetFolder("Saves");   // creates Saves/ if missing
                string marker = Path.Combine(root, MarkerName);
                if (File.Exists(marker)) return;

                // Snapshot the flat root BEFORE we create any console subfolders, so
                // freshly-created console dirs aren't re-processed.
                string[] topDirs = Directory.GetDirectories(root);
                string[] topFiles = Directory.GetFiles(root);

                int moved = 0, left = 0;

                // Per-game battery files: stem → console, from the library DB.
                Dictionary<string, string> stemToConsole = BuildStemMap();

                // 1) Loose files at the flat root, attributed by ROM stem.
                foreach (string file in topFiles)
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals(MarkerName, StringComparison.Ordinal)) continue;
                    string stem = Path.GetFileNameWithoutExtension(name);
                    if (stemToConsole.TryGetValue(stem, out string? console)
                        && !string.IsNullOrEmpty(console))
                    {
                        if (MoveInto(root, console, file, name)) moved++; else left++;
                    }
                    else
                    {
                        left++;
                        CloudSyncLog.Write($"Save migration: left unattributed file at root: {name}");
                    }
                }

                // 2) Known core save-tree roots.
                foreach (string dir in topDirs)
                {
                    string name = Path.GetFileName(dir);
                    if (RootToConsole.TryGetValue(name, out string? console))
                    {
                        if (MoveInto(root, console, dir, name)) moved++; else left++;
                    }
                    // Unknown dirs (already-migrated console folders on a re-run that
                    // somehow lost its marker, or user-made folders) are left alone.
                }

                File.WriteAllText(marker, DescribeMarker());
                if (moved > 0 || left > 0)
                    CloudSyncLog.Write($"Save layout migrated to per-console: {moved} moved, {left} left at root");
            }
            catch (Exception ex)
            {
                // Best-effort: a failed migration must never block startup. No marker is
                // written, so the next launch retries.
                CloudSyncLog.Write($"Save layout migration failed: {ex.Message}");
            }
        }

        private static Dictionary<string, string> BuildStemMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = new DatabaseService();
                foreach (var g in db.GetGamesSyncMap())
                {
                    if (string.IsNullOrEmpty(g.Console) || string.IsNullOrEmpty(g.RomPath)) continue;
                    string romPath = AppPaths.FromStoragePath(g.RomPath);
                    string stem = Path.GetFileNameWithoutExtension(romPath);
                    if (string.IsNullOrEmpty(stem)) continue;
                    map[stem] = g.Console;

                    // ROM hacks key their .srm by stem + first 8 hash chars
                    // (EmulatorSession's rule, mirrored in LocalSrmPathFor).
                    if (g.HasPatch && !string.IsNullOrEmpty(g.RomHash))
                        map[stem + "." + g.RomHash[..Math.Min(8, g.RomHash.Length)]] = g.Console;
                }
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Save migration: DB stem map unavailable ({ex.Message}); loose files left at root");
            }
            return map;
        }

        /// <summary>
        /// Moves <paramref name="source"/> (file or directory) into Saves/&lt;console&gt;/&lt;name&gt;.
        /// Handles the self-nest case (a core root named like its console, e.g. PSP →
        /// PSP/PSP) via a sibling temp hop, and never clobbers an existing target.
        /// </summary>
        private static bool MoveInto(string root, string console, string source, string name)
        {
            try
            {
                string consoleDir = Path.Combine(root, console);
                string dest = Path.Combine(consoleDir, name);

                // Already in place? (idempotency / re-run safety.)
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest),
                        StringComparison.Ordinal))
                    return false;

                bool isDir = Directory.Exists(source);

                // Self-nest: dest sits under source (Saves/PSP → Saves/PSP/PSP). Move the
                // source out to a sibling temp first so the rename target isn't a descendant.
                string actualSource = source;
                if (isDir)
                {
                    string destFull = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
                    string srcFull = Path.GetFullPath(source) + Path.DirectorySeparatorChar;
                    if (destFull.StartsWith(srcFull, StringComparison.Ordinal))
                    {
                        string tmp = Path.Combine(root, ".mig_" + name);
                        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
                        Directory.Move(source, tmp);
                        actualSource = tmp;
                    }
                }

                Directory.CreateDirectory(consoleDir);

                if (Directory.Exists(dest) || File.Exists(dest))
                {
                    CloudSyncLog.Write($"Save migration: target exists, left in place: {console}/{name}");
                    // Restore the temp hop if we did one, so nothing is orphaned.
                    if (!string.Equals(actualSource, source, StringComparison.Ordinal)
                        && Directory.Exists(actualSource))
                        Directory.Move(actualSource, source);
                    return false;
                }

                if (isDir) Directory.Move(actualSource, dest);
                else File.Move(actualSource, dest);
                return true;
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Save migration: failed to move {name} → {console}/: {ex.Message}");
                return false;
            }
        }

        private static string DescribeMarker() =>
            "Emutastic per-console save layout marker.\n" +
            "Battery saves live under Saves/<Console>/ (one folder per system).\n" +
            "Delete this file to force a one-time re-migration on next launch.\n";
    }
}
