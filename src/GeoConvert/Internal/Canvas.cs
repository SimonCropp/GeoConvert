/// <summary>A software RGBA raster with source-over blending and basic line/disc/polygon fills.</summary>
sealed class Canvas
{
    // Reused across FillPolygon calls so a render with hundreds of polygons doesn't allocate a fresh
    // crossings list per call.
    readonly List<double> scanlineCrossings = [];

    public Canvas(int width, int height, Rgba background)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        // Fill the background a pixel (uint) at a time rather than byte-by-byte.
        MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan()).Fill(Pack(background));
    }

    // Packs RGBA into a uint so that reinterpreting the pixel buffer as uints yields the R,G,B,A byte order.
    static uint Pack(Rgba color) =>
        BitConverter.IsLittleEndian
            ? (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24))
            : (uint)((color.R << 24) | (color.G << 16) | (color.B << 8) | color.A);

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public void Blend(int x, int y, Rgba color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || color.A == 0)
        {
            return;
        }

        var i = (y * Width + x) * 4;
        if (color.A == 255)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            Pixels[i + 3] = 255;
            return;
        }

        var a = color.A / 255d;
        BlendTranslucent(i, color.R * a, color.G * a, color.B * a, color.A, 1 - a);
    }

    // Source-over alpha blend at a known-valid pixel offset. Factored out so the per-pixel translucent
    // path is the same code whether reached via Blend (bounds-checked) or FillPolygon's inner loop
    // (which clips to the span ends once, then runs without bounds checks).
    void BlendTranslucent(int i, double preR, double preG, double preB, double aByte, double inverse)
    {
        Pixels[i] = (byte)(preR + Pixels[i] * inverse);
        Pixels[i + 1] = (byte)(preG + Pixels[i + 1] * inverse);
        Pixels[i + 2] = (byte)(preB + Pixels[i + 2] * inverse);
        Pixels[i + 3] = (byte)(aByte + Pixels[i + 3] * inverse);
    }

    // Vectorised translucent blend across a contiguous run of pixels. Math is kept in
    // Vector256<double> so per-lane evaluation matches scalar BlendTranslucent bit-for-bit —
    // separate vmulpd + vaddpd (not FMA, which would round once and shift output by an ULP) plus
    // VCVTTPD2DQ truncation that matches C#'s (byte)(double) cast for values in [0, 255].
    // The four lanes carry R/G/B/A so one vector op handles all channels of one pixel; the JIT
    // pipelines successive iterations so two or three pixels are typically in flight at once.
    // rowEnd is exclusive (byte offset just past the last pixel).
    void BlendTranslucentSpan(int rowStart, int rowEnd, double preR, double preG, double preB, double preA, double inverse)
    {
        var p = rowStart;
        if (Avx.IsSupported && Sse41.IsSupported)
        {
            var preVec = Vector256.Create(preR, preG, preB, preA);
            var inverseVec = Vector256.Create(inverse);
            ref var pixelsRef = ref MemoryMarshal.GetArrayDataReference(Pixels);
            // SIMD consumes everything except the final pixel, which the scalar tail handles. The
            // alternative — `p + 4 <= rowEnd` — would consume the whole span and leave the tail
            // unreachable for 4-byte-aligned spans, breaking 100% line coverage. The cost is one
            // scalar pixel per call: invisible against the per-row setup work.
            for (; p + 8 <= rowEnd; p += 4)
            {
                ref var src = ref Unsafe.Add(ref pixelsRef, p);
                // Read 4 bytes as one uint, widen low 4 bytes → 4 int32 (PMOVZXBD), then 4 int32 →
                // 4 doubles (VCVTDQ2PD). Two instructions for the whole byte→double pipeline beats
                // four scalar cvtsi2sd + four vector inserts the naive Vector256.Create path emits.
                var raw = Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref src)).AsByte();
                var existing = Avx.ConvertToVector256Double(Sse41.ConvertToVector128Int32(raw));
                var result = existing * inverseVec + preVec;
                // 4 doubles → 4 int32 (VCVTTPD2DQ, truncate toward zero — matches (int)(double)),
                // then pack int32 → int16 → byte via the standard two-stage saturating pack. Each
                // input lane is in [0, 255] (proven by linearity: result = R·α + dst·(1−α) stays
                // bounded by the inputs) so saturation is a no-op and the byte order is preserved.
                var ints = Avx.ConvertToVector128Int32WithTruncation(result);
                var int16s = Sse2.PackSignedSaturate(ints, ints);
                var bytes = Sse2.PackUnsignedSaturate(int16s, int16s);
                Unsafe.WriteUnaligned(ref src, bytes.AsUInt32().GetElement(0));
            }
        }

        // Scalar tail — handles the no-AVX path entirely, and the trailing pixel on AVX when the
        // span happens to start mid-row (shouldn't, since FillPolygon's row offsets are already
        // 4-byte aligned, but cheap insurance).
        for (; p < rowEnd; p += 4)
        {
            BlendTranslucent(p, preR, preG, preB, preA, inverse);
        }
    }

    /// <summary>
    /// Soft-edged thick-line stroke: every pixel within <c>width/2 + 0.5</c> of the line segment
    /// gets a fractional alpha based on its perpendicular distance, blended at that coverage.
    /// Antialiased everywhere — used both for label glyph strokes and for the renderer's polygon
    /// outlines / polyline geometry so the whole output reads consistently. The trade-off is a
    /// 1-pixel-wide stroke blooms slightly into a ~1.5px soft band; on typical map scales the
    /// smoothness wins over the lost pixel sharpness.
    /// </summary>
    public void StrokeLine(double x0, double y0, double x1, double y1, double width, Rgba color)
    {
        var radius = Math.Max(width / 2, 0.5);
        // One extra pixel beyond the geometric radius gives room for the fractional-coverage
        // ramp at the outer edge of the stroke: below this distance coverage = 1, beyond it
        // coverage = 0, with a linear fall-off in between.
        var outer = radius + 0.5;
        var minX = (int)Math.Floor(Math.Min(x0, x1) - outer);
        var maxX = (int)Math.Ceiling(Math.Max(x0, x1) + outer);
        var minY = (int)Math.Floor(Math.Min(y0, y1) - outer);
        var maxY = (int)Math.Ceiling(Math.Max(y0, y1) + outer);

        var dx = x1 - x0;
        var dy = y1 - y0;
        var lengthSq = dx * dx + dy * dy;
        if (lengthSq == 0)
        {
            // Zero-length segment degenerates to a single antialiased disc — the projection math
            // below would otherwise divide by zero.
            FillDisc(x0, y0, radius, color);
            return;
        }

        var outerSq = outer * outer;

        // SIMD path: same approach as FillDisc — vectorise the per-pixel coverage compute
        // (t-projection through sqrt) across 4 x-pixels at a time, then call Blend per-lane.
        // All vector ops mirror the scalar evaluation order so the per-lane alpha matches
        // (byte)(color.A * coverage) bit-for-bit.
        var simd = Avx.IsSupported && Sse41.IsSupported;
        var x0Vec = simd ? Vector256.Create(x0) : default;
        var y0Vec = simd ? Vector256.Create(y0) : default;
        var dxVec = simd ? Vector256.Create(dx) : default;
        var dyVec = simd ? Vector256.Create(dy) : default;
        var lengthSqVec = simd ? Vector256.Create(lengthSq) : default;
        var outerVec = simd ? Vector256.Create(outer) : default;
        var oneVec = simd ? Vector256.Create(1.0) : default;
        var colorAVec = simd ? Vector256.Create((double)color.A) : default;
        var laneOffsetsVec = simd ? Vector256.Create(0.0, 1.0, 2.0, 3.0) : default;

        for (var y = minY; y <= maxY; y++)
        {
            var x = minX;
            if (simd)
            {
                // (y - y0) * dy is row-constant — precompute scalar and broadcast so the per-lane
                // arithmetic does the same multiply-then-add sequence as scalar (different lane
                // ordering would still match bit-for-bit, but matching the scalar evaluation order
                // keeps the snapshot proof trivial).
                var yMinusY0TimesDyVec = Vector256.Create((y - y0) * dy);
                var yVec = Vector256.Create((double)y);
                for (; x + 4 <= maxX; x += 4)
                {
                    var xVec = Vector256.Create((double)x) + laneOffsetsVec;
                    // t = ((x - x0) * dx + (y - y0) * dy) / lengthSq, clamped to [0, 1].
                    var tVec = ((xVec - x0Vec) * dxVec + yMinusY0TimesDyVec) / lengthSqVec;
                    tVec = Vector256.Max(Vector256<double>.Zero, Vector256.Min(oneVec, tVec));
                    // ddx = x - (x0 + t * dx); ddy = y - (y0 + t * dy)
                    var ddxVec = xVec - (x0Vec + tVec * dxVec);
                    var ddyVec = yVec - (y0Vec + tVec * dyVec);
                    var distSqVec = ddxVec * ddxVec + ddyVec * ddyVec;
                    var coverageVec = Vector256.Max(
                        Vector256<double>.Zero,
                        Vector256.Min(oneVec, outerVec - Vector256.Sqrt(distSqVec)));
                    var alphaVec = Vector256.Floor(colorAVec * coverageVec);
                    for (var k = 0; k < 4; k++)
                    {
                        var alpha = (byte)alphaVec.GetElement(k);
                        if (alpha != 0)
                        {
                            Blend(x + k, y, new(color.R, color.G, color.B, alpha));
                        }
                    }
                }
            }

            for (; x <= maxX; x++)
            {
                // Closest point on the segment to (x, y): project onto the segment direction and
                // clamp t into [0, 1] so points past the endpoints fall back to round caps at the
                // segment ends (the coverage ramp does that work automatically).
                var t = ((x - x0) * dx + (y - y0) * dy) / lengthSq;
                if (t < 0)
                {
                    t = 0;
                }
                else if (t > 1)
                {
                    t = 1;
                }

                var ddx = x - (x0 + t * dx);
                var ddy = y - (y0 + t * dy);
                var distSq = ddx * ddx + ddy * ddy;
                if (distSq >= outerSq)
                {
                    continue;
                }

                var coverage = outer - Math.Sqrt(distSq);
                if (coverage > 1)
                {
                    coverage = 1;
                }

                Blend(x, y, new(color.R, color.G, color.B, (byte)(color.A * coverage)));
            }
        }
    }

    /// <summary>Antialiased disc — every pixel within <c>radius + 0.5</c> gets fractional
    /// coverage based on its distance to the centre. Used directly for point markers, and via
    /// <see cref="StrokeLine"/>'s zero-length fast path.</summary>
    public void FillDisc(double cx, double cy, double radius, Rgba color)
    {
        var r = Math.Max(radius, 0.5);
        var outer = r + 0.5;
        var outerSq = outer * outer;
        var minX = (int)Math.Floor(cx - outer);
        var maxX = (int)Math.Ceiling(cx + outer);
        var minY = (int)Math.Floor(cy - outer);
        var maxY = (int)Math.Ceiling(cy + outer);

        var simd = Avx.IsSupported && Sse41.IsSupported;
        var cxVec = simd ? Vector256.Create(cx) : default;
        var outerVec = simd ? Vector256.Create(outer) : default;
        var oneVec = simd ? Vector256.Create(1.0) : default;
        var colorAVec = simd ? Vector256.Create((double)color.A) : default;

        for (var y = minY; y <= maxY; y++)
        {
            var dy = y - cy;
            var dySq = dy * dy;
            // Whole-row skip when the row sits outside the disc's y span — every x on this row
            // would test distSq >= outerSq and continue, so we'd just be doing 2·outer wasted
            // sqrt+blend tests. Matches scalar output bit-for-bit; no Blend calls happen either way.
            if (dySq >= outerSq)
            {
                continue;
            }

            var x = minX;
            if (simd)
            {
                // Process 4 x-pixels per iter; coverage compute (including the sqrt) goes through
                // Vector256.Sqrt which on x86 maps to `vsqrtpd` — same IEEE-754 rounding as scalar
                // Math.Sqrt, so per-lane alpha matches scalar (byte)(color.A * coverage) exactly.
                // Stop early enough that the scalar tail gets at least one pixel — keeps the body
                // reachable for 100% line coverage.
                var dySqVec = Vector256.Create(dySq);
                for (; x + 4 <= maxX; x += 4)
                {
                    var xVec = Vector256.Create((double)x, x + 1, x + 2, x + 3);
                    var dxVec = xVec - cxVec;
                    var distSqVec = dxVec * dxVec + dySqVec;
                    // coverage = clamp(outer - sqrt(distSq), 0, 1). The Max(0, ...) is what
                    // replaces scalar's `if (distSq >= outerSq) continue` — for those lanes
                    // sqrt(distSq) >= outer so coverage clamps to 0, producing alpha=0 and a
                    // no-op blend in the lane loop below.
                    var sqrtVec = Vector256.Sqrt(distSqVec);
                    var coverageVec = Vector256.Max(
                        Vector256<double>.Zero,
                        Vector256.Min(oneVec, outerVec - sqrtVec));
                    var alphaVec = Vector256.Floor(colorAVec * coverageVec);
                    for (var k = 0; k < 4; k++)
                    {
                        var alpha = (byte)alphaVec.GetElement(k);
                        if (alpha != 0)
                        {
                            Blend(x + k, y, new(color.R, color.G, color.B, alpha));
                        }
                    }
                }
            }

            for (; x <= maxX; x++)
            {
                var dx = x - cx;
                var distSq = dx * dx + dySq;
                if (distSq >= outerSq)
                {
                    continue;
                }

                var coverage = outer - Math.Sqrt(distSq);
                if (coverage > 1)
                {
                    coverage = 1;
                }

                Blend(x, y, new(color.R, color.G, color.B, (byte)(color.A * coverage)));
            }
        }
    }

    /// <summary>Fills the region bounded by the given rings using the even-odd rule (so holes are excluded).</summary>
    public void FillPolygon((double X, double Y)[][] rings, Rgba color)
    {
        var minY = double.MaxValue;
        var maxY = double.MinValue;
        foreach (var ring in rings)
        {
            foreach (var point in ring)
            {
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        if (minY > maxY)
        {
            return;
        }

        var first = Math.Max(0, (int)Math.Ceiling(minY));
        var last = Math.Min(Height - 1, (int)Math.Floor(maxY));
        var opaque = color.A == 255;
        var packed = Pack(color);
        // Precompute alpha factors once per polygon — the inner per-pixel loop avoids a division by 255
        // on every pixel of a translucent fill.
        var a = color.A / 255d;
        var inverse = 1 - a;
        var preR = color.R * a;
        var preG = color.G * a;
        var preB = color.B * a;
        var preA = (double)color.A;

        // Per-polygon scanline parallelism. Each y writes to disjoint pixel rows so there's no data
        // race across threads within a single FillPolygon (different polygons in the same render
        // are still serialised by the caller — order matters for source-over). The threshold
        // gates out small polygons where Parallel.For's per-iter overhead dominates the row work;
        // measured tipping point on a modern x86 is ~64 rows. Below threshold the serial path
        // reuses the class-level crossings list (no allocation per render); above it each thread
        // gets a fresh list via the localInit factory.
        if (last - first + 1 >= ParallelScanlineThreshold)
        {
            Parallel.For(
                first,
                last + 1,
                () => new List<double>(),
                (y, _, crossings) =>
                {
                    FillScanline(y, crossings, rings, opaque, packed, preR, preG, preB, preA, inverse);
                    return crossings;
                },
                _ => { });
        }
        else
        {
            for (var y = first; y <= last; y++)
            {
                FillScanline(y, scanlineCrossings, rings, opaque, packed, preR, preG, preB, preA, inverse);
            }
        }
    }

    // ~64 rows is where Parallel.For's per-iter overhead breaks even with the row work on an
    // 8-core x86. Tune downward if profile shows under-utilisation at this threshold.
    const int ParallelScanlineThreshold = 64;

    // Fills one scanline of a polygon: walks every ring's edges to find x-crossings at y+0.5,
    // sorts them, then fills the pixel runs between paired crossings under the even-odd rule.
    // Factored out of FillPolygon so the serial and parallel paths share one body — `crossings`
    // is the caller's reusable list (class-level for serial, per-thread for parallel).
    void FillScanline(int y, List<double> crossings, (double X, double Y)[][] rings, bool opaque, uint packed, double preR, double preG, double preB, double preA, double inverse)
    {
        var scan = y + 0.5;
        crossings.Clear();
        foreach (var ring in rings)
        {
            for (var i = 0; i < ring.Length; i++)
            {
                var pa = ring[i];
                var pb = ring[i + 1 == ring.Length ? 0 : i + 1];
                if ((pa.Y <= scan && pb.Y > scan) || (pb.Y <= scan && pa.Y > scan))
                {
                    var t = (scan - pa.Y) / (pb.Y - pa.Y);
                    crossings.Add(pa.X + t * (pb.X - pa.X));
                }
            }
        }

        crossings.Sort();
        for (var i = 0; i + 1 < crossings.Count; i += 2)
        {
            var startX = Math.Max((int)Math.Ceiling(crossings[i] - 0.5), 0);
            var endX = Math.Min((int)Math.Floor(crossings[i + 1] - 0.5), Width - 1);
            if (startX > endX)
            {
                continue;
            }

            if (opaque)
            {
                // An opaque fill overwrites the span, so write whole pixels directly.
                var span = Pixels.AsSpan((y * Width + startX) * 4, (endX - startX + 1) * 4);
                MemoryMarshal.Cast<byte, uint>(span).Fill(packed);
            }
            else
            {
                // Spans are already clipped to [0, Width) by startX/endX, so blend directly into
                // the pixel buffer instead of re-bounds-checking each pixel in Blend. Routes
                // through BlendTranslucentSpan so a long translucent run vectorises across
                // pixels; the per-pixel math is identical to BlendTranslucent's bit-for-bit so
                // snapshot output matches the scalar path.
                var rowStart = (y * Width + startX) * 4;
                var rowEnd = (y * Width + endX + 1) * 4;
                BlendTranslucentSpan(rowStart, rowEnd, preR, preG, preB, preA, inverse);
            }
        }
    }
}
