public class PngTests
{
    static readonly byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Test]
    public async Task Renders_the_requested_size()
    {
        var png = MapRenderer.RenderPng(Sample.Polygons(), new() { Width = 200, Height = 150 });

        await Assert.That(png[..8]).IsEquivalentTo(signature);
        var (width, height, _) = Decode(png);
        await Assert.That(width).IsEqualTo(200);
        await Assert.That(height).IsEqualTo(150);
    }

    [Test]
    public async Task Draws_geometry_over_the_background()
    {
        var png = MapRenderer.RenderPng(Sample.Polygons(), new() { Width = 200, Height = 150 });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Clips_to_the_bounding_box()
    {
        // A bounding box far away from the data should leave the canvas empty.
        var options = new RenderOptions
        {
            Bounds = new Envelope(1000, 1000, 1001, 1001),
            Width = 64,
            Height = 64,
        };

        var (_, _, pixels) = Decode(MapRenderer.RenderPng(Sample.Polygons(), options));
        await Assert.That(NonBackgroundCount(pixels)).IsEqualTo(0);
    }

    [Test]
    public async Task Derives_height_from_aspect_ratio_when_unset()
    {
        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 100, 50),
            Width = 200,
        };

        var (width, height, _) = Decode(MapRenderer.RenderPng(Sample.Polygons(), options));
        await Assert.That(width).IsEqualTo(200);
        await Assert.That(height).IsEqualTo(100);
    }

    [Test]
    public async Task Empty_collection_throws()
    {
        var threw = false;
        try
        {
            MapRenderer.RenderPng(new());
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Renders_all_geometry_types()
    {
        var collection = new FeatureCollection
        {
            new Feature(new Point(1, 1)),
            new Feature(new MultiPoint([new(2, 2), new(3, 3)])),
            new Feature(new LineString([new(0, 0), new(4, 4)])),
            new Feature(new MultiLineString([new([new(0, 4), new(4, 0)])])),
            new Feature(new Polygon([[new(0, 0), new(4, 0), new(4, 4), new(0, 0)]])),
            new Feature(new MultiPolygon([new([[new(1, 1), new(2, 1), new(2, 2), new(1, 1)]])])),
            new Feature(new GeometryCollection([new Point(2, 3)])),
        };

        var png = MapRenderer.RenderPng(collection, new() { Width = 128, Height = 128 });
        await Assert.That(png[..8]).IsEquivalentTo(signature);
    }

    [Test]
    public async Task Fills_polygon_with_opaque_color()
    {
        // An opaque fill takes the fast span-fill path; the interior should be exactly the fill color.
        var collection = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
        };
        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 64,
            Height = 64,
            Fill = new(200, 50, 50),
        };

        var (width, _, pixels) = Decode(MapRenderer.RenderPng(collection, options));
        var center = (32 * width + 32) * 4;
        await Assert.That(pixels[center]).IsEqualTo((byte)200);
        await Assert.That(pixels[center + 1]).IsEqualTo((byte)50);
        await Assert.That(pixels[center + 2]).IsEqualTo((byte)50);
    }

    [Test]
    public Task Render_snapshot()
    {
        var collection = new FeatureCollection
        {
            new Feature(new Polygon(
            [
                [new(0, 0), new(10, 0), new(10, 8), new(0, 8), new(0, 0)],
                [new(2, 2), new(5, 2), new(5, 5), new(2, 5), new(2, 2)],
            ])),
            new Feature(new LineString([new(1, 1), new(9, 7), new(1, 7), new(9, 1)])),
            new Feature(new MultiPoint([new(3, 6), new(7, 3), new(5, 5)])),
        };

        var png = MapRenderer.RenderPng(
            collection,
            new() { Bounds = new Envelope(-1, -1, 11, 9), Width = 300, Height = 220 });

        return Verify(new MemoryStream(png), "png");
    }

    [Test]
    public Task Render_RealMap()
    {
        var collection = GeoConverter.Read(ProjectFiles.australian_suburbs_geojson);
        var png = MapRenderer.RenderPng(
            collection,
            new() { Width = 3000 });

        return Verify(new MemoryStream(png), "png");
    }

    [Test]
    public async Task Rejects_non_positive_width()
    {
        var threw = false;
        try
        {
            MapRenderer.RenderPng(Sample.Polygons(), new() { Width = 0 });
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    static int NonBackgroundCount(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
            {
                count++;
            }
        }

        return count;
    }

    // A small decoder for GeoConvert's own PNG output (8-bit RGBA, filter 0 rows).
    static (int Width, int Height, byte[] Rgba) Decode(byte[] data)
    {
        var position = 8;
        var width = 0;
        var height = 0;
        using var compressed = new MemoryStream();
        while (position < data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position));
            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            var contentStart = position + 8;
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(contentStart));
                height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(contentStart + 4));
            }
            else if (type == "IDAT")
            {
                compressed.Write(data, contentStart, length);
            }
            else if (type == "IEND")
            {
                break;
            }

            position = contentStart + length + 4;
        }

        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var rawBytes = raw.ToArray();

        var stride = width * 4;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(rawBytes, y * (stride + 1) + 1, rgba, y * stride, stride);
        }

        return (width, height, rgba);
    }
}
