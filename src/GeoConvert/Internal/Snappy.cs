/// <summary>
/// A dependency-free implementation of the Snappy block format — the default page compression for the
/// GeoParquet codec. <see cref="Decompress(byte[])"/> handles every tag form (literal plus 1/2/4-byte-offset
/// copies) so blocks from other tools read back; <see cref="Compress"/> uses a hash-matched encoder
/// over ≤64&#160;KB blocks, mirroring the reference algorithm.
/// </summary>
static class Snappy
{
    const int MaxBlockSize = 65536;
    const int MaxTableSize = 1 << 14;
    const int TableMask = MaxTableSize - 1;
    const int InputMargin = 15;
    const int MinNonLiteralBlockSize = 1 + 1 + InputMargin;

    public static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        WriteVarint(output, (uint)input.Length);
        var start = 0;
        while (start < input.Length)
        {
            var length = Math.Min(input.Length - start, MaxBlockSize);
            EncodeBlock(input, start, length, output);
            start += length;
        }

        return output.ToArray();
    }

    public static byte[] Decompress(byte[] input) => Decompress(input, 0, input.Length);

    // Decompresses the block held in input[start..start+length] without first copying it out — the
    // GeoParquet reader hands us a slice of a larger column-chunk buffer.
    public static byte[] Decompress(byte[] input, int start, int length)
    {
        var position = start;
        var end = start + length;
        var blockLength = (int)ReadVarint(input, ref position);
        var output = new byte[blockLength];
        var outPosition = 0;
        while (position < end)
        {
            var tag = input[position++];
            switch (tag & 0x03)
            {
                // literal
                case 0:
                {
                    var literal = tag >> 2;
                    if (literal >= 60)
                    {
                        var extra = literal - 59;
                        literal = 0;
                        for (var b = 0; b < extra; b++)
                        {
                            literal |= input[position++] << (8 * b);
                        }
                    }

                    literal++;
                    Array.Copy(input, position, output, outPosition, literal);
                    position += literal;
                    outPosition += literal;
                    break;
                }
                // copy, 1-byte offset
                case 1:
                {
                    var count = ((tag >> 2) & 0x07) + 4;
                    var offset = ((tag >> 5) << 8) | input[position++];
                    CopyBack(output, ref outPosition, offset, count);
                    break;
                }
                // copy, 2-byte offset
                case 2:
                {
                    var count = (tag >> 2) + 1;
                    var offset = input[position] | (input[position + 1] << 8);
                    position += 2;
                    CopyBack(output, ref outPosition, offset, count);
                    break;
                }
                // copy, 4-byte offset
                default:
                {
                    var count = (tag >> 2) + 1;
                    var offset = input[position] |
                                 (input[position + 1] << 8) |
                                 (input[position + 2] << 16) |
                                 (input[position + 3] << 24);
                    position += 4;
                    CopyBack(output, ref outPosition, offset, count);
                    break;
                }
            }
        }

        return output;
    }

    static void CopyBack(byte[] output, ref int outPosition, int offset, int count)
    {
        var source = outPosition - offset;
        for (var i = 0; i < count; i++)
        {
            output[outPosition++] = output[source + i];
        }
    }

    static void EncodeBlock(byte[] src, int start, int length, MemoryStream destination)
    {
        if (length < MinNonLiteralBlockSize)
        {
            EmitLiteral(destination, src, start, length);
            return;
        }

        var shift = 32 - 8;
        for (var tableSize = 1 << 8; tableSize < MaxTableSize && tableSize < length; tableSize <<= 1)
        {
            shift--;
        }

        var table = new int[MaxTableSize];
        var limit = length - InputMargin;
        var nextEmit = 0;
        var s = 1;
        var nextHash = Hash(Load32(src, start, s), shift);

        while (true)
        {
            var skip = 32;
            var nextS = s;
            int candidate;
            while (true)
            {
                s = nextS;
                var step = skip >> 5;
                nextS = s + step;
                skip += step;
                if (nextS > limit)
                {
                    goto emitRemainder;
                }

                candidate = table[nextHash & TableMask];
                table[nextHash & TableMask] = s;
                nextHash = Hash(Load32(src, start, nextS), shift);
                if (Load32(src, start, s) == Load32(src, start, candidate))
                {
                    break;
                }
            }

            EmitLiteral(destination, src, start + nextEmit, s - nextEmit);

            while (true)
            {
                var matchStart = s;
                s += 4;
                var i = candidate + 4;
                while (s < length && src[start + i] == src[start + s])
                {
                    i++;
                    s++;
                }

                EmitCopy(destination, matchStart - candidate, s - matchStart);
                nextEmit = s;
                if (s >= limit)
                {
                    goto emitRemainder;
                }

                var x = Load64(src, start, s - 1);
                table[Hash((uint)x, shift) & TableMask] = s - 1;
                var currentHash = Hash((uint)(x >> 8), shift);
                candidate = table[currentHash & TableMask];
                table[currentHash & TableMask] = s;
                if ((uint)(x >> 8) != Load32(src, start, candidate))
                {
                    nextHash = Hash((uint)(x >> 16), shift);
                    s++;
                    break;
                }
            }
        }

    emitRemainder:
        if (nextEmit < length)
        {
            EmitLiteral(destination, src, start + nextEmit, length - nextEmit);
        }
    }

    static void EmitLiteral(MemoryStream destination, byte[] src, int start, int length)
    {
        var n = length - 1;
        if (n < 60)
        {
            destination.WriteByte((byte)(n << 2));
        }
        else
        {
            var extra = 0;
            var value = n;
            while (value > 0)
            {
                extra++;
                value >>= 8;
            }

            destination.WriteByte((byte)((59 + extra) << 2));
            for (var b = 0; b < extra; b++)
            {
                destination.WriteByte((byte)(n >> (8 * b)));
            }
        }

        destination.Write(src, start, length);
    }

    static void EmitCopy(MemoryStream destination, int offset, int count)
    {
        while (count >= 68)
        {
            destination.WriteByte((63 << 2) | 0x02);
            destination.WriteByte((byte)offset);
            destination.WriteByte((byte)(offset >> 8));
            count -= 64;
        }

        if (count > 64)
        {
            destination.WriteByte((59 << 2) | 0x02);
            destination.WriteByte((byte)offset);
            destination.WriteByte((byte)(offset >> 8));
            count -= 60;
        }

        if (count >= 12 || offset >= 2048)
        {
            destination.WriteByte((byte)(((count - 1) << 2) | 0x02));
            destination.WriteByte((byte)offset);
            destination.WriteByte((byte)(offset >> 8));
            return;
        }

        destination.WriteByte((byte)(((offset >> 8) << 5) | ((count - 4) << 2) | 0x01));
        destination.WriteByte((byte)offset);
    }

    static uint Hash(uint value, int shift) => unchecked(value * 0x1e35a7bd) >> shift;

    static uint Load32(byte[] data, int start, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + index));

    static ulong Load64(byte[] data, int start, int index) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(start + index));

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
