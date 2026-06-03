using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Emutastic.Configuration;
using Emutastic.Emulator;
using Emutastic.Services;

namespace Emutastic
{
    /// <summary>
    /// The separate game process: <c>Emutastic --game-host &lt;core&gt; &lt;rom&gt; [--console X]
    /// [--fullscreen] [--results path]</c>. Dispatched from <see cref="Program.Main"/> BEFORE Avalonia is
    /// ever built, so this process has no Avalonia/X11/Skia in it — the clean environment the GlPresenter
    /// spike proved (Avalonia + SDL-GL in one process hangs after present #1; see
    /// docs/gl-present-phase1-host-process-design.md). It reuses the production EmulatorSession (GL
    /// present) and owns the SDL-GL window, audio, input, SRAM, and the present loop. The Avalonia library
    /// app spawns + supervises it (GameHostLauncher); only launch args in + a results file out cross the
    /// boundary — no frames/audio/input.
    /// </summary>
    public static class GameHost
    {
        // Linux: deliver SIGTERM to this process when the parent (library) dies, so a fullscreen game can't
        // outlive a killed supervisor. Combined with SRAM autosave, an orphaned child still has a save.
        [DllImport("libc", SetLastError = true)]
        private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
        private const int PR_SET_PDEATHSIG = 1, SIGTERM = 15;

        public static int Run(string[] args)
        {
            // args[0] == "--game-host"; [1]=core, [2]=rom; flags: --console <name> --fullscreen --results <path>
            string? core = args.Length > 1 ? args[1] : null;
            string? rom  = args.Length > 2 ? args[2] : null;
            string console = "", resultsPath = "";
            bool fullscreen = false, parentStdin = false;
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--console":      if (i + 1 < args.Length) console = args[++i]; break;
                    case "--fullscreen":   fullscreen = true; break;
                    case "--results":      if (i + 1 < args.Length) resultsPath = args[++i]; break;
                    case "--parent-stdin": parentStdin = true; break;   // supervisor holds our stdin; EOF = graceful quit
                }
            }

            try { prctl(PR_SET_PDEATHSIG, SIGTERM, 0, 0, 0); } catch { /* non-Linux / unavailable */ }

            // Same config bootstrap the Avalonia App does (App.axaml.cs) — portable mode FIRST so the child
            // resolves the same config/saves dir, then load the shared JSON config. No Avalonia involved.
            AppPaths.DetectPortableMode(args);
            App.Configuration ??= new JsonConfigurationService();
            try { App.Configuration.LoadAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Trace.WriteLine($"[Host] config load failed: {ex.Message}"); }

            var log = EmuLog.Setup("emulator-host.log");
            Trace.WriteLine($"[Host] === game-host start: core={core} rom={rom} console='{console}' fullscreen={fullscreen} ===");

            if (string.IsNullOrEmpty(core) || string.IsNullOrEmpty(rom) || !File.Exists(core) || !File.Exists(rom))
            {
                Trace.WriteLine("[Host] missing/invalid core or rom");
                WriteResults(resultsPath, 2, 0);
                return 2;
            }

            // Force the GL present path (this whole process exists to run it) and the start-fullscreen flag.
            Environment.SetEnvironmentVariable("EMUTASTIC_PRESENT", "gl");
            if (fullscreen) Environment.SetEnvironmentVariable("EMUTASTIC_GL_FULLSCREEN", "1");

            // Use native Wayland EGL (RetroArch's backend — its log shows GL context "wayland") instead of
            // SDL3's default Xwayland/GLX fallback. The x11/GLX path does NOT get clean FIFO vsync here
            // (eglGetCurrentDisplay=null, swap can't be set to FIFO); native Wayland does (eglSwapInterval=1
            // succeeds). Only override on a Wayland session when the user hasn't forced a driver.
            bool onWayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDL_VIDEODRIVER")) && onWayland)
                Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "wayland");

            // Default to OUR OWN xdg_toplevel (the proven windowed-60 fix) on Wayland — SDL's surface caps at
            // ~55 windowed; a bare own top-level (RetroArch's model) hits 60. SDL stays for gamepad + audio.
            // EMUTASTIC_GL_TOPLEVEL=0 reverts to the SDL-window present path for A/B.
            if (onWayland && Environment.GetEnvironmentVariable("EMUTASTIC_GL_TOPLEVEL") == null)
                Environment.SetEnvironmentVariable("EMUTASTIC_GL_TOPLEVEL", "1");

            var session = new EmulatorSession(core, rom, console);

            // Quit signals → ask the loop to stop cleanly (flushes SRAM), distinct from a hard kill:
            //  • SIGTERM/SIGINT (incl. the PR_SET_PDEATHSIG signal when the parent dies),
            //  • stdin EOF (the parent closes the child's stdin to request a graceful quit).
            using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; session.RequestQuit(); });
            using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  ctx => { ctx.Cancel = true; session.RequestQuit(); });
            // Only watch stdin when the SUPERVISOR explicitly holds our stdin pipe (--parent-stdin). Without
            // that flag (direct-launch / no pipe), stdin is already at EOF and would quit us instantly.
            if (parentStdin)
            {
                var stdinWatch = new Thread(() =>
                {
                    try { var s = Console.OpenStandardInput(); var b = new byte[1]; while (s.Read(b, 0, 1) > 0) { } }
                    catch { /* no stdin pipe */ }
                    session.RequestQuit();   // parent closed the pipe → graceful quit
                }) { IsBackground = true, Name = "HostStdinWatch" };
                stdinWatch.Start();
            }

            var sw = Stopwatch.StartNew();
            // Run the game window on THIS (main) thread by default — Linux screen-sync prefers it. The host's
            // main thread has nothing else to do. EMUTASTIC_GL_MAINTHREAD=0 reverts to a background thread.
            bool mainThread = Environment.GetEnvironmentVariable("EMUTASTIC_GL_MAINTHREAD") != "0";
            string? error;
            bool started = mainThread ? session.RunInline(out error) : session.Start(out error);
            if (!started)
            {
                Trace.WriteLine($"[Host] session start failed: {error}");
                session.Dispose();
                WriteResults(resultsPath, 3, 0);
                return 3;
            }
            if (!mainThread) session.WaitForExit();   // inline mode already blocked until the game exited
            sw.Stop();
            session.Dispose();

            int playSeconds = (int)sw.Elapsed.TotalSeconds;
            Trace.WriteLine($"[Host] === game-host end: playSeconds={playSeconds} ===");
            if (log != null) { try { Trace.Flush(); Trace.Listeners.Remove(log); log.Dispose(); } catch { } }
            WriteResults(resultsPath, 0, playSeconds);
            return 0;
        }

        // Atomic results handoff to the parent: write <path>.tmp then rename. A crashed child writes
        // nothing → the parent treats "no file + non-zero exit" as a crash.
        private static void WriteResults(string path, int exitCode, int playSeconds)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string json = JsonSerializer.Serialize(new GameHostResult { ExitCode = exitCode, PlaySeconds = playSeconds });
                File.WriteAllText(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) { Trace.WriteLine($"[Host] results write failed: {ex.Message}"); }
        }
    }

    /// <summary>The child→parent results payload (written on exit; read by GameHostLauncher).</summary>
    public sealed class GameHostResult
    {
        public int ExitCode { get; set; }
        public int PlaySeconds { get; set; }
    }
}
