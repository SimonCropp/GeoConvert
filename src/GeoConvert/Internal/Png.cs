using System.IO.Hashing;

/// <summary>
/// Minimal PNG encoder for 8-bit truecolor-with-alpha images. The image data is zlib-compressed with
/// the BCL <see cref="ZLibStream"/>; chunk CRCs use <see cref="Crc32"/> from System.IO.Hashing, which
/// dispatches to PCLMULQDQ on x86 and PMULL on ARM — several × faster than the slicing-by-8 table
/// approach this previously used (the IDAT CRC on a 1024×768 image is ~3 MB of input).
/// </summary>
static class Png
{
    static readonly byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    static readonly byte[] ihdrType = "IHDR"u8.ToArray();
    static readonly byte[] idatType = "IDAT"u8.ToArray();
    static readonly byte[] iendType = "IEND"u8.ToArray();

    public static void Write(Stream stream, byte[] rgba, int width, int height, CompressionLevel compression)
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

        // Buffer the deflate output in a MemoryStream so we know the IDAT length before writing
        // the chunk header (PNG chunks are length-prefixed, so streaming-through isn't an option).
        // GetBuffer hands back the underlying byte[] live — we slice it to the written length and
        // pass a span to WriteChunk, sidestepping the ~270 KB ToArray copy + allocation the old
        // `return memory.ToArray()` paid on every render.
        using var compressed = new MemoryStream();
        Compress(compressed, rgba, width, height, compression);
        WriteChunk(stream, idatType, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(stream, iendType, []);
    }

    static void Compress(Stream output, byte[] rgba, int width, int height, CompressionLevel compression)
    {
        var stride = width * 4;
        using var zlib = new ZLibStream(output, compression, leaveOpen: true);
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

    static void WriteChunk(Stream stream, byte[] type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        stream.Write(type);
        stream.Write(data);

        // PNG CRCs cover chunk type then data — Crc32 accumulates across Append calls so we don't
        // need to concatenate first.
        var crc = new Crc32();
        crc.Append(type);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        stream.Write(crcBytes);
    }
}
