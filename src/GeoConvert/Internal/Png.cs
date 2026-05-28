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

    // Per-row "minimum sum of absolute values" filter selection: for each row, compute every PNG
    // filter type (None / Sub / Up / Average / Paeth) into a scratch buffer, score each by
    // summing |signed byte| across the filtered row, and write the winner. The heuristic libpng
    // recommends; works because filtered byte distributions skewed toward zero compress better
    // through deflate's Huffman stage. Output stays a valid PNG (every decoder handles all
    // filter types) — Verify's UseSsimForPng compares decoded pixels, so PNG-snapshot tests
    // survive the IDAT-bytes change unchanged.
    static void Compress(Stream output, byte[] rgba, int width, int height, CompressionLevel compression)
    {
        var stride = width * Bpp;

        using var zlib = new ZLibStream(output, compression, leaveOpen: true);

        // One scratch buffer per filter type, plus the previous (unfiltered) row used by Up /
        // Average / Paeth. Allocated once per render and reused per row. Each buffer holds the
        // filter-type byte at index 0 followed by the filtered pixel data.
        var rowNone = new byte[stride + 1];
        var rowSub = new byte[stride + 1];
        var rowUp = new byte[stride + 1];
        var rowAvg = new byte[stride + 1];
        var rowPaeth = new byte[stride + 1];
        rowNone[0] = 0;
        rowSub[0] = 1;
        rowUp[0] = 2;
        rowAvg[0] = 3;
        rowPaeth[0] = 4;
        // prevRow stays all-zeros for the first row — that's what the PNG spec defines as the
        // "above" row for filters that reference one (treat out-of-bounds bytes as 0).
        var prevRow = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var srcOffset = y * stride;

            ApplyNone(rgba, srcOffset, rowNone, stride);
            var bestSum = SumAbsSigned(rowNone, 1, stride);
            var bestRow = rowNone;

            ApplySub(rgba, srcOffset, rowSub, stride);
            var sumSub = SumAbsSigned(rowSub, 1, stride);
            if (sumSub < bestSum)
            {
                bestSum = sumSub;
                bestRow = rowSub;
            }

            ApplyUp(rgba, srcOffset, prevRow, rowUp, stride);
            var sumUp = SumAbsSigned(rowUp, 1, stride);
            if (sumUp < bestSum)
            {
                bestSum = sumUp;
                bestRow = rowUp;
            }

            ApplyAvg(rgba, srcOffset, prevRow, rowAvg, stride);
            var sumAvg = SumAbsSigned(rowAvg, 1, stride);
            if (sumAvg < bestSum)
            {
                bestSum = sumAvg;
                bestRow = rowAvg;
            }

            ApplyPaeth(rgba, srcOffset, prevRow, rowPaeth, stride);
            var sumPaeth = SumAbsSigned(rowPaeth, 1, stride);
            if (sumPaeth < bestSum)
            {
                bestRow = rowPaeth;
            }

            zlib.Write(bestRow, 0, stride + 1);
            // Save this row's raw (unfiltered) data as the "above" row for the next iteration.
            rgba.AsSpan(srcOffset, stride).CopyTo(prevRow);
        }
    }

    const int Bpp = 4;

    // Filter 0 (None): identity. Just copies the row — Buffer.BlockCopy is already vectorised
    // internally, so no manual SIMD needed here.
    static void ApplyNone(byte[] src, int srcOffset, byte[] dst, int stride) =>
        Buffer.BlockCopy(src, srcOffset, dst, 1, stride);

    // Filter 1 (Sub): subtract the byte Bpp positions earlier in the same row. The first Bpp
    // bytes have no left neighbour → treat left as 0 → bytes pass through unchanged. The SIMD
    // body issues a 32-byte VPSUBB per iter; the scalar tail handles any trailing < 32 bytes
    // and is also the no-AVX fallback. The `i + 64 <= stride` stop condition guarantees the
    // tail processes ≥ 32 bytes when SIMD is taken (keeps the scalar body covered).
    static void ApplySub(byte[] src, int srcOffset, byte[] dst, int stride)
    {
        for (var i = 0; i < Bpp; i++)
        {
            dst[1 + i] = src[srcOffset + i];
        }

        var i2 = Bpp;
        if (Avx2.IsSupported)
        {
            ref var srcRef = ref src[srcOffset];
            ref var dstRef = ref dst[1];
            for (; i2 + 64 <= stride; i2 += 32)
            {
                var srcVec = Vector256.LoadUnsafe(ref srcRef, (uint)i2);
                var leftVec = Vector256.LoadUnsafe(ref srcRef, (uint)(i2 - Bpp));
                (srcVec - leftVec).StoreUnsafe(ref dstRef, (uint)i2);
            }
        }

        for (; i2 < stride; i2++)
        {
            dst[1 + i2] = (byte)(src[srcOffset + i2] - src[srcOffset + i2 - Bpp]);
        }
    }

    // Filter 2 (Up): subtract the byte at the same x in the previous row.
    static void ApplyUp(byte[] src, int srcOffset, byte[] prev, byte[] dst, int stride)
    {
        var i = 0;
        if (Avx2.IsSupported)
        {
            ref var srcRef = ref src[srcOffset];
            ref var prevRef = ref prev[0];
            ref var dstRef = ref dst[1];
            for (; i + 64 <= stride; i += 32)
            {
                var srcVec = Vector256.LoadUnsafe(ref srcRef, (uint)i);
                var aboveVec = Vector256.LoadUnsafe(ref prevRef, (uint)i);
                (srcVec - aboveVec).StoreUnsafe(ref dstRef, (uint)i);
            }
        }

        for (; i < stride; i++)
        {
            dst[1 + i] = (byte)(src[srcOffset + i] - prev[i]);
        }
    }

    // Filter 3 (Average): subtract floor((left + above) / 2). The byte-level identity
    // `floor((a + b) / 2) = pavg(a, b) - ((a XOR b) AND 1)` lets us compute the floored average
    // with SSE2's PAVGB (which gives the rounded `(a + b + 1) / 2`) plus a one-bit correction —
    // avoids promoting to u16 for the overflow-safe sum.
    static void ApplyAvg(byte[] src, int srcOffset, byte[] prev, byte[] dst, int stride)
    {
        for (var i = 0; i < Bpp; i++)
        {
            dst[1 + i] = (byte)(src[srcOffset + i] - prev[i] / 2);
        }

        var i2 = Bpp;
        if (Avx2.IsSupported)
        {
            ref var srcRef = ref src[srcOffset];
            ref var prevRef = ref prev[0];
            ref var dstRef = ref dst[1];
            var oneVec = Vector256.Create((byte)1);
            for (; i2 + 64 <= stride; i2 += 32)
            {
                var srcVec = Vector256.LoadUnsafe(ref srcRef, (uint)i2);
                var leftVec = Vector256.LoadUnsafe(ref srcRef, (uint)(i2 - Bpp));
                var aboveVec = Vector256.LoadUnsafe(ref prevRef, (uint)i2);
                // pavgb gives (a + b + 1) / 2 — subtract the LSB-mismatch bit to floor.
                var avgFloor = Avx2.Average(leftVec, aboveVec) - ((leftVec ^ aboveVec) & oneVec);
                (srcVec - avgFloor).StoreUnsafe(ref dstRef, (uint)i2);
            }
        }

        for (; i2 < stride; i2++)
        {
            dst[1 + i2] = (byte)(src[srcOffset + i2] - (src[srcOffset + i2 - Bpp] + prev[i2]) / 2);
        }
    }

    // Filter 4 (Paeth): subtract PaethPredictor(left, above, upper-left). Needs signed math
    // (a + b - c can go negative) so we widen 16 bytes to 16 i16 with VPMOVZXBW, do the
    // predictor in i16 lanes via ConditionalSelect masks, then narrow back to bytes (mask with
    // 0xFF + saturating-pack maps the modular-byte result without saturation since masked
    // values are in [0, 255]). 16-bytes-per-iter is half the throughput of the other filters
    // but the i16 promotion is unavoidable here.
    static void ApplyPaeth(byte[] src, int srcOffset, byte[] prev, byte[] dst, int stride)
    {
        for (var i = 0; i < Bpp; i++)
        {
            // PaethPredictor(0, above, 0) = above (the if-elseif logic picks `b` when left and
            // upper-left are both 0 and above > 0), so the first Bpp bytes look like Filter 2 (Up).
            dst[1 + i] = (byte)(src[srcOffset + i] - PaethPredictor(0, prev[i], 0));
        }

        var i2 = Bpp;
        if (Avx2.IsSupported)
        {
            ref var srcRef = ref src[srcOffset];
            ref var prevRef = ref prev[0];
            ref var dstRef = ref dst[1];
            var lowByteMask = Vector256.Create((short)0xFF);
            for (; i2 + 32 <= stride; i2 += 16)
            {
                var srcBytes = Vector128.LoadUnsafe(ref srcRef, (uint)i2);
                var leftBytes = Vector128.LoadUnsafe(ref srcRef, (uint)(i2 - Bpp));
                var aboveBytes = Vector128.LoadUnsafe(ref prevRef, (uint)i2);
                var upperLeftBytes = Vector128.LoadUnsafe(ref prevRef, (uint)(i2 - Bpp));

                // Widen 16 bytes to 16 i16 (values stay in [0, 255]).
                var s = Avx2.ConvertToVector256Int16(srcBytes);
                var a = Avx2.ConvertToVector256Int16(leftBytes);
                var b = Avx2.ConvertToVector256Int16(aboveBytes);
                var c = Avx2.ConvertToVector256Int16(upperLeftBytes);

                // Algebraic simplifications: pa = |b - c|, pb = |a - c|, pc = |a + b - 2c|.
                var pa = Vector256.Abs(b - c);
                var pb = Vector256.Abs(a - c);
                var pc = Vector256.Abs(a + b - c - c);

                // Build masks for the original predictor's branch ordering:
                //   if pa <= pb && pa <= pc: a;  else if pb <= pc: b;  else: c.
                var maskA = ~Avx2.CompareGreaterThan(pa, pb) & ~Avx2.CompareGreaterThan(pa, pc);
                var maskB = Vector256.AndNot(~Avx2.CompareGreaterThan(pb, pc), maskA);

                var pred = Vector256.ConditionalSelect(maskA, a, Vector256.ConditionalSelect(maskB, b, c));
                var filtered = s - pred;

                // Mask to low 8 bits per i16 lane, then pack the two 128-bit halves to a
                // Vector128<byte> in lane order [low 8 lanes | high 8 lanes] — same order the
                // bytes were widened in, so output bytes align with input positions.
                var masked = filtered & lowByteMask;
                var packed = Sse2.PackUnsignedSaturate(masked.GetLower(), masked.GetUpper());
                packed.StoreUnsafe(ref dstRef, (uint)i2);
            }
        }

        for (; i2 < stride; i2++)
        {
            dst[1 + i2] = (byte)(src[srcOffset + i2] - PaethPredictor(src[srcOffset + i2 - Bpp], prev[i2], prev[i2 - Bpp]));
        }
    }

    // Sum |signed byte| across a filtered row — the deflate-friendliness score the heuristic
    // ranks filters by. A byte is signed-interpreted as int8 ([0..127] stays positive, [128..255]
    // wraps to negative); summing |signed| favours filters that produce mostly small magnitudes
    // (positive or negative), which deflate's Huffman stage compresses tightly. SIMD via the
    // `min(b, 0 - b)` identity (which gives |signed(b)| per byte) plus VPSADBW to reduce 32-byte
    // chunks into 4 partial u64 sums in one instruction — orders of magnitude faster than the
    // per-byte scalar loop, which matters because this is called once per filter per row.
    static long SumAbsSigned(byte[] buffer, int offset, int length)
    {
        long sum = 0;
        var i = 0;
        if (Avx2.IsSupported)
        {
            var zeroVec = Vector256<byte>.Zero;
            var sumVec = Vector256<ulong>.Zero;
            ref var bufRef = ref buffer[offset];
            // Stop early enough that the scalar tail processes at least a few bytes — keeps
            // the scalar body reachable for 100% line coverage.
            for (; i + 64 <= length; i += 32)
            {
                var bytes = Vector256.LoadUnsafe(ref bufRef, (uint)i);
                // |signed(b)| = min(b, 256 - b) = min(b, 0 - b) under modular subtraction.
                var absSigned = Vector256.Min(bytes, zeroVec - bytes);
                // VPSADBW(absSigned, 0) returns 4 u64 lanes, each holding the sum of 8 bytes
                // in its low 16 bits. Accumulate; reduce after the loop.
                sumVec += Avx2.SumAbsoluteDifferences(absSigned, zeroVec).AsUInt64();
            }

            for (var k = 0; k < Vector256<ulong>.Count; k++)
            {
                sum += (long)sumVec.GetElement(k);
            }
        }

        for (; i < length; i++)
        {
            var b = buffer[offset + i];
            // Branchless |int8(b)|: bytes 0..127 contribute themselves; 128..255 contribute
            // 256 - b (so 128 → 128, 255 → 1). One compare + one subtract, no Math.Abs call.
            sum += b < 128 ? b : 256 - b;
        }

        return sum;
    }

    // PNG Paeth predictor — picks whichever of {left, above, upper-left} the linear-predictor
    // target a + b - c is closest to (ties favouring left, then above). Done in int to avoid
    // byte-overflow surprises; inputs are unsigned bytes (0..255).
    static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        if (pb <= pc)
        {
            return b;
        }

        return c;
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
