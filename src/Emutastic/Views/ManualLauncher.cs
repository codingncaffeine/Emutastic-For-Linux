using System;
using System.IO;
using System.Threading.Tasks;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>
/// Opens a game's PDF manual. On Linux we degrade the upstream WebView2/PDF.js viewer
/// to the system PDF viewer via xdg-open (per the port plan); downloads the manual first
/// (ScreenScraper, via ArtworkFetchService) if it isn't cached yet.
/// </summary>
public static class ManualLauncher
{
    public static bool HasUsableManual(Game g)
        => !string.IsNullOrEmpty(g.ManualPath) && File.Exists(AppPaths.FromStoragePath(g.ManualPath));

    private static void OpenInSystemViewer(Game g)
    {
        ShellOpen.Open(AppPaths.FromStoragePath(g.ManualPath));
    }

    /// <summary>Library / detail-card path: download (with banner progress via the fetch
    /// service) if needed, then open in the system PDF viewer.</summary>
    public static async Task OpenOrDownloadAsync(Game game, ArtworkFetchService fetch)
    {
        if (HasUsableManual(game)) { OpenInSystemViewer(game); return; }
        string? path = await fetch.FetchManualForGameAsync(game);
        if (path != null) OpenInSystemViewer(game);
    }
}
