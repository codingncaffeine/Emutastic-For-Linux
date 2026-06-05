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
        public static int Run()
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
