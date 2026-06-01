using System.Collections.Generic;

namespace Emutastic.Services
{
    /// <summary>One BIOS file the BIOS panel knows about. Md5 null = presence-only check.</summary>
    public record BiosEntry(
        string Console,
        string ConsoleDisplay,
        string Filename,
        string Description,
        long ExpectedSize,
        string? Md5);

    /// <summary>
    /// Static BIOS manifest (verbatim from upstream PreferencesWindow.xaml.cs). Platform-neutral
    /// data — filenames, expected sizes, MD5s — used by the System Files panel's scan + the
    /// drag-drop importer's identity matching, and by CoreManager's launch-time BIOS pre-flight.
    /// </summary>
    public static class KnownBios
    {
        public static readonly List<BiosEntry> All = new()
        {
            // PlayStation
            new("PS1","PlayStation","scph5501.bin","USA v3.0 (recommended)",524288,"490f666e1afb15b7362b406ed1cea246"),
            new("PS1","PlayStation","scph5500.bin","Japan v3.0",524288,"8dd7d5296a650fac7319bce665a6a53c"),
            new("PS1","PlayStation","scph5502.bin","Europe v3.0",524288,"32736f17079d0b2b7024407c39bd3050"),
            new("PS1","PlayStation","scph1001.bin","USA v2.2",524288,"37157331b6d4d325cb9f597ea42cd597"),
            new("PS1","PlayStation","scph7001.bin","USA v4.1",524288,"502224b6d23561a46e5a7ba01a1fed62"),
            // Sega CD
            new("SegaCD","Sega CD","bios_CD_U.bin","USA",131072,"2efd74e3232ff260e371b99f84024f7f"),
            new("SegaCD","Sega CD","bios_CD_J.bin","Japan",131072,"278a9397d192149e84e820ac621a8edd"),
            new("SegaCD","Sega CD","bios_CD_E.bin","Europe",131072,"e66fa1dc5820d254611fdcdba0662372"),
            // Saturn
            new("Saturn","Saturn","sega_101.bin","Japan v1.00",524288,"85ec9ca47d8f6807718151cbcca8b964"),
            new("Saturn","Saturn","mpr-17933.bin","Japan v1.01",524288,"3240872c70984b6cbfda1586cab68dbe"),
            new("Saturn","Saturn","mpr-17941.bin","USA/Europe v1.01 (recommended)",524288,"4df44ac9af0e58fc63b0e2af9cec25a9"),
            new("Saturn","Saturn","kronos/saturn_bios.bin","Kronos (any region)",524288,null),
            // Famicom Disk System
            new("FDS","Famicom Disk System","disksys.rom","",8192,"ca30b50f880eb660a320674ed365ef7a"),
            // TurboGrafx-CD
            new("TGCD","TurboGrafx-CD","syscard3.pce","System Card v3.0 (recommended)",262144,"0754f903b52e3b3342202bdafb13efa5"),
            new("TGCD","TurboGrafx-CD","syscard2.pce","System Card v2.1",131072,null),
            new("TGCD","TurboGrafx-CD","syscard1.pce","System Card v1.0",131072,null),
            // 3DO
            new("3DO","3DO","panafz10.bin","Panasonic FZ-10",1048576,"51f2f43ae2f3508a14d9f56597e2d3ce"),
            new("3DO","3DO","panafz1j.bin","Panasonic FZ-1 (Japan)",1048576,null),
            new("3DO","3DO","goldstar.bin","GoldStar",1048576,null),
            // Philips CD-i (place cdibios.zip in the System folder)
            new("CDi","Philips CD-i","cdibios.zip","CD-i BIOS (required)",0,null),
            // Neo Geo (Geolith)
            new("NeoGeo","Neo Geo","neogeo.zip","Neo Geo BIOS (required)",0,null),
            new("NeoGeo","Neo Geo","aes.zip","AES BIOS (required)",0,null),
            // Neo Geo CD
            new("NeoCD","Neo Geo CD","neogeo.zip","Cart BIOS (required, same as Neo Geo)",0,null),
            new("NeoCD","Neo Geo CD","aes.zip","AES BIOS (required, same as Neo Geo)",0,null),
            new("NeoCD","Neo Geo CD","neocdz.zip","CDZ BIOS archive (required for CD games)",0,null),
            // Game Boy Advance (optional — mgba has built-in HLE BIOS)
            new("GBA","Game Boy Advance","gba_bios.bin","BIOS (optional, improves compatibility)",16384,"a860e8c0b6d573d191e4ec7db1b1e4f6"),
        };
    }
}
