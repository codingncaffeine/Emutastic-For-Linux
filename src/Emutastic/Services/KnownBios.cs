using System;
using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Services
{
    /// <summary>One BIOS file the BIOS panel knows about. Md5 null = presence-only check.</summary>
    public record BiosEntry(
        string Console,
        string ConsoleDisplay,
        string Filename,
        string Description,
        long ExpectedSize,
        string? Md5,
        string[]? AltMd5s = null); // other known-good dumps — drag-drop recognition only

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
            // PlayStation 2 — dumps live in the pcsx2/bios/ subfolder. Any valid
            // 4 MB dump works; these are common known-good ones (any one is enough).
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0230a-20080220.bin","USA v2.30 (recommended)",4194304,"21038400dc633070a78ad53090c53017"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0230e-20080220.bin","Europe v2.30",4194304,"dc69f0643a3030aaa4797501b483d6c4"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0230j-20080220.bin","Japan v2.30",4194304,"80ac46fa7e77b8ab4366e86948e54f83"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0220a-20060905.bin","USA v2.20",4194304,"40c11c063b3b9409aa5e4058e984e30c"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0170a-20030325.bin","USA v1.70",4194304,"8aa12ce243210128c5074552d3b86251"),
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
            // GameCube IPL (optional — Dolphin boots without it, but the dump
            // restores the official IPL fonts; without it games that render
            // text through the font ROM (e.g. Star Fox Assault) show missing
            // or misplaced text. The NTSC dump is shared by USA and Japan;
            // PAL covers Europe. Md5 is null (presence-only — dev/NR dumps
            // exist beyond the known set); AltMd5s carries the known retail
            // dumps (libretro-database System.dat) so drag-drop recognizes
            // them under any filename (gc-ntsc-10.bin, *.ipl, zipped…).
            new("GameCube","GameCube","GC/USA/IPL.bin","USA — optional; restores official IPL fonts (fixes e.g. Star Fox Assault text)",2097152,null,
                new[]{ "fc924a7c879b661abc37cec4f018fdf3",    // NTSC 1.0
                       "019e39822a9ca3029124f74dd4d55ac4",    // NTSC 1.1
                       "b17148254a5799684c7d783206504926" }), // NTSC 1.2
            new("GameCube","GameCube","GC/JAP/IPL.bin","Japan — optional; same NTSC dump as USA",2097152,null,
                new[]{ "fc924a7c879b661abc37cec4f018fdf3",    // NTSC 1.0
                       "019e39822a9ca3029124f74dd4d55ac4",    // NTSC 1.1
                       "b17148254a5799684c7d783206504926" }), // NTSC 1.2
            new("GameCube","GameCube","GC/EUR/IPL.bin","Europe — optional; PAL dump",2097152,null,
                new[]{ "0cdda509e2da83c85bfe423dd87346cc",    // PAL 1.0
                       "339848a0b7c2124cf155276c1e79cbd0",    // PAL 1.1
                       "db92574caab77a7ec99d4605fd6f2450" }), // PAL 1.2
        };

        // ── Recognition (shared by drag-drop and the ROM-folder auto-import) ──

        // Returns the best KnownBios match for (filename, size, md5). md5 may be null
        // when the caller hasn't computed it yet — tier 1 is skipped in that case.
        // openStream (optional) lets content-based tiers peek at the file bytes
        // (used for GameCube IPL dumps, which ship under arbitrary filenames).
        internal static BiosEntry? MatchKnownBios(string entryName, long size, string? md5,
            Func<System.IO.Stream>? openStream = null)
        {
            if (md5 != null)
            {
                var hashMatch = All.FirstOrDefault(b =>
                    (b.Md5 != null && string.Equals(b.Md5, md5, StringComparison.OrdinalIgnoreCase))
                    || (b.AltMd5s != null && b.AltMd5s.Contains(md5, StringComparer.OrdinalIgnoreCase)));
                if (hashMatch != null) return hashMatch;
            }

            // GameCube IPL dumps: identify by content (exact 2 MB + plaintext
            // copyright header) so revisions missing from the hash table still
            // route to the right region folder regardless of filename.
            if (size == GcIplSize && openStream != null)
            {
                string? gcRegion = SniffGcIplRegion(openStream);
                if (gcRegion != null)
                    return All.FirstOrDefault(b => b.Filename == $"GC/{gcRegion}/IPL.bin");
            }

            var sizeMatch = All.FirstOrDefault(b =>
                string.Equals(System.IO.Path.GetFileName(b.Filename), entryName, StringComparison.OrdinalIgnoreCase)
                && (b.ExpectedSize == 0 || b.ExpectedSize == size));
            if (sizeMatch != null) return sizeMatch;

            return All.FirstOrDefault(b =>
                string.Equals(System.IO.Path.GetFileName(b.Filename), entryName, StringComparison.OrdinalIgnoreCase));
        }

        internal const long GcIplSize = 2097152; // every retail GC IPL dump is exactly 2 MB

        // Every retail GameCube IPL begins with this plaintext copyright header
        // (verbatim from Dolphin's EXI_DeviceIPL.cpp; the rest of the ROM is
        // scrambled). PAL revisions append a "PAL  Revision …" marker; NTSC
        // revisions (shared by USA and Japan) do not.
        private const string GcIplHeader =
            "(C) 1999-2001 Nintendo.  All rights reserved."
          + "(C) 1999 ArtX Inc.  All rights reserved.";

        // Returns "EUR" or "USA" when the stream is a GameCube IPL dump, else null.
        // (NTSC dumps land on USA; callers mirror them to JAP via GcIplTargets.)
        private static string? SniffGcIplRegion(Func<System.IO.Stream> openStream)
        {
            try
            {
                using var s = openStream();
                byte[] head = new byte[0x100];
                int read = 0;
                while (read < head.Length)
                {
                    int n = s.Read(head, read, head.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < head.Length) return null;
                string text = System.Text.Encoding.ASCII.GetString(head);
                if (!text.StartsWith(GcIplHeader, StringComparison.Ordinal)) return null;
                return text.Contains("PAL", StringComparison.Ordinal) ? "EUR" : "USA";
            }
            catch { return null; }
        }

        // The NTSC GameCube IPL serves both the USA and JAP folders — a
        // recognized NTSC dump is written to both so either region's games
        // pick it up. Everything else maps to exactly its own entry.
        internal static BiosEntry[] GcIplTargets(BiosEntry match)
        {
            if (match.Console != "GameCube") return new[] { match };
            string sibling = match.Filename switch
            {
                "GC/USA/IPL.bin" => "GC/JAP/IPL.bin",
                "GC/JAP/IPL.bin" => "GC/USA/IPL.bin",
                _ => ""
            };
            var sib = All.FirstOrDefault(b => b.Filename == sibling);
            return sib != null ? new[] { match, sib } : new[] { match };
        }
    }
}
