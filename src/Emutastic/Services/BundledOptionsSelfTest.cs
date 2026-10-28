using System;
using System.IO;
using System.Linq;

namespace Emutastic.Services
{
    /// <summary>
    /// `Emutastic --selftest-bundled-options --portable` from a fresh copy of the build: the bundled Midway Verdant core's
    /// options reach CoreOptions/ with no game run (BundledCoreOptionsProbe), under its proper name and console, and a
    /// saved list newer than the core is left alone. No window. Exit 0 = pass, 1 = fail, 2 = incomplete.
    /// </summary>
    public static class BundledOptionsSelfTest
    {
        public static int Run()
        {
            if (!AppPaths.IsPortable) { Console.WriteLine("INCOMPLETE: run with --portable from a copy of the build"); return 2; }
            string core = Path.Combine(CoreManager.BundledCoresFolder, "midway_verdant_libretro.so");
            if (!File.Exists(core)) { Console.WriteLine($"INCOMPLETE: no bundled core at {core}"); return 2; }
            var store = new CoreOptionsService();
            string schemaPath = store.SchemaPath("midway_verdant_libretro");
            File.Delete(schemaPath);  // the copy's PortableData: a first run
            int failed = 0;
            void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failed++; }

            BundledCoreOptionsProbe.EnsureAsync().Wait();
            var schema = store.LoadSchema("midway_verdant_libretro");
            Check(schema != null, "the option list is saved with no game run");
            if (schema == null) { Console.WriteLine("1 FAILED"); return 1; }  // nothing below can be checked
            {
                var keys = schema.Options.Select(o => o.Key).ToHashSet();
                Check(schema.DisplayName == "Midway Verdant", $"listed as \"{schema.DisplayName}\"");
                Check(schema.ConsoleName == "Xbox", $"under the console \"{schema.ConsoleName}\"");
                foreach (string k in new[] { "midway_verdant_cpu", "midway_verdant_resolution", "midway_verdant_audio", "midway_verdant_shaders",
                                             "midway_verdant_anisotropy", "midway_verdant_disc_speed", "midway_verdant_pipeline", "midway_verdant_pads",
                                             "midway_verdant_drive" })
                    Check(keys.Contains(k), $"has {k}");
                var res = schema.Options.FirstOrDefault(o => o.Key == "midway_verdant_resolution");
                Check(res != null && res.ValidValues.Contains("6x") && res.DefaultValue == res.ValidValues[0], "the resolution offers 6x, its default the first value");
                Console.WriteLine($"      {schema.Options.Count} options");
            }

            // A saved list newer than the core is not read again (a second probe pass leaves the file alone).
            DateTime before = File.GetLastWriteTimeUtc(schemaPath);
            BundledCoreOptionsProbe.ProbeAll();
            Check(File.GetLastWriteTimeUtc(schemaPath) == before, "a list newer than the core is left alone");
            // An older one is read again (a newer core: its new options appear).
            File.SetLastWriteTimeUtc(schemaPath, File.GetLastWriteTimeUtc(core).AddHours(-1));
            BundledCoreOptionsProbe.ProbeAll();
            Check(File.GetLastWriteTimeUtc(schemaPath) > File.GetLastWriteTimeUtc(core).AddHours(-1), "a list older than the core is read again");

            Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
            return failed == 0 ? 0 : 1;
        }
    }
}
