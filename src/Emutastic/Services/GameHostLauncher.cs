using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Emutastic.Emulator;

namespace Emutastic.Services
{
    /// <summary>
    /// Parent-side launcher for games. Default (legacy) path opens the in-process Avalonia
    /// <see cref="Views.EmulatorWindow"/>. When <c>EMUTASTIC_PRESENT=gl</c> it instead spawns a separate
    /// <c>--game-host</c> child process (Branch B — see docs/gl-present-phase1-host-process-design.md) and
    /// supervises it OFF the UI thread. While any child game is alive it sets
    /// <see cref="EmulatorSession.ExternalGameActive"/> so the parent's ControllerManager stops pumping
    /// the gamepads the child now owns.
    /// </summary>
    public static class GameHostLauncher
    {
        private static int _active;   // live child game processes

        /// <summary>Set once by MainWindow: ingests save states the host wrote (DiscoverSaveStates)
        /// after any game session with a known Game ends — regardless of which window launched it.</summary>
        public static Action<Models.Game>? OnGameSessionEnded;
        /// <summary>RA session results ingest (DB + config writes live in the parent).</summary>
        public static Action<Models.Game, GameHostResult>? OnRaSessionResults;

        /// <summary>Set once by MainWindow: records play stats when a session ends — (game,
        /// playSeconds). Feeds PlayCount/LastPlayed/TotalPlayTimeSeconds (Recently Played view,
        /// detail-card stat pills). The parent owns the DB.</summary>
        public static Action<Models.Game, int>? OnPlayStats;
        /// <summary>Fired when a host spawns with a known Game — MainWindow uses it to
        /// pre-fetch friend leaderboard ranks for the LB-toast decision.</summary>
        public static Action<Models.Game>? OnGameLaunching;
        /// <summary>Game-aware variant of OnHostCommand (same verbs, plus the emitting
        /// game) for commands whose reply must route back to that host's stdin.</summary>
        public static Action<Models.Game, string, string>? OnHostCommandForGame;

        /// <summary>Set once by MainWindow: handles "EMUTASTIC-CMD &lt;verb&gt; &lt;arg&gt;" requests the
        /// host writes to stdout (e.g. cog → "Edit Game Controls…" = ("open-controls", console)).
        /// Invoked on the UI thread.</summary>
        public static Action<string, string>? OnHostCommand;

        // Live hosts by game id — lets the app push lines down a child's stdin (the app→host half
        // of the command channel; e.g. "reload-cheats" after the cheat editor saves).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> _liveHosts = new();

        /// <summary>Send one protocol line to the running host for <paramref name="gameId"/> (no-op if gone).</summary>
        public static void SendToHost(int gameId, string line)
        {
            if (!_liveHosts.TryGetValue(gameId, out var proc)) return;
            try { proc.StandardInput.WriteLine(line); proc.StandardInput.Flush(); }
            catch (Exception ex) { Trace.WriteLine($"[Launcher] SendToHost failed: {ex.Message}"); }
        }

        /// <summary>Send one protocol line to EVERY running host. Used for config that isn't keyed by a
        /// specific game (e.g. "reload-input" after a Controls-panel edit — each host rebinds its own
        /// console). Normally there's just one live host; harmless to fan out.</summary>
        public static void BroadcastToHosts(string line)
        {
            foreach (var proc in _liveHosts.Values)
                try { proc.StandardInput.WriteLine(line); proc.StandardInput.Flush(); }
                catch (Exception ex) { Trace.WriteLine($"[Launcher] BroadcastToHosts failed: {ex.Message}"); }
        }

        /// <summary>Launch a game. <paramref name="onExit"/> is invoked on the UI thread when the game
        /// ends (result is null on a crash / missing results file).</summary>
        public static void Launch(string corePath, string romPath, string console, Action<GameHostResult?>? onExit = null)
            => Launch(corePath, romPath, console, game: null, loadStatePath: null, onExit);

        /// <summary>
        /// Launch with save-state support: <paramref name="game"/> supplies the state directory /
        /// title / hash context the host needs (it has no DB), and <paramref name="loadStatePath"/>
        /// boots straight into a state (upstream's pendingLoadStatePath). Null game = no state support
        /// for this session (e.g. direct CLI launches).
        /// </summary>
        public static void Launch(string corePath, string romPath, string console,
            Models.Game? game, string? loadStatePath, Action<GameHostResult?>? onExit = null)
        {
            // GL present is now the DEFAULT path: the separate --game-host process runs the decoupled
            // pacing (audio-clock emu thread + vsync present thread), which on this hardware gives correct
            // speed + clean audio where the legacy in-process WriteableBitmap path stuttered. Set
            // EMUTASTIC_PRESENT=writeable to revert to that legacy window.
            string present = (Environment.GetEnvironmentVariable("EMUTASTIC_PRESENT") ?? "gl").Trim();
            bool gl = !string.Equals(present, "writeable", StringComparison.OrdinalIgnoreCase);
            // EMUTASTIC_GAMEHOST=0 forces the in-process path even in GL mode (debug/escape hatch + lets us
            // A/B in-process vs separate-process GL on the real machine).
            bool useHost = gl && Environment.GetEnvironmentVariable("EMUTASTIC_GAMEHOST") != "0";
            if (!useHost)
            {
                // Legacy in-process path — unchanged behavior, the default until GL ships (Phase 5).
                var win = new Views.EmulatorWindow(new EmulatorSession(corePath, romPath, console));
                if (onExit != null) win.Closed += (_, _) => onExit(new GameHostResult { ExitCode = 0, PlaySeconds = 0 });
                win.Show();
                return;
            }
            // Cloud sync: pull a newer remote battery save BEFORE the host opens the
            // .srm (upstream blocked launch on this with the same per-game lock + 5s
            // bound). Off the UI thread; spawn resumes on the dispatcher.
            var syncSvc = GitHubSyncService.Instance;
            var syncCfg = App.Configuration?.GetCloudSyncConfiguration();
            if (game != null && syncSvc.IsAuthenticated && syncCfg is { Enabled: true })
            {
                _ = Task.Run(async () =>
                {
                    try { await syncSvc.PullSaveBeforeLaunchAsync(game).ConfigureAwait(false); }
                    catch (Exception ex) { CloudSyncLog.Write($"pre-launch pull failed: {ex.Message}"); }
                    Dispatcher.UIThread.Post(() => SpawnHost(corePath, romPath, console, game, loadStatePath, onExit));
                });
                return;
            }
            SpawnHost(corePath, romPath, console, game, loadStatePath, onExit);
        }

        private static void SpawnHost(string corePath, string romPath, string console,
            Models.Game? game, string? loadStatePath, Action<GameHostResult?>? onExit)
        {
            string results = Path.Combine(Path.GetTempPath(), $"emutastic-host-{Guid.NewGuid():N}.json");
            var psi = new ProcessStartInfo
            {
                RedirectStandardInput = true,   // closing this stdin pipe = graceful-quit request to the child
                RedirectStandardOutput = true,  // host→parent command channel ("EMUTASTIC-CMD …" lines)
                UseShellExecute = false,
            };

            // Re-launch ourselves in --game-host mode. Handle both `dotnet Emutastic.dll` and a native apphost.
            string self = Environment.ProcessPath ?? "dotnet";
            psi.FileName = self;
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string dll = Assembly.GetEntryAssembly()?.Location ?? "";
                if (!string.IsNullOrEmpty(dll)) psi.ArgumentList.Add(dll);
            }
            psi.ArgumentList.Add("--game-host");
            psi.ArgumentList.Add(corePath);
            psi.ArgumentList.Add(romPath);
            if (!string.IsNullOrEmpty(console)) { psi.ArgumentList.Add("--console"); psi.ArgumentList.Add(console); }
            psi.ArgumentList.Add("--results"); psi.ArgumentList.Add(results);
            psi.ArgumentList.Add("--parent-stdin");   // we hold the child's stdin; closing it = graceful quit
            // Portable mode forwarding: the child re-detects via portable.txt next to the
            // exe, but a CLI-only `--portable` launch has no marker — forward the flag.
            if (AppPaths.IsPortable) psi.ArgumentList.Add("--portable");

            // Save-state context: the host writes .state/.png/.json into this dir (same convention
            // ImportService.DiscoverSaveStates scans); we ingest DB rows when the host exits.
            if (game != null)
            {
                string saveDir = AppPaths.GetFolder("Save States",
                    FileNameHelper.SanitizeFileName(game.Console ?? console),
                    FileNameHelper.SanitizeFileName(game.Title ?? Path.GetFileNameWithoutExtension(romPath)));
                psi.ArgumentList.Add("--save-dir");   psi.ArgumentList.Add(saveDir);
                psi.ArgumentList.Add("--game-title"); psi.ArgumentList.Add(game.Title ?? "");
                psi.ArgumentList.Add("--rom-hash");   psi.ArgumentList.Add(game.RomHash ?? "");
                psi.ArgumentList.Add("--game-id");    psi.ArgumentList.Add(game.Id.ToString());
                // ROM-hack entry: hand the host the stored patch so it soft-patches at load.
                if (game.HasPatch && File.Exists(game.PatchPath))
                {
                    psi.ArgumentList.Add("--patch");
                    psi.ArgumentList.Add(game.PatchPath);
                }
                if (!string.IsNullOrEmpty(loadStatePath))
                {
                    psi.ArgumentList.Add("--load-state");
                    psi.ArgumentList.Add(loadStatePath);
                }
                // Per-game turbo sets (upstream's turbo_p{port}_{gameId} keys): handed down as one
                // arg — the host reads no config. Live toggles come back as "save-turbo" commands.
                if (App.Configuration != null)
                {
                    string turbo = string.Join(";", Enumerable.Range(0, 4)
                        .Select(p => App.Configuration.GetValue($"turbo_p{p}_{game.Id}", "")));
                    if (turbo != ";;;") { psi.ArgumentList.Add("--turbo"); psi.ArgumentList.Add(turbo); }

                    // Per-game remembered window size (saved at session end via "save-win-size").
                    int gw = App.Configuration.GetValue($"gameWin_{game.Id}_w", 0);
                    int gh = App.Configuration.GetValue($"gameWin_{game.Id}_h", 0);
                    if (gw > 0 && gh > 0) { psi.ArgumentList.Add("--win-size"); psi.ArgumentList.Add($"{gw}x{gh}"); }
                }
            }

            // Native Wayland for the game window (RetroArch's backend — the smooth path). The parent Avalonia
            // app runs on X11/Xwayland, so without forcing this the child also picks x11/Xwayland → no EGL
            // FIFO → unsynced/juddery present. Only on a Wayland session, and don't override an explicit pick.
            // EXCEPTION: a core that forces a GLX/XWayland HW context (PPSSPP — ForceCompatibilityGlProfile)
            // must keep its WHOLE present pipeline on XWayland/GLX; native Wayland EGL can't coexist with that
            // GLX context on NVIDIA (the surface never maps → no PSP window). Pin those to x11+GLX here — we
            // MUST set it on the child env, because GameHost's own setenv() can't override a value the parent
            // already injected (this is exactly why the Play-button path didn't get the fix). Mirrors GameHost.cs.
            bool onWayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            bool forceX11Gl = ConsoleHandlers.ConsoleHandlerFactory.Create(console).ForceCompatibilityGlProfile;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDL_VIDEODRIVER")) && onWayland)
            {
                if (forceX11Gl)
                {
                    psi.Environment["SDL_VIDEODRIVER"] = "x11";
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDL_VIDEO_X11_FORCE_EGL")))
                        psi.Environment["SDL_VIDEO_X11_FORCE_EGL"] = "0";   // SDL3 x11 defaults to EGL → force GLX
                }
                else
                    psi.Environment["SDL_VIDEODRIVER"] = "wayland";
            }

            // Never-evict shader cache. The per-scene freeze on 3D cores (esp. flycast/Dreamcast)
            // is one-time GPU shader COMPILATION; Mesa persists each compiled shader to
            // ~/.cache/mesa_shader_cache_db so a given shader only ever stalls ONCE — but the
            // default cap (~1GB, or 5% of the fs) can EVICT old entries, re-introducing the
            // freeze on a revisited scene. Raise the cap so accumulated shaders are never purged,
            // making the warm-up genuinely permanent across sessions (and across pre-warm runs).
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MESA_SHADER_CACHE_MAX_SIZE")))
                psi.Environment["MESA_SHADER_CACHE_MAX_SIZE"] = "12G";

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Launcher] failed to spawn game host: {ex.Message}");
                onExit?.Invoke(null);
                return;
            }

            Interlocked.Increment(ref _active);
            EmulatorSession.ExternalGameActive = true;
            if (game != null) _liveHosts[game.Id] = proc;
            if (game != null) { try { OnGameLaunching?.Invoke(game); } catch { } }
            Trace.WriteLine($"[Launcher] game host pid={proc.Id} core={corePath} rom={romPath}");

            // Drain the child's stdout continuously (a full pipe would block the child) and act on
            // command lines. Anything not "EMUTASTIC-CMD <verb> [arg]" (e.g. a core's native printf)
            // is discarded — the host's real logging goes to emulator-host.log, not stdout.
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (!line.StartsWith("EMUTASTIC-CMD ", StringComparison.Ordinal)) continue;
                        string[] parts = line.Substring("EMUTASTIC-CMD ".Length).Split(' ', 2);
                        string verb = parts[0], arg = parts.Length > 1 ? parts[1] : "";
                        Trace.WriteLine($"[Launcher] host command: {verb} {arg}");
                        Dispatcher.UIThread.Post(() =>
                        {
                            OnHostCommand?.Invoke(verb, arg);
                            if (game != null) OnHostCommandForGame?.Invoke(game, verb, arg);
                        });
                    }
                }
                catch (Exception ex) { Trace.WriteLine($"[Launcher] stdout reader ended: {ex.Message}"); }
            });

            // Supervise off the UI thread (GOLDEN RULE). Never blocks the dispatcher.
            _ = Task.Run(async () =>
            {
                GameHostResult? result = null;
                try
                {
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                    result = TryReadResults(results, proc.ExitCode);
                }
                catch (Exception ex) { Trace.WriteLine($"[Launcher] supervise error: {ex.Message}"); }
                finally
                {
                    try { File.Delete(results); } catch { }
                    if (game != null) _liveHosts.TryRemove(game.Id, out _);
                    if (Interlocked.Decrement(ref _active) == 0) EmulatorSession.ExternalGameActive = false;
                }

                Trace.WriteLine($"[Launcher] game host exited: code={result?.ExitCode}, playSeconds={result?.PlaySeconds}");

                // Play stats: count the session + add its seconds (a crashed host — null result —
                // still counts the play; its seconds are simply unknown). Off-UI thread.
                if (game != null && OnPlayStats != null)
                {
                    try { OnPlayStats(game, result?.PlaySeconds ?? 0); }
                    catch (Exception ex) { Trace.WriteLine($"[Launcher] play-stats record failed: {ex.Message}"); }
                }

                // Ingest any save states the host wrote this session (host has no DB). Off-UI thread;
                // DiscoverSaveStates fires SaveStatesChanged so open views refresh themselves.
                if (game != null && OnGameSessionEnded != null)
                {
                    try { OnGameSessionEnded(game); }
                    catch (Exception ex) { Trace.WriteLine($"[Launcher] save-state ingest failed: {ex.Message}"); }
                }

                // RetroAchievements session results (identification, live progress, refreshed
                // token) — the host can't write DB/config, so the parent ingests them here.
                if (game != null && result != null && OnRaSessionResults != null)
                {
                    try { OnRaSessionResults(game, result); }
                    catch (Exception ex) { Trace.WriteLine($"[Launcher] RA results ingest failed: {ex.Message}"); }
                }

                // Recording safety net: a host that exits cleanly finishes its own encode first,
                // so this only finds work when the host CRASHED mid-recording/mid-encode — finish
                // the orphaned encode now rather than at next app launch. (Skips anything a
                // surviving ffmpeg is still actively writing.)
                try { RecordingService.RecoverInterrupted(); }
                catch (Exception ex) { Trace.WriteLine($"[Launcher] recording recovery failed: {ex.Message}"); }

                if (onExit != null) Dispatcher.UIThread.Post(() => onExit(result));
            });
        }

        private static GameHostResult? TryReadResults(string path, int exitCode)
        {
            try
            {
                if (File.Exists(path))
                {
                    var r = JsonSerializer.Deserialize<GameHostResult>(File.ReadAllText(path));
                    if (r != null) return r;
                }
            }
            catch (Exception ex) { Trace.WriteLine($"[Launcher] results read failed: {ex.Message}"); }
            // No file (or unreadable): a non-zero process exit means a crash; report it as such.
            return exitCode == 0 ? new GameHostResult { ExitCode = 0, PlaySeconds = 0 } : null;
        }
    }
}
