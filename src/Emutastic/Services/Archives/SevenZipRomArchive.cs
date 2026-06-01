using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;

namespace Emutastic.Services.Archives
{
    // Adapter over SharpCompress — a pure-managed, cross-platform archive library that handles
    // .7z, .rar, .tar, .gz, .iso and others with NO native dependency. Replaces upstream's
    // SevenZipExtractor (a Windows 7z.dll wrapper) for the Linux port, which keeps the whole
    // archive path portable — important for the flash-drive/PortableData mode (nothing native
    // needs to travel with the data folder). Class name is kept so RomArchive.Open routing
    // (.zip → ZipRomArchive, everything else → here) is unchanged.
    internal sealed class SevenZipRomArchive : IRomArchive
    {
        private readonly IArchive _archive;

        public SevenZipRomArchive(string path)
        {
            // SharpCompress 0.49 renamed the entry point Open -> OpenArchive.
            _archive = ArchiveFactory.OpenArchive(path);
        }

        public IEnumerable<IRomArchiveEntry> Entries
        {
            get
            {
                foreach (var e in _archive.Entries)
                    yield return new Entry(e);
            }
        }

        public void Dispose() => _archive.Dispose();

        private sealed class Entry : IRomArchiveEntry
        {
            private readonly IArchiveEntry _e;
            public Entry(IArchiveEntry e) { _e = e; }

            public string Key         => _e.Key ?? "";
            // SharpCompress reports Size as long; keep the -1 sentinel for unknown/zero so the
            // fast-path size-match check stays honest (matches the upstream adapter's contract).
            public long   Size        => _e.Size <= 0 ? -1 : _e.Size;
            public bool   IsDirectory => _e.IsDirectory;

            // SharpCompress's per-entry stream is forward-only and single-pass; buffer small
            // entries (BIOS, manifests) into memory so callers can read repeatedly. Large
            // entries should use ExtractTo to stream straight to disk.
            public Stream OpenEntryStream()
            {
                var ms = new MemoryStream();
                using (var s = _e.OpenEntryStream()) s.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }

            public void ExtractTo(Stream destination)
            {
                using var s = _e.OpenEntryStream();
                s.CopyTo(destination);
            }
        }
    }
}
