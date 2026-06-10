using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static Emutastic.Services.RcheevosInterop;

namespace Emutastic.Services
{
    /// <summary>
    /// High-level wrapper around the rcheevos rc_client API (port of upstream's
    /// RetroAchievementsClient). Manages login, game loading, per-frame
    /// processing, and achievement events. Runs in the GAME-HOST process (the
    /// core and its memory live there) — keep this file Avalonia-free.
    ///
    /// Linux deltas from upstream: badge/avatar URLs come from the
    /// rc_client_*_get_image_url accessors (vendored v11.6.0 dropped the struct
    /// fields); the CHD cdreader installs globally via rc_hash_init_custom_cdreader
    /// (v11.6.0 has no per-client hash-callbacks merge).
    /// </summary>
    public class RetroAchievementsClient : IDisposable
    {
        private IntPtr _client;
        private LibretroCore? _core;
        private bool _disposed;

        // Keep delegates alive so GC doesn't collect them while native code holds pointers.
        private ReadMemoryFunc? _readMemoryDelegate;
        private ServerCallFunc? _serverCallDelegate;
        private EventHandlerFunc? _eventHandlerDelegate;
        private MessageCallbackFunc? _logDelegate;

        // Cached memory region pointers (refreshed each frame is too slow;
        // these are stable for the lifetime of a loaded game).
        private IntPtr _systemRamPtr;
        private uint _systemRamSize;
        private IntPtr _saveRamPtr;
        private uint _saveRamSize;
        private IntPtr _videoRamPtr;
        private uint _videoRamSize;

        // Identifies us to RA's server. RA's hardcore policy: a missing or
        // unrecognized User-Agent downgrades hardcore unlocks to softcore, and
        // the emulator name must be on RA's approved list for hardcore to count.
        private static readonly HttpClient _http = CreateRcheevosHttp();

        private static HttpClient CreateRcheevosHttp()
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(EmutasticUserAgent.Build());
            return http;
        }

        /// <summary>
        /// Updates the rcheevos HTTP client's User-Agent to include the active
        /// libretro core's name and version. Call once per game-launch BEFORE
        /// rcheevos login/identify. Not thread-safe — call before frames run.
        /// </summary>
        public static void SetCoreContext(string? coreName, string? coreVersion)
        {
            _http.DefaultRequestHeaders.UserAgent.Clear();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(EmutasticUserAgent.Build(coreName, coreVersion));
        }

        /// <summary>Fired on the emulation thread when an achievement is triggered.</summary>
        public event Action<AchievementInfo>? AchievementTriggered;

        /// <summary>Fired when the player completes the game (all achievements).</summary>
        public event Action? GameCompleted;

        /// <summary>Fired when rcheevos requests an emulator reset (hardcore toggle).</summary>
        public event Action? ResetRequested;

        /// <summary>Fired for achievement progress updates (show/update/hide).</summary>
        public event Action<AchievementInfo?, bool>? ProgressIndicatorChanged;

        /// <summary>Fired when a challenge achievement primes (true) / un-primes (false) —
        /// rcheevos CHALLENGE_INDICATOR_SHOW/HIDE. Several can be active at once.</summary>
        public event Action<AchievementInfo, bool>? ChallengeIndicatorChanged;

        /// <summary>
        /// Fired on the emulation thread when rcheevos delivers a leaderboard
        /// scoreboard post-submission. Subscribers MUST hop threads before
        /// touching any UI surface.
        /// </summary>
        public event Action<LbScoreboardInfo>? LeaderboardScoreboardReceived;

        /// <summary>Marshaled, pointer-free view of a SCOREBOARD event.</summary>
        public sealed record LbScoreboardInfo(
            int LeaderboardId,
            int NewRank,
            string SubmittedScore,
            string BestScore,
            string LbTitle,
            bool LowerIsBetter);

        // Decode a fixed-size UTF-8 byte buffer (null-padded rcheevos display string).
        private static string DecodeFixed(byte[] buf)
        {
            if (buf == null) return "";
            int len = 0;
            while (len < buf.Length && buf[len] != 0) len++;
            return System.Text.Encoding.UTF8.GetString(buf, 0, len);
        }

        // Live measured-progress snapshot, accumulated from PROGRESS_INDICATOR
        // events during play. Written from the emu thread, read at game-exit
        // flush time; ConcurrentDictionary keeps the hot path lock-free.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, AchievementInfo> _liveProgress = new();

        /// <summary>
        /// Snapshot of measured-progress data captured this session, keyed by
        /// achievement ID. Callers invoke this from the emu loop's teardown,
        /// after rcheevos has stopped firing events into this client.
        /// </summary>
        public IReadOnlyDictionary<int, AchievementInfo> GetLiveProgressSnapshot()
        {
            var copy = new Dictionary<int, AchievementInfo>(_liveProgress.Count);
            foreach (var kvp in _liveProgress)
                copy[kvp.Key] = kvp.Value;
            return copy;
        }

        public bool IsInitialized => _client != IntPtr.Zero;
        public bool IsGameLoaded => _client != IntPtr.Zero && rc_client_is_game_loaded(_client) != 0;

        // Per-console virtual-to-real address translation, mirroring rcheevos's
        // built-in _rc_memory_regions_<console> tables (src/rcheevos/consoleinfo.c).
        // Set authoring uses virtual addresses; descriptors map real hardware
        // addresses to pointers; the frontend translates virtual→real first.
        private uint _virtualAddressBase;
        private readonly struct VirtualRegion
        {
            public readonly uint VirtStart;   // inclusive
            public readonly uint VirtEnd;     // inclusive
            public readonly ulong PhysStart;
            public VirtualRegion(uint vs, uint ve, ulong ps) { VirtStart = vs; VirtEnd = ve; PhysStart = ps; }
        }
        private VirtualRegion[]? _virtualMap;

        private static readonly VirtualRegion[] _vmap_segacd = new[]
        {
            new VirtualRegion(0x000000u, 0x00FFFFu, 0x00FF0000UL), // 68000 RAM
            new VirtualRegion(0x010000u, 0x08FFFFu, 0x80020000UL), // CD PRG RAM
            new VirtualRegion(0x090000u, 0x0AFFFFu, 0x00200000UL), // CD Word RAM
        };
        private static readonly VirtualRegion[] _vmap_megadrive = new[]
        {
            new VirtualRegion(0x000000u, 0x00FFFFu, 0x00FF0000UL), // System RAM
            new VirtualRegion(0x010000u, 0x01FFFFu, 0x00000000UL), // Cartridge RAM (SRAM)
        };
        private static readonly VirtualRegion[] _vmap_gamecube = new[]
        {
            new VirtualRegion(0x00000000u, 0x017FFFFFu, 0x80000000UL), // 24MB System RAM (PowerPC)
        };

        // Cart Neo Geo only: XOR-1 byte-address swap on descriptor reads (RA's
        // cart sets were authored against FBNeo's host-native byte stream).
        // Assumes a little-endian host (x86-64); see upstream notes.
        private bool _cartByteswap;

        // ── Memory descriptor table (RETRO_ENVIRONMENT_SET_MEMORY_MAPS) ──
        public readonly struct MemoryRegion
        {
            public readonly ulong Flags;
            public readonly IntPtr Ptr;
            public readonly ulong Offset;
            public readonly ulong Start;
            public readonly ulong Len;
            public MemoryRegion(ulong flags, IntPtr ptr, ulong offset, ulong start, ulong len)
            { Flags = flags; Ptr = ptr; Offset = offset; Start = start; Len = len; }
        }
        private const ulong RETRO_MEMDESC_BIGENDIAN = 1UL << 1;
        private MemoryRegion[]? _memoryRegions;

        /// <summary>
        /// Called when the core publishes a memory map via
        /// RETRO_ENVIRONMENT_SET_MEMORY_MAPS. OnReadMemory prefers these
        /// regions over the legacy retro_get_memory_data linear space.
        /// </summary>
        public void SetMemoryDescriptors(MemoryRegion[] regions)
        {
            bool wasUnset = _memoryRegions == null || _memoryRegions.Length == 0;
            _memoryRegions = regions;
            Trace.WriteLine($"[RA] Memory descriptors registered: {regions.Length} region(s)");
            foreach (var r in regions)
                Trace.WriteLine($"[RA]   start=0x{r.Start:X8} len=0x{r.Len:X} flags=0x{r.Flags:X} {((r.Flags & RETRO_MEMDESC_BIGENDIAN) != 0 ? "BE" : "")}");

            // Cores that publish descriptors during the first retro_run frame
            // (not during retro_load_game) arrive after rcheevos has validated
            // achievement addresses against an empty map and disabled the set.
            // Reload so addresses re-validate against the real regions.
            if (wasUnset && _client != IntPtr.Zero && _lastRomPath != null
                && rc_client_is_game_loaded(_client) != 0)
            {
                Trace.WriteLine("[RA] Descriptors arrived post-load; reloading game to re-arm achievements");
                _reloadCallbackDelegate = (result, errorPtr, client, userdata) =>
                {
                    string? msg = PtrToStringUTF8(errorPtr);
                    Trace.WriteLine($"[RA] Post-descriptor reload result={result} err={msg}");
                };
                rc_client_unload_game(_client);
                rc_client_begin_identify_and_load_game(
                    _client, _lastConsoleId, _lastRomPath,
                    IntPtr.Zero, UIntPtr.Zero,
                    _reloadCallbackDelegate, IntPtr.Zero);
            }
        }

        /// <summary>Core may be null for login-only clients (the settings page's
        /// credential test) — memory routing simply stays empty.</summary>
        public void Initialize(LibretroCore? core, bool hardcoreEnabled, string? consoleName = null)
        {
            // Refuse to run on a misaligned native build — a shifted struct
            // field reads garbage silently (see RcheevosInterop.VerifyAbi).
            string? abi = VerifyAbi();
            if (abi != null)
                throw new InvalidOperationException($"rcheevos ABI mismatch: {abi} — rebuild native/rcheevos.");

            _core = core;

            // NGCD/NeoGeo cart: +0x100000 virtual→real M68K translation (see
            // upstream's notes on Geolith/FBNeo conventions).
            bool isNeoGeoFamily = string.Equals(consoleName, "NeoCD", StringComparison.Ordinal)
                              || string.Equals(consoleName, "NeoGeo", StringComparison.Ordinal);
            _virtualAddressBase = isNeoGeoFamily ? 0x100000u : 0u;
            _cartByteswap = string.Equals(consoleName, "NeoGeo", StringComparison.Ordinal);

            _virtualMap = consoleName switch
            {
                "SegaCD"     => _vmap_segacd,
                "Genesis"    => _vmap_megadrive,
                "MegaDrive"  => _vmap_megadrive,
                "GameCube"   => _vmap_gamecube,
                _            => null,
            };

            _readMemoryDelegate = OnReadMemory;
            _serverCallDelegate = OnServerCall;

            _client = rc_client_create(_readMemoryDelegate, _serverCallDelegate);
            if (_client == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create rcheevos client.");

            // CHD-aware cdreader: achievement identification for .chd content on
            // every CD-based console; non-CHD (.cue+.bin, .gdi, .iso) continues
            // through rcheevos's default cdreader. Global install (v11.6.0 API).
            RcheevosChdCdReader.InstallInto(_client);

            _logDelegate = OnLogMessage;
            rc_client_enable_logging(_client, RC_CLIENT_LOG_LEVEL_INFO, _logDelegate);

            _eventHandlerDelegate = OnEvent;
            rc_client_set_event_handler(_client, _eventHandlerDelegate);

            rc_client_set_hardcore_enabled(_client, hardcoreEnabled ? 1 : 0);
        }

        /// <summary>Log in with a saved token. Returns the token on success for re-saving.</summary>
        public (bool success, string? error, string? token) LoginWithToken(string username, string token)
        {
            if (_client == IntPtr.Zero) return (false, "Client not initialized.", null);

            bool completed = false;
            int resultCode = 0;
            string? errorMsg = null;
            var loginEvent = new ManualResetEventSlim(false);

            ClientCallbackFunc loginCallback = (result, errorPtr, client, userdata) =>
            {
                resultCode = result;
                errorMsg = PtrToStringUTF8(errorPtr);
                completed = true;
                loginEvent.Set();
            };

            rc_client_begin_login_with_token(_client, username, token, loginCallback, IntPtr.Zero);
            loginEvent.Wait(TimeSpan.FromSeconds(15));

            if (!completed) return (false, "Login timed out.", null);
            if (resultCode != RC_OK) return (false, errorMsg ?? $"Token login failed (code {resultCode}).", null);

            return (true, null, token);
        }

        /// <summary>Log in with username + password. Returns the token on success for saving.</summary>
        public (bool success, string? error, string? token) LoginWithPassword(string username, string password)
        {
            if (_client == IntPtr.Zero) return (false, "Client not initialized.", null);

            bool completed = false;
            int resultCode = 0;
            string? errorMsg = null;
            var loginEvent = new ManualResetEventSlim(false);

            ClientCallbackFunc loginCallback = (result, errorPtr, client, userdata) =>
            {
                resultCode = result;
                errorMsg = PtrToStringUTF8(errorPtr);
                completed = true;
                loginEvent.Set();
            };

            rc_client_begin_login_with_password(_client, username, password, loginCallback, IntPtr.Zero);
            loginEvent.Wait(TimeSpan.FromSeconds(15));

            if (!completed) return (false, "Login timed out.", null);
            if (resultCode != RC_OK) return (false, errorMsg ?? $"Password login failed (code {resultCode}).", null);

            // Extract the token from the user info
            IntPtr userPtr = rc_client_get_user_info(_client);
            string? returnedToken = null;
            if (userPtr != IntPtr.Zero)
            {
                var userInfo = Marshal.PtrToStructure<rc_client_user_t>(userPtr);
                returnedToken = PtrToStringUTF8(userInfo.token);
            }

            return (true, null, returnedToken);
        }

        /// <summary>
        /// Identify and load a game by its ROM file path.
        /// Blocks the calling thread until loading completes.
        /// </summary>
        public (bool success, string? error) LoadGame(string romPath, uint consoleId)
        {
            if (_client == IntPtr.Zero) return (false, "Client not initialized.");

            // Stash for late-descriptor reload (cores that publish
            // SET_MEMORY_MAPS during the first frame, not during load).
            _lastRomPath = romPath;
            _lastConsoleId = consoleId;

            bool completed = false;
            int resultCode = 0;
            string? errorMsg = null;
            var loadEvent = new ManualResetEventSlim(false);

            ClientCallbackFunc loadCallback = (result, errorPtr, client, userdata) =>
            {
                resultCode = result;
                errorMsg = PtrToStringUTF8(errorPtr);
                completed = true;
                loadEvent.Set();
            };

            // Cache memory region pointers BEFORE loading — rcheevos validates
            // achievement addresses during load via the read memory callback.
            CacheMemoryRegions();

            rc_client_begin_identify_and_load_game(
                _client, consoleId, romPath,
                IntPtr.Zero, UIntPtr.Zero,
                loadCallback, IntPtr.Zero);

            loadEvent.Wait(TimeSpan.FromSeconds(30));

            if (!completed) return (false, "Game load timed out.");
            if (resultCode != RC_OK) return (false, errorMsg ?? $"Game load failed (code {resultCode}).");

            return (true, null);
        }

        // Stored for late-descriptor reload — see SetMemoryDescriptors.
        private string? _lastRomPath;
        private uint _lastConsoleId;
        private ClientCallbackFunc? _reloadCallbackDelegate;

        /// <summary>Call once per emulated frame, after retro_run().</summary>
        public void DoFrame()
        {
            if (_client != IntPtr.Zero)
                rc_client_do_frame(_client);
        }

        /// <summary>Call while paused, at least once per second.</summary>
        public void Idle()
        {
            if (_client != IntPtr.Zero)
                rc_client_idle(_client);
        }

        /// <summary>Call on emulator reset.</summary>
        public void Reset()
        {
            if (_client != IntPtr.Zero)
                rc_client_reset(_client);
        }

        /// <summary>
        /// Serializes rcheevos's in-memory runtime state (hit counts, measured
        /// trackers) for pairing with a libretro save state. Null on failure;
        /// empty array when there is nothing to serialize.
        /// </summary>
        public byte[]? SerializeProgress()
        {
            if (_client == IntPtr.Zero) return null;
            try
            {
                UIntPtr size = rc_client_progress_size(_client);
                ulong sz = size.ToUInt64();
                if (sz == 0) return Array.Empty<byte>();
                var buf = new byte[sz];
                int rc = rc_client_serialize_progress_sized(_client, buf, size);
                if (rc != RC_OK)
                {
                    Trace.WriteLine($"[RA] rc_client_serialize_progress_sized failed: rc={rc}");
                    return null;
                }
                return buf;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RA] SerializeProgress threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Restores rcheevos's runtime state from a SerializeProgress blob.
        /// Safe with empty/missing data (older states predate the sidecar).
        /// </summary>
        public bool DeserializeProgress(byte[]? blob)
        {
            if (_client == IntPtr.Zero || blob == null || blob.Length == 0) return false;
            try
            {
                int rc = rc_client_deserialize_progress_sized(_client, blob, (UIntPtr)blob.LongLength);
                if (rc != RC_OK)
                {
                    Trace.WriteLine($"[RA] rc_client_deserialize_progress_sized failed: rc={rc}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RA] DeserializeProgress threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void UnloadGame()
        {
            if (_client != IntPtr.Zero)
                rc_client_unload_game(_client);
            _systemRamPtr = IntPtr.Zero;
            _systemRamSize = 0;
            _saveRamPtr = IntPtr.Zero;
            _saveRamSize = 0;
            _videoRamPtr = IntPtr.Zero;
            _videoRamSize = 0;
        }

        public string? GetGameTitle()
        {
            if (_client == IntPtr.Zero) return null;
            IntPtr gamePtr = rc_client_get_game_info(_client);
            if (gamePtr == IntPtr.Zero) return null;
            var game = Marshal.PtrToStructure<rc_client_game_t>(gamePtr);
            return PtrToStringUTF8(game.title);
        }

        /// <summary>
        /// RA's numeric game ID for the loaded game, or 0. Cached on the Game
        /// row by the caller so Web API fetches skip the hash-resolve roundtrip.
        /// </summary>
        public int GetGameId()
        {
            if (_client == IntPtr.Zero) return 0;
            IntPtr gamePtr = rc_client_get_game_info(_client);
            if (gamePtr == IntPtr.Zero) return 0;
            var game = Marshal.PtrToStructure<rc_client_game_t>(gamePtr);
            return (int)game.id;
        }

        // ── Memory read callback ─────────────────────────────────────────────

        private void CacheMemoryRegions()
        {
            if (_core == null) return;
            const uint RETRO_MEMORY_SAVE_RAM = 0;
            const uint RETRO_MEMORY_SYSTEM_RAM = 2;
            const uint RETRO_MEMORY_VIDEO_RAM = 3;

            (_systemRamPtr, _systemRamSize) = _core.GetMemoryRegion(RETRO_MEMORY_SYSTEM_RAM);
            (_saveRamPtr, _saveRamSize) = _core.GetMemoryRegion(RETRO_MEMORY_SAVE_RAM);
            (_videoRamPtr, _videoRamSize) = _core.GetMemoryRegion(RETRO_MEMORY_VIDEO_RAM);

            Trace.WriteLine($"[RA] Memory regions — SRAM: {_saveRamSize} bytes, System: {_systemRamSize} bytes, VRAM: {_videoRamSize} bytes");
        }

        private uint OnReadMemory(uint address, IntPtr buffer, uint numBytes, IntPtr client)
        {
            // Descriptor-aware path: when the core published a memory map,
            // route reads through it (virtual→real translation first; cart
            // NeoGeo additionally byte-swaps — see upstream's rationale).
            if (_memoryRegions != null && _memoryRegions.Length > 0)
            {
                ulong realAddress;
                if (_virtualMap != null)
                {
                    realAddress = ulong.MaxValue;
                    for (int v = 0; v < _virtualMap.Length; v++)
                    {
                        var vr = _virtualMap[v];
                        if (address >= vr.VirtStart && address <= vr.VirtEnd)
                        {
                            realAddress = vr.PhysStart + (address - vr.VirtStart);
                            break;
                        }
                    }
                    if (realAddress == ulong.MaxValue) return 0; // not in any region
                }
                else
                {
                    realAddress = (ulong)address + _virtualAddressBase;
                }

                for (int i = 0; i < _memoryRegions.Length; i++)
                {
                    var r = _memoryRegions[i];
                    if (r.Ptr == IntPtr.Zero || r.Len == 0) continue;
                    if (realAddress < r.Start) continue;
                    ulong rel = realAddress - r.Start;
                    if (rel >= r.Len) continue;

                    ulong avail = r.Len - rel;
                    uint toCopy = numBytes < avail ? numBytes : (uint)avail;

                    unsafe
                    {
                        byte* baseSrc = (byte*)r.Ptr + (long)r.Offset;
                        byte* dst = (byte*)buffer;
                        if (_cartByteswap)
                        {
                            for (uint k = 0; k < toCopy; k++)
                                dst[k] = baseSrc[(long)(rel + k) ^ 1L];
                        }
                        else
                        {
                            Buffer.MemoryCopy(baseSrc + (long)rel, dst, toCopy, toCopy);
                        }
                    }
                    return toCopy;
                }
                return 0; // address not covered by any descriptor
            }

            // Legacy path: linear concat of SYSTEM_RAM then SAVE_RAM at virtual 0.
            if (_systemRamSize > 0 && _systemRamPtr != IntPtr.Zero && address < _systemRamSize)
            {
                uint offset = address;
                uint avail = _systemRamSize - offset;
                uint toCopy = Math.Min(numBytes, avail);
                unsafe
                {
                    Buffer.MemoryCopy((byte*)_systemRamPtr + offset, (byte*)buffer, toCopy, toCopy);
                }
                return toCopy;
            }

            if (_saveRamSize > 0 && _saveRamPtr != IntPtr.Zero)
            {
                uint saveStart = _systemRamSize; // save RAM starts after system RAM
                if (address >= saveStart && address < saveStart + _saveRamSize)
                {
                    uint offset = address - saveStart;
                    uint avail = _saveRamSize - offset;
                    uint toCopy = Math.Min(numBytes, avail);
                    unsafe
                    {
                        Buffer.MemoryCopy((byte*)_saveRamPtr + offset, (byte*)buffer, toCopy, toCopy);
                    }
                    return toCopy;
                }
            }

            return 0; // address not mapped
        }

        // ── HTTP callback ────────────────────────────────────────────────────

        private void OnServerCall(IntPtr requestPtr, ServerCallbackFunc callback, IntPtr callbackData, IntPtr client)
        {
            // Read the request struct — only the first 3 pointers matter.
            IntPtr urlPtr = Marshal.ReadIntPtr(requestPtr, 0);
            IntPtr postDataPtr = Marshal.ReadIntPtr(requestPtr, IntPtr.Size);
            IntPtr contentTypePtr = Marshal.ReadIntPtr(requestPtr, IntPtr.Size * 2);

            string? url = PtrToStringUTF8(urlPtr);
            string? postData = PtrToStringUTF8(postDataPtr);
            string? contentType = PtrToStringUTF8(contentTypePtr);

            // Capture the raw native function pointer so it survives GC of the
            // delegate wrapper.
            IntPtr callbackFnPtr = Marshal.GetFunctionPointerForDelegate(callback);

            if (string.IsNullOrEmpty(url))
            {
                InvokeServerCallback(callbackFnPtr, callbackData, IntPtr.Zero, UIntPtr.Zero, 0);
                return;
            }

            Trace.WriteLine($"[RA] HTTP → {(postData != null ? "POST" : "GET")} {url}");

            Task.Run(async () =>
            {
                try
                {
                    HttpResponseMessage response;
                    if (!string.IsNullOrEmpty(postData))
                    {
                        var content = new StringContent(postData, System.Text.Encoding.UTF8,
                            contentType ?? "application/x-www-form-urlencoded");
                        response = await _http.PostAsync(url, content);
                    }
                    else
                    {
                        response = await _http.GetAsync(url);
                    }

                    string body = await response.Content.ReadAsStringAsync();
                    int statusCode = (int)response.StatusCode;
                    Trace.WriteLine($"[RA] HTTP ← {statusCode} ({body.Length} bytes)");

                    IntPtr bodyPtr = Marshal.StringToCoTaskMemUTF8(body);
                    InvokeServerCallback(callbackFnPtr, callbackData, bodyPtr, (UIntPtr)body.Length, statusCode);
                    Marshal.FreeCoTaskMem(bodyPtr);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RA] HTTP error: {ex.Message}");
                    InvokeServerCallback(callbackFnPtr, callbackData, IntPtr.Zero, UIntPtr.Zero, 0);
                }
            });
        }

        /// <summary>Builds rc_api_server_response_t and calls the native callback pointer.</summary>
        private static void InvokeServerCallback(IntPtr callbackFnPtr, IntPtr callbackData,
            IntPtr body, UIntPtr bodyLength, int httpStatusCode)
        {
            var resp = new rc_api_server_response_t
            {
                body = body,
                body_length = bodyLength,
                http_status_code = httpStatusCode
            };
            IntPtr respPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<rc_api_server_response_t>());
            try
            {
                Marshal.StructureToPtr(resp, respPtr, false);
                var fn = Marshal.GetDelegateForFunctionPointer<ServerCallbackFunc>(callbackFnPtr);
                fn(respPtr, callbackData);
            }
            finally
            {
                Marshal.FreeCoTaskMem(respPtr);
            }
        }

        // ── Event handler ────────────────────────────────────────────────────

        private void OnEvent(IntPtr eventPtr, IntPtr client)
        {
            if (eventPtr == IntPtr.Zero) return;

            var evt = Marshal.PtrToStructure<rc_client_event_t>(eventPtr);

            switch (evt.type)
            {
                case RC_CLIENT_EVENT_ACHIEVEMENT_TRIGGERED:
                    if (evt.achievement != IntPtr.Zero)
                    {
                        var info = ReadAchievementInfo(evt.achievement);
                        Trace.WriteLine($"[RA] Achievement triggered: {info.Title} ({info.Points} pts)");
                        AchievementTriggered?.Invoke(info);
                    }
                    break;

                case RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_SHOW:
                case RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_UPDATE:
                    if (evt.achievement != IntPtr.Zero)
                    {
                        var info = ReadAchievementInfo(evt.achievement);
                        // Emu-thread hot path: capture into the live snapshot
                        // dict; SQLite write is deferred to game-exit flush.
                        if (info.Id > 0 && info.MeasuredPercent > 0)
                            _liveProgress[(int)info.Id] = info;
                        ProgressIndicatorChanged?.Invoke(info, true);
                    }
                    break;

                case RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_HIDE:
                    ProgressIndicatorChanged?.Invoke(null, false);
                    break;

                // Challenge indicators: a primed challenge achievement ("beat the boss without
                // dying") shows its badge while the condition is being attempted; HIDE fires when
                // it un-primes (failed or completed). Several can be active at once.
                case RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_SHOW:
                case RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_HIDE:
                    if (evt.achievement != IntPtr.Zero)
                    {
                        var chInfo = ReadAchievementInfo(evt.achievement);
                        if (chInfo.Id > 0)
                            ChallengeIndicatorChanged?.Invoke(chInfo,
                                evt.type == RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_SHOW);
                    }
                    break;

                case RC_CLIENT_EVENT_LEADERBOARD_SCOREBOARD:
                    if (evt.leaderboard_scoreboard != IntPtr.Zero)
                    {
                        var sb = Marshal.PtrToStructure<rc_client_leaderboard_scoreboard_t>(evt.leaderboard_scoreboard);
                        string lbTitle = "";
                        bool lowerIsBetter = false;
                        if (evt.leaderboard != IntPtr.Zero)
                        {
                            var lb = Marshal.PtrToStructure<rc_client_leaderboard_t>(evt.leaderboard);
                            lbTitle = PtrToStringUTF8(lb.title) ?? "";
                            lowerIsBetter = lb.lower_is_better != 0;
                        }
                        var info = new LbScoreboardInfo(
                            LeaderboardId: (int)sb.leaderboard_id,
                            NewRank: (int)sb.new_rank,
                            SubmittedScore: DecodeFixed(sb.submitted_score),
                            BestScore: DecodeFixed(sb.best_score),
                            LbTitle: lbTitle,
                            LowerIsBetter: lowerIsBetter);
                        RaLog.Write($"[RA] LB scoreboard lb={info.LeaderboardId} title=[{info.LbTitle}] rank=#{info.NewRank} submitted=[{info.SubmittedScore}] best=[{info.BestScore}] lib={info.LowerIsBetter}");
                        LeaderboardScoreboardReceived?.Invoke(info);
                    }
                    break;

                case RC_CLIENT_EVENT_GAME_COMPLETED:
                    Trace.WriteLine("[RA] Game completed (all achievements earned)!");
                    GameCompleted?.Invoke();
                    break;

                case RC_CLIENT_EVENT_RESET:
                    Trace.WriteLine("[RA] Reset requested by rcheevos.");
                    ResetRequested?.Invoke();
                    break;

                case RC_CLIENT_EVENT_SERVER_ERROR:
                    if (evt.server_error != IntPtr.Zero)
                    {
                        IntPtr msgPtr = Marshal.ReadIntPtr(evt.server_error, 0);
                        string? msg = PtrToStringUTF8(msgPtr);
                        Trace.WriteLine($"[RA] Server error: {msg}");
                    }
                    break;

                case RC_CLIENT_EVENT_DISCONNECTED:
                    Trace.WriteLine("[RA] Disconnected from server.");
                    break;

                case RC_CLIENT_EVENT_RECONNECTED:
                    Trace.WriteLine("[RA] Reconnected to server.");
                    break;
            }
        }

        private static AchievementInfo ReadAchievementInfo(IntPtr achPtr)
        {
            var ach = Marshal.PtrToStructure<rc_client_achievement_t>(achPtr);

            // v11.6.0: badge URL comes from the accessor, not a struct field.
            // Ask for the unlocked variant — this path feeds the unlock toast.
            var urlBuf = new byte[256];
            int rc = rc_client_achievement_get_image_url(
                achPtr, RC_CLIENT_ACHIEVEMENT_STATE_UNLOCKED, urlBuf, (UIntPtr)urlBuf.Length);

            return new AchievementInfo
            {
                Id = ach.id,
                Title = PtrToStringUTF8(ach.title) ?? "",
                Description = PtrToStringUTF8(ach.description) ?? "",
                Points = ach.points,
                BadgeUrl = BufferToString(urlBuf, rc),
                MeasuredProgress = System.Text.Encoding.UTF8.GetString(ach.measured_progress ?? Array.Empty<byte>()).TrimEnd('\0'),
                MeasuredPercent = ach.measured_percent,
                Rarity = ach.rarity,
                RarityHardcore = ach.rarity_hardcore,
                Type = ach.type
            };
        }

        // ── Logging ──────────────────────────────────────────────────────────

        private static void OnLogMessage(IntPtr messagePtr, IntPtr client)
        {
            string? msg = PtrToStringUTF8(messagePtr);
            if (msg != null)
                Trace.WriteLine($"[rcheevos] {msg}");
        }

        // ── Console ID mapping ───────────────────────────────────────────────

        public static uint GetConsoleId(string consoleName)
        {
            return consoleName switch
            {
                "NES"          => RC_CONSOLE_NINTENDO,
                "FDS"          => RC_CONSOLE_FAMICOM_DISK_SYSTEM,
                "SNES"         => RC_CONSOLE_SUPER_NINTENDO,
                "N64"          => RC_CONSOLE_NINTENDO_64,
                "GameCube"     => RC_CONSOLE_GAMECUBE,
                "GB"           => RC_CONSOLE_GAMEBOY,
                "GBC"          => RC_CONSOLE_GAMEBOY_COLOR,
                "GBA"          => RC_CONSOLE_GAMEBOY_ADVANCE,
                "NDS"          => RC_CONSOLE_NINTENDO_DS,
                "VirtualBoy"   => RC_CONSOLE_VIRTUAL_BOY,
                "Genesis"      => RC_CONSOLE_MEGA_DRIVE,
                "SegaCD"       => RC_CONSOLE_SEGA_CD,
                "Sega32X"      => RC_CONSOLE_SEGA_32X,
                "SMS"          => RC_CONSOLE_MASTER_SYSTEM,
                "GameGear"     => RC_CONSOLE_GAME_GEAR,
                "SG1000"       => RC_CONSOLE_SG1000,
                "Saturn"       => RC_CONSOLE_SATURN,
                "Dreamcast"    => RC_CONSOLE_DREAMCAST,
                "PS1"          => RC_CONSOLE_PLAYSTATION,
                "PS2"          => RC_CONSOLE_PLAYSTATION_2,
                "PSP"          => RC_CONSOLE_PSP,
                "TG16"         => RC_CONSOLE_PC_ENGINE,
                "TGCD"         => RC_CONSOLE_PC_ENGINE_CD,
                "NGP"          => RC_CONSOLE_NEOGEO_POCKET,
                // Neo Geo carts: RA classes them under Arcade (filename-based
                // MD5 hash, MAME short names) — see upstream's notes.
                "NeoGeo"       => RC_CONSOLE_ARCADE,
                "NeoCD"        => RC_CONSOLE_NEO_GEO_CD,
                "Atari2600"    => RC_CONSOLE_ATARI_2600,
                "Atari7800"    => RC_CONSOLE_ATARI_7800,
                "Jaguar"       => RC_CONSOLE_ATARI_JAGUAR,
                "ColecoVision" => RC_CONSOLE_COLECOVISION,
                "Vectrex"      => RC_CONSOLE_VECTREX,
                "3DO"          => RC_CONSOLE_3DO,
                "CDi"          => RC_CONSOLE_CDI,
                "3DS"          => RC_CONSOLE_NINTENDO_3DS,
                "Arcade"       => RC_CONSOLE_ARCADE,
                _ => 0
            };
        }

        // ── Cleanup ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_client != IntPtr.Zero)
            {
                try { rc_client_unload_game(_client); } catch { }
                try { rc_client_destroy(_client); } catch { }
                _client = IntPtr.Zero;
            }

            _core = null;
            GC.SuppressFinalize(this);
        }

        ~RetroAchievementsClient() => Dispose();
    }

    /// <summary>Achievement data passed to event handlers.</summary>
    public class AchievementInfo
    {
        public uint Id { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public uint Points { get; set; }
        public string? BadgeUrl { get; set; }
        public string MeasuredProgress { get; set; } = "";
        public float MeasuredPercent { get; set; }
        public float Rarity { get; set; }
        public float RarityHardcore { get; set; }
        public byte Type { get; set; }
    }
}
