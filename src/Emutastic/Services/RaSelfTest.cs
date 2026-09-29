using System;
using static Emutastic.Services.RcheevosInterop;

namespace Emutastic.Services
{
    /// <summary>
    /// `Emutastic --ra-selftest`: proves the RetroAchievements native foundation
    /// without a window, network, or login. Exit 0 = healthy.
    ///   1. VerifyAbi — every marshaled struct layout matches the numbers the
    ///      native checkabi harness printed for this librcheevos.so build.
    ///   2. rc_client create/configure/destroy round-trip through the real .so
    ///      (catches load failures, missing exports, calling-convention slips).
    /// </summary>
    internal static class RaSelfTest
    {
        public static int Run(string[] discs)
        {
            Console.WriteLine($"[ra-selftest] UA: {EmutasticUserAgent.Build("test core", "v1.0")}");

            string? abi = VerifyAbi();
            if (abi != null)
            {
                Console.WriteLine($"[ra-selftest] FAIL: ABI mismatch — {abi}");
                return 1;
            }
            Console.WriteLine("[ra-selftest] ABI: all marshaled layouts match checkabi");

            try
            {
                ReadMemoryFunc readMem = (addr, buf, num, cl) => 0;
                ServerCallFunc serverCall = (req, cb, cbData, cl) => { };
                IntPtr client = rc_client_create(readMem, serverCall);
                if (client == IntPtr.Zero)
                {
                    Console.WriteLine("[ra-selftest] FAIL: rc_client_create returned null");
                    return 2;
                }

                rc_client_set_hardcore_enabled(client, 1);
                int hc = rc_client_get_hardcore_enabled(client);
                bool loaded = rc_client_is_game_loaded(client) != 0;
                rc_client_destroy(client);

                if (hc != 1)
                {
                    Console.WriteLine($"[ra-selftest] FAIL: hardcore round-trip returned {hc}");
                    return 3;
                }
                Console.WriteLine($"[ra-selftest] rc_client create/configure/destroy OK (hardcore={hc}, gameLoaded={loaded})");

                // 3. Optional disc hashing through the app's cdreader:
                //    `--ra-selftest <consoleId>:<path> ...` (12 = PS1). Every disc
                //    identification goes through these callbacks, so a layout slip
                //    in RcHashCdreader crashes here the way it crashes a launch.
                if (discs.Length > 0)
                {
                    RcheevosChdCdReader.InstallInto(IntPtr.Zero);
                    foreach (string spec in discs)
                    {
                        int colon = spec.IndexOf(':');
                        if (colon <= 0 || !uint.TryParse(spec.AsSpan(0, colon), out uint consoleId))
                        {
                            Console.WriteLine($"[ra-selftest] FAIL: bad disc spec '{spec}' (want <consoleId>:<path>)");
                            return 6;
                        }
                        string path = spec[(colon + 1)..];
                        var hash = new byte[33];
                        if (rc_hash_generate_from_file(hash, consoleId, path) == 0)
                        {
                            Console.WriteLine($"[ra-selftest] FAIL: no hash for {path}");
                            return 7;
                        }
                        string hex = System.Text.Encoding.ASCII.GetString(hash, 0, 32);
                        Console.WriteLine($"[ra-selftest] hash {hex} console={consoleId} {System.IO.Path.GetFileName(path)}");
                    }
                }
                Console.WriteLine("[ra-selftest] PASS");
                return 0;
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"[ra-selftest] FAIL: librcheevos.so not loadable — {ex.Message}");
                return 4;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ra-selftest] FAIL: {ex.GetType().Name}: {ex.Message}");
                return 5;
            }
        }
    }
}
