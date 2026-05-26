/// <summary>
/// Minimal PNG encoder for 8-bit truecolor-with-alpha images. The image data is zlib-compressed with
/// the BCL <see cref="ZLibStream"/>; chunk CRCs are computed here. No third-party dependencies.
/// </summary>
static class Png
{
    static readonly byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    static readonly byte[] ihdrType = "IHDR"u8.ToArray();
    static readonly byte[] idatType = "IDAT"u8.ToArray();
    static readonly byte[] iendType = "IEND"u8.ToArray();
    // Slicing-by-8 CRC32 (poly 0xEDB88320). The first table is the canonical per-byte table; the next
    // seven tables shift it so eight bytes of input can be folded into the running CRC per iteration.
    static readonly uint[][] crcTables = BuildCrcTables();

    public static void Write(Stream stream, byte[] rgba, int width, int height)
    {
        stream.Write(signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        // bit depth
        header[8] = 8;
        // color type: truecolor with alpha
        header[9] = 6;
        // deflate
        header[10] = 0;
        // filter
        header[11] = 0;
        // no interlace
        header[12] = 0;
        WriteChunk(stream, ihdrType, header);

        var compressed = Compress(rgba, width, height);
        WriteChunk(stream, idatType, compressed);
        WriteChunk(stream, iendType, []);
    }

    static byte[] Compress(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        using var memory = new MemoryStream();
        using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, leaveOpen: true))
        {
            // Build one filtered row at a time and write it in a single Write — the old code did one
            // virtual WriteByte for the filter tag plus a separate Write per row.
            var row = new byte[stride + 1];
            // None filter: row[0] = 0; never reassigned per row so the leading byte is already correct.
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, y * stride, row, 1, stride);
                zlib.Write(row, 0, row.Length);
            }
        }

        return memory.ToArray();
    }

    static void WriteChunk(Stream stream, byte[] type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        stream.Write(type);
        stream.Write(data);

        var crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        crc = Update(crc, type);
        crc = Update(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    // Slicing-by-8: fold eight input bytes per iteration through eight precomputed tables. For an IDAT
    // chunk on a 1024×768 image that's ~3 MB of CRC work; the byte-by-byte original is ~5–8× slower.
    static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var i = 0;
        var t0 = crcTables[0];
        var t1 = crcTables[1];
        var t2 = crcTables[2];
        var t3 = crcTables[3];
        var t4 = crcTables[4];
        var t5 = crcTables[5];
        var t6 = crcTables[6];
        var t7 = crcTables[7];
        while (i + 8 <= data.Length)
        {
            crc ^= (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            crc = t7[crc & 0xFF]
                  ^ t6[(crc >> 8) & 0xFF]
                  ^ t5[(crc >> 16) & 0xFF]
                  ^ t4[crc >> 24]
                  ^ t3[data[i + 4]]
                  ^ t2[data[i + 5]]
                  ^ t1[data[i + 6]]
                  ^ t0[data[i + 7]];
            i += 8;
        }

        while (i < data.Length)
        {
            crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            i++;
        }

        return crc;
    }

    static uint[][] BuildCrcTables()
    {
        var tables = new uint[8][];
        var table0 = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table0[n] = c;
        }

        tables[0] = table0;
        for (var t = 1; t < 8; t++)
        {
            var prev = tables[t - 1];
            var next = new uint[256];
            for (var n = 0; n < 256; n++)
            {
                next[n] = (prev[n] >> 8) ^ table0[prev[n] & 0xFF];
            }

            tables[t] = next;
        }

        return tables;
    }
}
