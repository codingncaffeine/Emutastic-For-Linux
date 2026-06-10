using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Schema saved once per core (after first game launch) so the Preferences
    /// UI can display options without needing a game to be running.
    /// </summary>
    public class CoreOptionsSchema
    {
        public string DisplayName { get; set; } = "";
        public string ConsoleName { get; set; } = "";
        public List<CoreOptionEntry> Options { get; set; } = new();
    }

    /// <summary>
    /// Persists per-core option schemas and user-chosen values to
    /// %AppData%\Emutastic\CoreOptions\.
    /// </summary>
    public class CoreOptionsService
    {
        private readonly string _dir;
        private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

        public CoreOptionsService()
        {
            _dir = AppPaths.GetFolder("CoreOptions");
        }

        /// <summary>Human display name for a core dll/so file name or stem ("parallel_n64_libretro.so"
        /// or "parallel_n64_libretro" → "Parallel N64"). Used by the Preferences core lists and as the
        /// schema DisplayName captured at game launch.</summary>
        public static string DisplayNameFor(string dllName)
        {
            string name = dllName
                .Replace("_libretro.so", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_libretro.dll", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_libretro", "", StringComparison.OrdinalIgnoreCase);
            return name switch
            {
                "nestopia" => "Nestopia", "fceumm" => "FCE Ultra MM", "quicknes" => "QuickNES",
                "snes9x" => "Snes9x", "snes9x2002" => "Snes9x 2002", "snes9x2005" => "Snes9x 2005",
                "snes9x2005_plus" => "Snes9x 2005 Plus", "snes9x2010" => "Snes9x 2010", "bsnes" => "bsnes",
                "parallel_n64" => "Parallel N64", "mupen64plus_next" => "Mupen64Plus-Next", "dolphin" => "Dolphin",
                "mgba" => "mGBA", "gambatte" => "Gambatte", "sameboy" => "SameBoy", "desmume" => "DeSmuME",
                "melonds" => "melonDS", "azahar" => "Azahar (3DS)", "mednafen_vb" => "Mednafen Virtual Boy",
                "genesis_plus_gx" => "Genesis Plus GX", "picodrive" => "PicoDrive", "kronos" => "Kronos",
                "mednafen_saturn" => "Mednafen Saturn", "yabause" => "Yabause", "mednafen_psx" => "Mednafen PSX (Beetle)",
                "pcsx_rearmed" => "PCSX-ReARMed", "ppsspp" => "PPSSPP", "mednafen_pce" => "Mednafen PCE",
                "mednafen_pce_fast" => "Mednafen PCE Fast", "mednafen_ngp" => "Mednafen Neo Geo Pocket",
                "gearcoleco" => "GearColeco", "stella" => "Stella", "stella2014" => "Stella 2014", "stella2023" => "Stella 2023",
                "prosystem" => "ProSystem", "flycast" => "Flycast (Dreamcast)", "virtualjaguar" => "Virtual Jaguar",
                "bluemsx" => "blueMSX", "vecx" => "Vecx", "opera" => "Opera (3DO)", "same_cdi" => "SAME CDi",
                "fbneo" => "FBNeo (Final Burn Neo)", "geolith" => "Geolith (Neo Geo)",
                "mame2003_plus" => "MAME 2003-Plus", "mednafen_psx_hw" => "Mednafen PSX HW (Beetle)",
                _ => name.Length == 0 ? name : char.ToUpper(name[0]) + name[1..].Replace("_", " "),
            };
        }

        // ── Schema ────────────────────────────────────────────────────────────────

        public void SaveSchema(string coreName, CoreOptionsSchema schema)
        {
            try
            {
                File.WriteAllText(Path.Combine(_dir, $"{coreName}.schema.json"),
                    JsonSerializer.Serialize(schema, _json));
            }
            catch { /* non-fatal */ }
        }

        public CoreOptionsSchema? LoadSchema(string coreName)
        {
            string path = Path.Combine(_dir, $"{coreName}.schema.json");
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<CoreOptionsSchema>(File.ReadAllText(path)); }
            catch { return null; }
        }

        // Fallback for schemas saved before ConsoleName was added to the model.
        private static readonly Dictionary<string, string> _coreToConsole = new()
        {
            ["desmume_libretro"]          = "NDS",
            ["dolphin_libretro"]          = "GameCube",
            ["flycast_libretro"]          = "Dreamcast",
            ["gearcoleco_libretro"]       = "ColecoVision",
            ["genesis_plus_gx_libretro"]  = "Genesis",
            ["kronos_libretro"]           = "Saturn",
            ["mednafen_ngp_libretro"]     = "NGP",
            ["mame2003_plus_libretro"]    = "Arcade",
            ["mednafen_pce_libretro"]     = "TG16",
            ["mednafen_psx_libretro"]     = "PS1",
            ["mednafen_vb_libretro"]      = "VirtualBoy",
            ["mgba_libretro"]             = "GBA",
            ["nestopia_libretro"]         = "NES",
            ["opera_libretro"]            = "3DO",
            ["parallel_n64_libretro"]     = "N64",
            ["pcsx2_libretro"]            = "PS2",
            ["picodrive_libretro"]        = "Sega32X",
            ["ppsspp_libretro"]           = "PSP",
            ["prosystem_libretro"]        = "Atari7800",
            ["snes9x_libretro"]           = "SNES",
            ["stella_libretro"]           = "Atari2600",
            ["vecx_libretro"]             = "Vectrex",
            ["virtualjaguar_libretro"]    = "Jaguar",
        };

        /// <summary>Returns (coreName, displayName, consoleName) tuples for every core that has a saved schema.</summary>
        public List<(string CoreName, string DisplayName, string ConsoleName)> GetCoresWithSchema()
        {
            try
            {
                return Directory.EnumerateFiles(_dir, "*.schema.json")
                    .Select(f =>
                    {
                        string cn = Path.GetFileNameWithoutExtension(
                            Path.GetFileNameWithoutExtension(f)); // strip .schema then .json
                        var schema = LoadSchema(cn);
                        string dn = schema?.DisplayName is { Length: > 0 } d ? d : cn;
                        string console = schema?.ConsoleName is { Length: > 0 } c
                            ? c
                            : _coreToConsole.GetValueOrDefault(cn, "");
                        return (cn, dn, console);
                    })
                    .OrderBy(x => x.Item2)
                    .ToList();
            }
            catch { return new(); }
        }

        // ── Values ────────────────────────────────────────────────────────────────

        public Dictionary<string, string> LoadValues(string coreName)
        {
            string path = Path.Combine(_dir, $"{coreName}.values.json");
            if (!File.Exists(path)) return new();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path)) ?? new();
            }
            catch { return new(); }
        }

        public void SaveValues(string coreName, Dictionary<string, string> values)
        {
            try
            {
                // Merge with existing saved values so callers that save a single option
                // don't wipe out all other previously-saved options.
                var existing = LoadValues(coreName);
                foreach (var kv in values)
                    existing[kv.Key] = kv.Value;

                File.WriteAllText(Path.Combine(_dir, $"{coreName}.values.json"),
                    JsonSerializer.Serialize(existing, _json));
            }
            catch { /* non-fatal */ }
        }

        public void DeleteValues(string coreName)
        {
            try
            {
                string path = Path.Combine(_dir, $"{coreName}.values.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
