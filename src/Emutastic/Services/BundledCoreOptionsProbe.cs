using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// The option lists of the cores that ship with the app (BundledCores: Midway Verdant), known before any game has
    /// run: the core is loaded on its own for a moment and handed an environment callback, and the options it announces
    /// there (SET_VARIABLES, from retro_set_environment, before retro_init) are saved as the schema a game launch would
    /// save. Re-read when the core file is newer than the saved schema, so a new core's options appear at once. Off the
    /// UI thread; nothing is initialised or run in the core.
    /// </summary>
    public static class BundledCoreOptionsProbe
    {
        private const uint ENV_SET_VARIABLES = 16;

        [StructLayout(LayoutKind.Sequential)]
        private struct RetroVariable { public IntPtr key; public IntPtr value; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetEnvironmentFn(retro_environment_t cb);

        private static Task? _running;
        private static readonly object _gate = new();

        /// <summary>Starts the probe once per app run (later calls return the same task).</summary>
        public static Task EnsureAsync()
        {
            lock (_gate) return _running ??= Task.Run(ProbeAll);
        }

        internal static void ProbeAll()
        {
            string dir = CoreManager.BundledCoresFolder;
            if (!Directory.Exists(dir)) return;
            var store = new CoreOptionsService();
            foreach (string path in Directory.EnumerateFiles(dir, "*_libretro.so"))
            {
                string coreName = Path.GetFileNameWithoutExtension(path);
                try
                {
                    string schemaPath = store.SchemaPath(coreName);
                    if (File.Exists(schemaPath) && File.GetLastWriteTimeUtc(schemaPath) >= File.GetLastWriteTimeUtc(path)) continue;
                    var options = ReadOptions(path);
                    if (options.Count == 0) continue;
                    store.SaveSchema(coreName, new CoreOptionsSchema
                    {
                        DisplayName = CoreOptionsService.DisplayNameFor(coreName),
                        ConsoleName = CoreOptionsService.ConsoleFor(coreName),
                        Options = options,
                    });
                    Trace.WriteLine($"[CoreOptions] {coreName}: {options.Count} options read from the bundled core");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[CoreOptions] {coreName}: the bundled core's options could not be read: {ex.Message}");
                }
            }
        }

        private static List<CoreOptionEntry> ReadOptions(string path)
        {
            var options = new List<CoreOptionEntry>();
            IntPtr lib = NativeLibrary.Load(path);
            try
            {
                var setEnv = Marshal.GetDelegateForFunctionPointer<SetEnvironmentFn>(NativeLibrary.GetExport(lib, "retro_set_environment"));
                retro_environment_t cb = (cmd, data) =>
                {
                    if ((cmd & 0xFFFF) != ENV_SET_VARIABLES || data == IntPtr.Zero) return false;   // everything else: not offered
                    options.Clear();
                    int stride = Marshal.SizeOf<RetroVariable>();
                    for (int n = 0; n < 4096; n++)
                    {
                        var v = Marshal.PtrToStructure<RetroVariable>(IntPtr.Add(data, n * stride));
                        if (v.key == IntPtr.Zero) break;
                        string? key = Marshal.PtrToStringAnsi(v.key), desc = Marshal.PtrToStringAnsi(v.value);
                        if (string.IsNullOrEmpty(key) || desc == null) continue;
                        // "Description; first|second|...", as EmulatorSession.ParseSetVariables reads it at launch.
                        int semi = desc.IndexOf(';');
                        string[] vals = (semi >= 0 ? desc[(semi + 1)..] : desc).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (vals.Length == 0) continue;
                        options.Add(new CoreOptionEntry { Key = key, Description = semi >= 0 ? desc[..semi].Trim() : key, ValidValues = vals, DefaultValue = vals[0] });
                    }
                    return true;
                };
                setEnv(cb);
                GC.KeepAlive(cb);
            }
            finally
            {
                NativeLibrary.Free(lib);
            }
            return options;
        }
    }
}
