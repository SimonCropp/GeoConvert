// Reverses PNG row filters for the test decoders. The two test Decode helpers (PngTests,
// LabelTests) share this rather than each implementing the five-filter reconstruction inline.
// Production code never uses this — the encoder in Png.cs is write-only; if read-back becomes a
// supported scenario we'd want a hardened decoder instead.
static class PngDecoder
{
    const int bpp = 4;

    // Given the inflated IDAT stream (filter-byte-prefixed rows) and the image dimensions,
    // reverses each row's filter and produces a flat width*height*bpp RGBA byte buffer.
    public static byte[] Reconstruct(byte[] rawBytes, int width, int height)
    {
        var stride = width * bpp;
        var rgba = new byte[width * height * bpp];

        for (var y = 0; y < height; y++)
        {
            var filterType = rawBytes[y * (stride + 1)];
            var filteredStart = y * (stride + 1) + 1;
            var outStart = y * stride;
            var prevOutStart = (y - 1) * stride;

            switch (filterType)
            {
                case 0:
                    // None: pass-through copy.
                    Array.Copy(rawBytes, filteredStart, rgba, outStart, stride);
                    break;
                case 1:
                    // Sub: add left neighbour (byte bpp positions earlier in this row).
                    for (var i = 0; i < bpp; i++)
                    {
                        rgba[outStart + i] = rawBytes[filteredStart + i];
                    }

                    for (var i = bpp; i < stride; i++)
                    {
                        rgba[outStart + i] = (byte)(rawBytes[filteredStart + i] + rgba[outStart + i - bpp]);
                    }

                    break;
                case 2:
                    // Up: add same-column byte from previous row (zeros for first row).
                    for (var i = 0; i < stride; i++)
                    {
                        var above = y > 0 ? rgba[prevOutStart + i] : 0;
                        rgba[outStart + i] = (byte)(rawBytes[filteredStart + i] + above);
                    }

                    break;
                case 3:
                    // Average: add floor((left + above) / 2). Out-of-bounds neighbours treated as 0.
                    for (var i = 0; i < bpp; i++)
                    {
                        var above = y > 0 ? rgba[prevOutStart + i] : 0;
                        rgba[outStart + i] = (byte)(rawBytes[filteredStart + i] + above / 2);
                    }

                    for (var i = bpp; i < stride; i++)
                    {
                        int left = rgba[outStart + i - bpp];
                        var above = y > 0 ? rgba[prevOutStart + i] : 0;
                        rgba[outStart + i] = (byte)(rawBytes[filteredStart + i] + (left + above) / 2);
                    }

                    break;
                case 4:
                    // Paeth: add PaethPredictor(left, above, upper-left). Out-of-bounds neighbours
                    // treated as 0. Reconstruction is the inverse of the encoder's PaethPredictor
                    // call in Png.cs.
                    for (var i = 0; i < stride; i++)
                    {
                        var left = i >= bpp ? rgba[outStart + i - bpp] : 0;
                        var above = y > 0 ? rgba[prevOutStart + i] : 0;
                        var upperLeft = y > 0 && i >= bpp ? rgba[prevOutStart + i - bpp] : 0;
                        rgba[outStart + i] = (byte)(rawBytes[filteredStart + i] + PaethPredictor(left, above, upperLeft));
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unsupported PNG filter type {filterType}.");
            }
        }

        return rgba;
    }

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
}
