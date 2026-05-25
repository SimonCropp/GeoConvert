using System.Runtime.InteropServices;

/// <summary>A software RGBA raster with source-over blending and basic line/disc/polygon fills.</summary>
sealed class Canvas
{
    readonly byte[] pixels;

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
        var inverse = 1 - a;
        pixels[i] = (byte)(color.R * a + pixels[i] * inverse);
        pixels[i + 1] = (byte)(color.G * a + pixels[i + 1] * inverse);
        pixels[i + 2] = (byte)(color.B * a + pixels[i + 2] * inverse);
        pixels[i + 3] = (byte)(color.A + pixels[i + 3] * inverse);
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
        var crossings = new List<double>();
        for (var y = first; y <= last; y++)
        {
            var scan = y + 0.5;
            crossings.Clear();
            foreach (var ring in rings)
            {
                for (var i = 0; i < ring.Length; i++)
                {
                    var a = ring[i];
                    var b = ring[i + 1 == ring.Length ? 0 : i + 1];
                    if ((a.Y <= scan && b.Y > scan) || (b.Y <= scan && a.Y > scan))
                    {
                        var t = (scan - a.Y) / (b.Y - a.Y);
                        crossings.Add(a.X + t * (b.X - a.X));
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
                    for (var x = startX; x <= endX; x++)
                    {
                        Blend(x, y, color);
                    }
                }
            }
        }
    }
}
