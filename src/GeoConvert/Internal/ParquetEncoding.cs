/// <summary>
/// Parquet page-level encodings used by the GeoParquet codec: PLAIN (the only encoding GeoConvert
/// writes for values) and the RLE/bit-packed hybrid used for definition levels and dictionary indices.
/// The decoders are deliberately broader than the encoders so files written by other tools — which use
/// dictionary encoding and mixed RLE/bit-packed runs — read back correctly.
/// </summary>
static class ParquetEncoding
{
    public static byte[] PlainInt64(IReadOnlyList<long> values)
    {
        var bytes = new byte[values.Count * 8];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }

    public static byte[] PlainDouble(IReadOnlyList<double> values)
    {
        var bytes = new byte[values.Count * 8];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }

    public static byte[] PlainBool(IReadOnlyList<bool> values)
    {
        var bytes = new byte[(values.Count + 7) / 8];
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bytes;
    }

    public static byte[] PlainByteArray(IReadOnlyList<byte[]> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total += 4 + value.Length;
        }

        var bytes = new byte[total];
        var position = 0;
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position), value.Length);
            position += 4;
            value.CopyTo(bytes, position);
            position += value.Length;
        }

        return bytes;
    }

    public static long[] ReadPlainInt64(byte[] data, int offset, int count)
    {
        var values = new long[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + (i * 8)));
        }

        return values;
    }

    public static long[] ReadPlainInt32(byte[] data, int offset, int count)
    {
        var values = new long[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + (i * 4)));
        }

        return values;
    }

    public static double[] ReadPlainDouble(byte[] data, int offset, int count)
    {
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset + (i * 8)));
        }

        return values;
    }

    public static bool[] ReadPlainBool(byte[] data, int offset, int count)
    {
        var values = new bool[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (data[offset + (i / 8)] & (1 << (i % 8))) != 0;
        }

        return values;
    }

    public static byte[][] ReadPlainByteArray(byte[] data, int offset, int count)
    {
        var values = new byte[count][];
        var position = offset;
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
            position += 4;
            values[i] = data.AsSpan(position, length).ToArray();
            position += length;
        }

        return values;
    }

    /// <summary>Number of bits needed to represent values up to <paramref name="max"/> (minimum 1).</summary>
    public static int BitWidth(int max)
    {
        var width = 0;
        while (max > 0)
        {
            width++;
            max >>= 1;
        }

        return Math.Max(width, 1);
    }

    /// <summary>Encodes values as a single bit-packed run (used for definition levels).</summary>
    public static byte[] EncodeRle(IReadOnlyList<int> values, int bitWidth)
    {
        using var memory = new MemoryStream();
        var groups = (values.Count + 7) / 8;
        WriteVarint(memory, (uint)((groups << 1) | 1));

        var mask = bitWidth == 32 ? 0xFFFFFFFFL : (1L << bitWidth) - 1;
        long buffer = 0;
        var bits = 0;
        for (var i = 0; i < groups * 8; i++)
        {
            var value = i < values.Count ? values[i] : 0;
            buffer |= ((uint)value & mask) << bits;
            bits += bitWidth;
            while (bits >= 8)
            {
                memory.WriteByte((byte)(buffer & 0xFF));
                buffer >>= 8;
                bits -= 8;
            }
        }

        return memory.ToArray();
    }

    /// <summary>Decodes <paramref name="count"/> values from an RLE/bit-packed hybrid stream.</summary>
    public static int[] DecodeRle(byte[] data, int offset, int count, int bitWidth)
    {
        var values = new int[count];
        var position = offset;
        var produced = 0;
        var byteWidth = (bitWidth + 7) / 8;
        var mask = bitWidth >= 32 ? 0xFFFFFFFFL : (1L << bitWidth) - 1;

        while (produced < count)
        {
            var header = (int)ReadVarint(data, ref position);
            if ((header & 1) == 0)
            {
                // RLE run: a length followed by one value repeated.
                var runLength = header >> 1;
                var value = 0;
                for (var b = 0; b < byteWidth; b++)
                {
                    value |= data[position++] << (8 * b);
                }

                for (var i = 0; i < runLength && produced < count; i++)
                {
                    values[produced++] = value;
                }
            }
            else
            {
                // Bit-packed run: groups of 8 values, each bitWidth bits, least-significant first.
                var valuesInRun = (header >> 1) * 8;
                long buffer = 0;
                var bits = 0;
                for (var i = 0; i < valuesInRun; i++)
                {
                    while (bits < bitWidth)
                    {
                        buffer |= (long)data[position++] << bits;
                        bits += 8;
                    }

                    var value = (int)(buffer & mask);
                    buffer >>= bitWidth;
                    bits -= bitWidth;
                    if (produced < count)
                    {
                        values[produced++] = value;
                    }
                }
            }
        }

        return values;
    }

    static void WriteVarint(Stream stream, uint value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    static ulong ReadVarint(byte[] data, ref int position)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}
