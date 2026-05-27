using System.Runtime.InteropServices;

/// <summary>A software RGBA raster with source-over blending and basic line/disc/polygon fills.</summary>
sealed class Canvas
{
    readonly byte[] pixels;
    // Reused across FillPolygon calls so a render with hundreds of polygons doesn't allocate a fresh
    // crossings list per call.
    readonly List<double> scanlineCrossings = [];

    public Canvas(int width, int height, Rgba background)
    {
        Width = width;
        Height = height;
        pixels = new byte[width * height * 4];
        // Fill the background a pixel (uint) at a time rather than byte-by-byte.
        MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()).Fill(Pack(background));
    }

    // Packs RGBA into a uint so that reinterpreting the pixel buffer as uints yields the R,G,B,A byte order.
    static uint Pack(Rgba color) =>
        BitConverter.IsLittleEndian
            ? (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24))
            : (uint)((color.R << 24) | (color.G << 16) | (color.B << 8) | color.A);

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels => pixels;

    public void Blend(int x, int y, Rgba color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || color.A == 0)
        {
            return;
        }

        var i = (y * Width + x) * 4;
        if (color.A == 255)
        {
            pixels[i] = color.R;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.B;
            pixels[i + 3] = 255;
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
        pixels[i] = (byte)(preR + pixels[i] * inverse);
        pixels[i + 1] = (byte)(preG + pixels[i + 1] * inverse);
        pixels[i + 2] = (byte)(preB + pixels[i + 2] * inverse);
        pixels[i + 3] = (byte)(aByte + pixels[i + 3] * inverse);
    }

    public void FillDisc(double cx, double cy, double radius, Rgba color)
    {
        var r = Math.Max(radius, 0.5);
        var minX = (int)Math.Floor(cx - r);
        var maxX = (int)Math.Ceiling(cx + r);
        var minY = (int)Math.Floor(cy - r);
        var maxY = (int)Math.Ceiling(cy + r);
        var r2 = r * r;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy <= r2)
                {
                    Blend(x, y, color);
                }
            }
        }
    }

    public void StrokeLine(double x0, double y0, double x1, double y1, double width, Rgba color)
    {
        var radius = Math.Max(width / 2, 0.5);
        var distance = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        var steps = Math.Max(1, (int)Math.Ceiling(distance));
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            FillDisc(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, radius, color);
        }
    }

    /// <summary>
    /// Soft-edged thick-line stroke: every pixel within <c>width/2 + 0.5</c> of the line segment
    /// gets a fractional alpha based on its perpendicular distance, blended at that coverage.
    /// Used by <see cref="StrokeFont"/> so labels stay readable at small cap heights (14px etc.)
    /// where the 1-pixel-wide jagged strokes from <see cref="StrokeLine"/> read as pixelated.
    /// Bounded geometry rendering still uses the crisp binary <see cref="StrokeLine"/> — sharp
    /// coastlines and polygon edges read better than antialiased ones at typical map scales.
    /// </summary>
    public void StrokeLineAntialiased(double x0, double y0, double x1, double y1, double width, Rgba color)
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
            FillDiscAntialiased(x0, y0, radius, color);
            return;
        }

        var outerSq = outer * outer;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
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
    /// coverage based on its distance to the centre. Used by
    /// <see cref="StrokeLineAntialiased"/> for the zero-length segment fast path.</summary>
    public void FillDiscAntialiased(double cx, double cy, double radius, Rgba color)
    {
        var r = Math.Max(radius, 0.5);
        var outer = r + 0.5;
        var outerSq = outer * outer;
        var minX = (int)Math.Floor(cx - outer);
        var maxX = (int)Math.Ceiling(cx + outer);
        var minY = (int)Math.Floor(cy - outer);
        var maxY = (int)Math.Ceiling(cy + outer);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var distSq = dx * dx + dy * dy;
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
        for (var y = first; y <= last; y++)
        {
            var scan = y + 0.5;
            scanlineCrossings.Clear();
            foreach (var ring in rings)
            {
                for (var i = 0; i < ring.Length; i++)
                {
                    var pa = ring[i];
                    var pb = ring[i + 1 == ring.Length ? 0 : i + 1];
                    if ((pa.Y <= scan && pb.Y > scan) || (pb.Y <= scan && pa.Y > scan))
                    {
                        var t = (scan - pa.Y) / (pb.Y - pa.Y);
                        scanlineCrossings.Add(pa.X + t * (pb.X - pa.X));
                    }
                }
            }

            scanlineCrossings.Sort();
            for (var i = 0; i + 1 < scanlineCrossings.Count; i += 2)
            {
                var startX = Math.Max((int)Math.Ceiling(scanlineCrossings[i] - 0.5), 0);
                var endX = Math.Min((int)Math.Floor(scanlineCrossings[i + 1] - 0.5), Width - 1);
                if (startX > endX)
                {
                    continue;
                }

                if (opaque)
                {
                    // An opaque fill overwrites the span, so write whole pixels directly.
                    var span = pixels.AsSpan((y * Width + startX) * 4, (endX - startX + 1) * 4);
                    MemoryMarshal.Cast<byte, uint>(span).Fill(packed);
                }
                else
                {
                    // Spans are already clipped to [0, Width) by startX/endX, so blend directly into
                    // the pixel buffer instead of re-bounds-checking each pixel in Blend. Goes through
                    // BlendTranslucent for the per-pixel math so the formula stays in one place.
                    var rowStart = (y * Width + startX) * 4;
                    var rowEnd = (y * Width + endX + 1) * 4;
                    for (var p = rowStart; p < rowEnd; p += 4)
                    {
                        BlendTranslucent(p, preR, preG, preB, preA, inverse);
                    }
                }
            }
        }
    }
}
