using System;
using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Services
{
    /// <summary>
    /// One console exactly as the sidebar presents it: the tag stored on every Game row,
    /// the label shown next to the icon, and the manufacturer heading it sits under
    /// (null = a standalone entry above the groups, which today is only Arcade).
    /// </summary>
    public sealed record ConsoleCatalogEntry(string Tag, string DisplayName, string? Group);

    /// <summary>
    /// The sidebar's console list as DATA, replacing the hand-written markup that used to
    /// spell out every button. <see cref="Default"/> is a transcription of that markup —
    /// same entries, same order, same labels, same headings — so "Restore Defaults" is
    /// literally the layout the app has always shipped, not a reconstruction of it.
    ///
    /// Scope is deliberately narrow: order, label and sidebar heading. Icons still come
    /// from <see cref="Converters.ConsoleTagToIconConverter"/> and manufacturer metadata
    /// still comes from RomService — this is not a second copy of either.
    ///
    /// ⛔ Any edit here changes what users see. `packaging/verify-console-catalog.mjs`
    /// diffs this list against the sidebar markup's history and fails on a mismatch.
    /// </summary>
    public static class ConsoleCatalog
    {
        /// <summary>Arcade is drawn with a glyph rather than an icon asset (Linux only —
        /// upstream Windows uses arcade.png). Kept here so the renderer stays data-driven.</summary>
        public const string ArcadeGlyph = "🕹";

        /// <summary>Manufacturer headings, in the order the sidebar shows them.</summary>
        public static readonly IReadOnlyList<string> DefaultGroupOrder = new[]
        {
            "ATARI", "NINTENDO", "SEGA", "SONY", "NEC", "SNK", "OTHER",
        };

        /// <summary>
        /// Every console the sidebar lists, in shipping order. PS3 is absent on purpose:
        /// it is a Windows-only platform (RPCS3), so this port has 35 entries to
        /// upstream's 36.
        /// </summary>
        public static readonly IReadOnlyList<ConsoleCatalogEntry> Default = new ConsoleCatalogEntry[]
        {
            // Standalone, above the manufacturer groups.
            new("Arcade",       "Arcade",              null),

            new("Atari2600",    "Atari 2600",          "ATARI"),
            new("Atari7800",    "Atari 7800",          "ATARI"),
            new("Jaguar",       "Atari Jaguar",        "ATARI"),

            new("NES",          "Nintendo (NES)",      "NINTENDO"),
            new("FDS",          "Famicom Disk System", "NINTENDO"),
            new("SNES",         "Super Nintendo",      "NINTENDO"),
            new("N64",          "Nintendo 64",         "NINTENDO"),
            new("GameCube",     "GameCube",            "NINTENDO"),
            new("GB",           "Game Boy",            "NINTENDO"),
            new("GBC",          "Game Boy Color",      "NINTENDO"),
            new("GBA",          "Game Boy Advance",    "NINTENDO"),
            new("3DS",          "Nintendo 3DS",        "NINTENDO"),
            new("NDS",          "Nintendo DS",         "NINTENDO"),
            new("VirtualBoy",   "Virtual Boy",         "NINTENDO"),

            new("SMS",          "Sega Master System",  "SEGA"),
            new("Genesis",      "Sega Genesis",        "SEGA"),
            new("SegaCD",       "Sega CD",             "SEGA"),
            new("Sega32X",      "Sega 32X",            "SEGA"),
            new("Saturn",       "Sega Saturn",         "SEGA"),
            new("GameGear",     "Sega Game Gear",      "SEGA"),
            new("SG1000",       "SG-1000",             "SEGA"),
            new("Dreamcast",    "Dreamcast",           "SEGA"),

            new("PS1",          "PlayStation",         "SONY"),
            new("PS2",          "PlayStation 2",       "SONY"),
            new("PSP",          "PSP",                 "SONY"),

            new("TG16",         "TurboGrafx-16",       "NEC"),
            new("TGCD",         "TurboGrafx-CD",       "NEC"),

            new("NeoGeo",       "Neo Geo",             "SNK"),
            new("NeoCD",        "Neo Geo CD",          "SNK"),
            new("NGP",          "NeoGeo Pocket",       "SNK"),

            new("3DO",          "3DO",                 "OTHER"),
            new("CDi",          "Philips CD-i",        "OTHER"),
            new("ColecoVision", "ColecoVision",        "OTHER"),
            new("Vectrex",      "Vectrex",             "OTHER"),
        };

        private static readonly Dictionary<string, ConsoleCatalogEntry> ByTag =
            Default.ToDictionary(e => e.Tag, StringComparer.OrdinalIgnoreCase);

        /// <summary>The entry for a tag, or null when the tag isn't a known console.</summary>
        public static ConsoleCatalogEntry? Find(string? tag)
            => string.IsNullOrEmpty(tag) ? null : ByTag.GetValueOrDefault(tag!);

        /// <summary>
        /// The label for a tag, falling back to the tag itself. A game whose console this
        /// build doesn't know (e.g. a Windows-platform entry arriving through cloud sync
        /// from the upstream app) still shows something readable rather than blank.
        /// </summary>
        public static string DisplayNameFor(string? tag)
            => Find(tag)?.DisplayName ?? tag ?? "";

        /// <summary>True when the tag is a console this build can actually present.</summary>
        public static bool IsKnown(string? tag) => Find(tag) != null;

        /// <summary>Entries under one heading, in shipping order.</summary>
        public static IEnumerable<ConsoleCatalogEntry> InGroup(string group)
            => Default.Where(e => string.Equals(e.Group, group, StringComparison.Ordinal));

        /// <summary>The standalone entries shown above the manufacturer groups.</summary>
        public static IEnumerable<ConsoleCatalogEntry> Ungrouped
            => Default.Where(e => e.Group == null);

        /// <summary>
        /// Applies a user-chosen order. Anything the user never placed keeps its catalog
        /// order AFTER the entries they did place, so a console added by a future update
        /// appears somewhere sensible instead of vanishing or jumping to the top.
        ///
        /// ⛔ The ONE implementation of this rule: the sidebar renderer and the Preferences
        /// editor must agree, or the preview and the real thing drift apart.
        /// </summary>
        public static IEnumerable<T> InUserOrder<T>(
            IEnumerable<T> items, IReadOnlyList<string>? order, Func<T, string> key)
        {
            if (order == null || order.Count == 0) return items;

            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++)
                if (!rank.ContainsKey(order[i])) rank[order[i]] = i;

            return items
                .Select((item, index) => (item, index))
                .OrderBy(t => rank.TryGetValue(key(t.item), out int r) ? r : int.MaxValue)
                .ThenBy(t => t.index)          // stable: ties keep catalog order
                .Select(t => t.item);
        }
    }
}
