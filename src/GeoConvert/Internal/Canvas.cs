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
        var crossings = scanlineCrossings;
        for (var y = first; y <= last; y++)
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
