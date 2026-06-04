using System;
using System.IO;
using System.IO.Compression;

namespace Emutastic.Services
{
    /// <summary>
    /// Minimal dependency-free PNG writer for save-state screenshots. The game-host process has
    /// no Avalonia/Skia (clean GL environment — see GameHost.cs), so the WPF PngBitmapEncoder
    /// path upstream uses is replaced with a hand-rolled encoder: 8-bit RGBA, one IDAT,
    /// zlib via System.IO.Compression.ZLibStream (which emits the proper zlib header + Adler32).
    /// Input is the emu frame buffer's BGRA top-down layout (EmulatorSession._frame).
    /// </summary>
    public static class PngEncoder
    {
        public static void WriteBgra(string path, byte[] bgra, int width, int height)
        {
            if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
                throw new ArgumentException("PngEncoder: buffer smaller than width*height*4");

            // Raw scanlines: filter byte 0 (None) + RGBA pixels.
            var raw = new byte[height * (1 + width * 4)];
            int o = 0;
            for (int y = 0; y < height; y++)
            {
                raw[o++] = 0; // filter: None
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x * 4;
                    raw[o++] = bgra[i + 2]; // R
                    raw[o++] = bgra[i + 1]; // G
                    raw[o++] = bgra[i + 0]; // B
                    raw[o++] = 255;         // A — frame alpha is undefined (same hard-set as present path)
                }
            }

            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }

            using var fs = File.Create(path);
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // PNG signature

            Span<byte> ihdr = stackalloc byte[13];
            WriteBE(ihdr, 0, width);
            WriteBE(ihdr, 4, height);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 6;   // color type RGBA
            ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0; // deflate, none, no interlace
            WriteChunk(fs, "IHDR", ihdr.ToArray());
            WriteChunk(fs, "IDAT", idat);
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteBE(Span<byte> b, int off, int v)
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            WriteBE(len, 0, data.Length);
            s.Write(len);
            byte[] t = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(t);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(t, data);
            Span<byte> c = stackalloc byte[4];
            WriteBE(c, 0, unchecked((int)crc));
            s.Write(c);
        }

        // Standard PNG CRC-32 (poly 0xEDB88320) over chunk type + data.
        private static uint[]? _crcTable;
        private static uint Crc32(byte[] type, byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint v = n;
                    for (int k = 0; k < 8; k++) v = (v & 1) != 0 ? 0xEDB88320 ^ (v >> 1) : v >> 1;
                    _crcTable[n] = v;
                }
            }
            uint crc = 0xFFFFFFFF;
            foreach (byte b in type) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }
}
