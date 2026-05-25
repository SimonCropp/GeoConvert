/// <summary>
/// Minimal PNG encoder for 8-bit truecolor-with-alpha images. The image data is zlib-compressed with
/// the BCL <see cref="ZLibStream"/>; chunk CRCs are computed here. No third-party dependencies.
/// </summary>
static class Png
{
    static readonly byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    static readonly uint[] crcTable = BuildCrcTable();

    public static void Write(Stream stream, byte[] rgba, int width, int height)
    {
        stream.Write(signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
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
        WriteChunk(stream, "IHDR", header);

        WriteChunk(stream, "IDAT", Compress(AddFilterBytes(rgba, width, height)));
        WriteChunk(stream, "IEND", []);
    }

    static byte[] AddFilterBytes(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        var output = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            // A leading 0 selects the "None" row filter.
            output[y * (stride + 1)] = 0;
            Array.Copy(rgba, y * stride, output, y * (stride + 1) + 1, stride);
        }

        return output;
    }

    static byte[] Compress(byte[] data)
    {
        using var memory = new MemoryStream();
        using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return memory.ToArray();
    }

    static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
