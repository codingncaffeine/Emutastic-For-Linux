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

        // Longer default pre-warm budget (s). The warm loop exits EARLY once shader-cache growth
        // stalls, so this is an upper bound, not a fixed wait — covers menu/intro shaders without
        // making the common already-warm case sit idle.
        private const int DefaultPrewarmSeconds = 90;

        public static int Run(string[] args)
        {
            // args[0] == "--game-host"; [1]=core, [2]=rom; flags: --console <name> --fullscreen --results <path>
            string? core = args.Length > 1 ? args[1] : null;
            string? rom  = args.Length > 2 ? args[2] : null;
            string console = "", resultsPath = "";
            string saveDir = "", gameTitle = "", romHash = "", loadStatePath = "", patchPath = "", turboSpec = "", winSize = "";
            int gameId = -1;
            bool fullscreen = false, parentStdin = false;
            int prewarmSeconds = 0;   // >0 = shader pre-warm pass: run the attract/boot loop, then auto-quit
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--console":      if (i + 1 < args.Length) console = args[++i]; break;
                    case "--fullscreen":   fullscreen = true; break;
                    case "--results":      if (i + 1 < args.Length) resultsPath = args[++i]; break;
                    case "--parent-stdin": parentStdin = true; break;   // supervisor holds our stdin; EOF = graceful quit
                    // Save-state context (this process has no DB; the main app ingests rows on exit).
                    case "--save-dir":     if (i + 1 < args.Length) saveDir = args[++i]; break;
                    case "--game-title":   if (i + 1 < args.Length) gameTitle = args[++i]; break;
                    case "--rom-hash":     if (i + 1 < args.Length) romHash = args[++i]; break;
                    case "--load-state":   if (i + 1 < args.Length) loadStatePath = args[++i]; break;
                    // ROM-hack patch (IPS/BPS/UPS) — applied to the ROM buffer in memory at load.
                    case "--patch":        if (i + 1 < args.Length) patchPath = args[++i]; break;
                    // Per-game turbo sets ("p0csv;p1csv;p2csv;p3csv") — this process reads no config.
                    case "--turbo":        if (i + 1 < args.Length) turboSpec = args[++i]; break;
                    // Per-game remembered window size ("WxH"), saved at the last session's end.
                    case "--win-size":     if (i + 1 < args.Length) winSize = args[++i]; break;
                    // Library row id — names the per-game cheat file (Cheats/{Console}/{id}.json).
                    case "--game-id":      if (i + 1 < args.Length && int.TryParse(args[i + 1], out int gid)) { i++; gameId = gid; } break;
                    case "--prewarm":
                        // Optional explicit budget; bare "--prewarm" uses the longer default.
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pw)) { i++; prewarmSeconds = Math.Clamp(pw, 1, 600); }
                        else prewarmSeconds = DefaultPrewarmSeconds;
                        break;
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

            var session = new EmulatorSession(core, rom, console)
            {
                SaveStateDir  = saveDir,
                SaveGameTitle = gameTitle,
                SaveRomHash   = romHash,
                CheatGameId   = gameId,
                PatchPath     = string.IsNullOrEmpty(patchPath) ? null : patchPath,
            };
            // Host→parent command channel: one prefixed line on stdout per request (the launcher
            // redirects and parses our stdout; everything else written there is ignored).
            EmulatorSession.EmitHostCommand = cmd =>
            {
                try { Console.Out.WriteLine("EMUTASTIC-CMD " + cmd); Console.Out.Flush(); }
                catch { /* parent gone — harmless */ }
            };
            // Launch-into-state (mirrors upstream's pendingLoadStatePath ctor param): queued now,
            // executed on the emu thread after warmup once the loop starts.
            if (!string.IsNullOrEmpty(loadStatePath)) session.QueuePendingLoad(loadStatePath);
            // Saved per-game turbo sets (upstream's LoadTurboConfig, handed down by the parent).
            if (!string.IsNullOrEmpty(turboSpec)) session.SetTurboConfig(turboSpec);
            // Per-game remembered window size ("WxH" from the parent).
            int xPos = winSize.IndexOf('x');
            if (xPos > 0 && int.TryParse(winSize.AsSpan(0, xPos), out int rw)
                         && int.TryParse(winSize.AsSpan(xPos + 1), out int rh))
            { session.RestoreWinW = rw; session.RestoreWinH = rh; }

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
                    // Line protocol from the parent (the app→host half of the command channel);
                    // EOF/null keeps its original meaning: parent closed the pipe → graceful quit.
                    try
                    {
                        using var reader = new StreamReader(Console.OpenStandardInput());
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            switch (line.Trim())
                            {
                                case "reload-cheats":
                                    Trace.WriteLine("[Host] parent requested cheat reload");
                                    session.ReloadCheats();
                                    break;
                                case string l when l.StartsWith("show-ra-toast ", StringComparison.Ordinal):
                                    // Library-decided LB toast: JSON {h, s} → in-game GlOsd toast.
                                    try
                                    {
                                        var doc = System.Text.Json.JsonDocument.Parse(l.Substring("show-ra-toast ".Length));
                                        session.ShowRaToastFromParent(
                                            doc.RootElement.GetProperty("h").GetString() ?? "",
                                            doc.RootElement.GetProperty("s").GetString() ?? "");
                                    }
                                    catch (Exception ex) { Trace.WriteLine($"[Host] show-ra-toast parse failed: {ex.Message}"); }
                                    break;
                                // unknown lines ignored (forward compat)
                            }
                        }
                    }
                    catch { /* no stdin pipe */ }
                    session.RequestQuit();   // parent closed the pipe → graceful quit
                }) { IsBackground = true, Name = "HostStdinWatch" };
                stdinWatch.Start();
            }

            // Shader pre-warm pass: run the boot/attract loop for a fixed budget, then quit cleanly.
            // The point is to make the driver COMPILE the title/menu/demo shaders into the (now
            // never-evicted) Mesa cache up front, so the first real play session doesn't stall on
            // them. Coverage is partial — only the states the attract loop actually reaches — but it
            // front-loads the most common boot→title→menu path. Auto-quit on a background timer so
            // RRunInline (main-thread present loop) still owns the window.
            if (prewarmSeconds > 0)
            {
                Trace.WriteLine($"[Host] === shader pre-warm: up to {prewarmSeconds}s (early-exit on cache plateau) ===");
                var warmQuit = new Thread(() =>
                {
                    string cacheDir = MesaShaderCacheDir();
                    long lastSize = DirSize(cacheDir);
                    int stallSecs = 0;       // consecutive seconds with no cache growth
                    const int PlateauExit = 8;   // quit once growth has stalled this long (shaders done compiling)
                    const int MinWarm = 10;      // always warm at least this long before allowing early-exit
                    for (int s = 0; s < prewarmSeconds && !session.QuitRequested; s++)
                    {
                        Thread.Sleep(1000);
                        long sz = DirSize(cacheDir);
                        if (sz > lastSize) { stallSecs = 0; lastSize = sz; } else stallSecs++;
                        if (s >= MinWarm && stallSecs >= PlateauExit)
                        {
                            Trace.WriteLine($"[Host] pre-warm: cache plateaued ({sz} bytes) after {s + 1}s → done");
                            break;
                        }
                    }
                    if (!session.QuitRequested) Trace.WriteLine($"[Host] pre-warm finished → quitting (cache {lastSize} bytes)");
                    session.RequestQuit();
                }) { IsBackground = true, Name = "ShaderPrewarm" };
                warmQuit.Start();
            }

            // Test hook: EMUTASTIC_AUTORECORD=1 starts a recording ~3s in — used to exercise the
            // close-while-recording path end-to-end (stdin EOF → quit → stop → encode → exit)
            // without keyboard/HUD interaction.
            if (Environment.GetEnvironmentVariable("EMUTASTIC_AUTORECORD") == "1")
            {
                new Thread(() => { Thread.Sleep(3000); try { session.ToggleRecording(); } catch { } })
                { IsBackground = true, Name = "AutoRecord" }.Start();
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

            // Closed mid-recording: the window is gone but the encode is still running — linger
            // headless until it finishes rather than exiting and orphaning the raw capture.
            // The wait is bounded by the encode's own ffmpeg timeouts (≤ ~11 min worst case).
            var encode = session.PendingRecordingEncode;
            if (encode != null && !encode.IsCompleted)
            {
                Trace.WriteLine("[Host] recording encode still running — waiting before exit…");
                try { encode.Wait(TimeSpan.FromMinutes(12)); }
                catch (Exception ex) { Trace.WriteLine($"[Host] encode wait ended: {ex.Message}"); }
            }

            int playSeconds = (int)sw.Elapsed.TotalSeconds;
            Trace.WriteLine($"[Host] === game-host end: playSeconds={playSeconds} ===");
            if (log != null) { try { Trace.Flush(); Trace.Listeners.Remove(log); log.Dispose(); } catch { } }
            WriteResults(resultsPath, 0, playSeconds, session);
            return 0;
        }

        // Mesa's on-disk shader cache: $MESA_SHADER_CACHE_DIR, else $XDG_CACHE_HOME/mesa_shader_cache_db,
        // else ~/.cache/mesa_shader_cache_db. Used only to watch growth during pre-warm (best-effort).
        private static string MesaShaderCacheDir()
        {
            string? d = Environment.GetEnvironmentVariable("MESA_SHADER_CACHE_DIR");
            if (!string.IsNullOrEmpty(d)) return d;
            string cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cacheHome, "mesa_shader_cache_db");
        }

        private static long DirSize(string dir)
        {
            try
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
                return total;
            }
            catch { return 0; }
        }

        // Atomic results handoff to the parent: write <path>.tmp then rename. A crashed child writes
        // nothing → the parent treats "no file + non-zero exit" as a crash.
        private static void WriteResults(string path, int exitCode, int playSeconds,
                                         Emutastic.Emulator.EmulatorSession? session = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string json = JsonSerializer.Serialize(new GameHostResult
                {
                    ExitCode = exitCode,
                    PlaySeconds = playSeconds,
                    // RetroAchievements session results — the host writes neither DB nor
                    // config (conventions), so identification + live progress + a refreshed
                    // login token ride home here for the parent to ingest.
                    RaGameId = session?.RaGameIdResult ?? 0,
                    RaOutcome = session?.RaOutcomeResult ?? "",
                    RaNewToken = session?.RaNewTokenResult,
                    RaLiveProgressJson = session?.RaLiveProgressJsonResult,
                });
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
        // RetroAchievements session results (0/empty/null when RA was off or didn't land).
        public int RaGameId { get; set; }
        public string RaOutcome { get; set; } = "";
        public string? RaNewToken { get; set; }
        public string? RaLiveProgressJson { get; set; }
    }
}
