using G = TestSupport;

// Tests that reach into internal helpers (Tests has InternalsVisibleTo).
public class InternalTests
{
    [Test]
    public async Task FlatGeobuf_rejects_unknown_geometry_type()
    {
        byte[] magic = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00];

        var headerBuilder = new FlatBufferBuilder();
        headerBuilder.StartTable(14);
        var header = headerBuilder.FinishSizePrefixed(headerBuilder.EndTable());

        var featureBuilder = new FlatBufferBuilder();
        featureBuilder.StartTable(8);
        // geometry type field = 99
        featureBuilder.AddByte(6, 99, 0);
        var geometry = featureBuilder.EndTable();
        featureBuilder.StartTable(3);
        featureBuilder.AddOffset(0, geometry);
        var feature = featureBuilder.FinishSizePrefixed(featureBuilder.EndTable());

        byte[] bytes = [.. magic, .. header, .. feature];
        using var stream = new MemoryStream(bytes);
        await Assert.That(G.ThrowsGeo(() => FlatGeobuf.Read(stream))).IsTrue();
    }

    [Test]
    public async Task FlatGeobuf_skips_spatial_index()
    {
        byte[] magic = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00];

        var headerBuilder = new FlatBufferBuilder();
        headerBuilder.StartTable(14);
        // index_node_size = 16
        headerBuilder.AddUShort(9, 16, 0);
        // features_count = 2 (forces the index-size loop to iterate)
        headerBuilder.AddULong(8, 2, 0);
        var header = headerBuilder.FinishSizePrefixed(headerBuilder.EndTable());

        // IndexByteSize(2, 16) = 3 nodes * 40 bytes
        var index = new byte[120];

        byte[] bytes = [.. magic, .. header, .. index, .. PointFeature(1), .. PointFeature(2)];
        using var stream = new MemoryStream(bytes);
        var collection = FlatGeobuf.Read(stream);
        await Assert.That(collection.Count).IsEqualTo(2);
        await Assert.That(((Point)collection.Features[0].Geometry!).Coordinate.X).IsEqualTo(1d);
    }

    static byte[] PointFeature(double x)
    {
        var builder = new FlatBufferBuilder();
        var xy = builder.CreateDoubleVector([x, x]);
        builder.StartTable(8);
        builder.AddOffset(1, xy);
        // Point
        builder.AddByte(6, 1, 0);
        var geometry = builder.EndTable();
        builder.StartTable(3);
        builder.AddOffset(0, geometry);
        return builder.FinishSizePrefixed(builder.EndTable());
    }

    [Test]
    public async Task FlatGeobuf_column_without_name()
    {
        byte[] magic = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00];

        var builder = new FlatBufferBuilder();
        builder.StartTable(11);
        // column type Long, no name
        builder.AddByte(1, 7, 0);
        var column = builder.EndTable();
        var columns = builder.CreateOffsetVector([column]);
        builder.StartTable(14);
        builder.AddOffset(7, columns);
        var header = builder.FinishSizePrefixed(builder.EndTable());

        using var stream = new MemoryStream([.. magic, .. header]);
        await Assert.That(FlatGeobuf.Read(stream).Count).IsEqualTo(0);
    }

    [Test]
    public async Task FlatGeobuf_handles_large_geometry()
    {
        // A long line forces the FlatBuffers builder to grow its backing buffer.
        var points = new List<Position>();
        for (var i = 0; i < 5000; i++)
        {
            points.Add(new(i * 0.001, i * 0.002));
        }

        var source = new FeatureCollection { new Feature(new LineString(points)) };
        var result = G.RoundtripStream(source, GeoFormat.FlatGeobuf);
        await Assert.That(((LineString)result.Features[0].Geometry!).Positions.Count).IsEqualTo(5000);
    }

    [Test]
    public async Task Scalars_format()
    {
        await Assert.That(Scalars.Format(null)).IsEqualTo("");
        await Assert.That(Scalars.Format(true)).IsEqualTo("true");
        await Assert.That(Scalars.Format(false)).IsEqualTo("false");
        await Assert.That(Scalars.Format(1.5d)).IsEqualTo("1.5");
        await Assert.That(Scalars.Format(1.5f)).IsEqualTo("1.5");
        await Assert.That(Scalars.Format(42L)).IsEqualTo("42");
        await Assert.That(Scalars.Format("text")).IsEqualTo("text");
    }

    [Test]
    public async Task Scalars_infer()
    {
        await Assert.That(Scalars.Infer(null)).IsNull();
        await Assert.That(Scalars.Infer("")).IsEqualTo("");
        await Assert.That(Scalars.Infer("42")).IsEqualTo(42L);
        await Assert.That(Scalars.Infer("1.5")).IsEqualTo(1.5d);
        await Assert.That((bool)Scalars.Infer("true")!).IsTrue();
        await Assert.That((bool)Scalars.Infer("false")!).IsFalse();
        await Assert.That(Scalars.Infer("abc")).IsEqualTo("abc");
    }

    [Test]
    public async Task Ring_orientation()
    {
        // Counter-clockwise square (positive area).
        IReadOnlyList<Position> ccw = [new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(0, 0)];
        await Assert.That(Ring.IsClockwise(ccw)).IsFalse();
        await Assert.That(Ring.IsClockwise(Ring.Orient(ccw, clockwise: true))).IsTrue();
        // degenerate
        await Assert.That(Ring.SignedArea([new(0, 0), new(1, 1)])).IsEqualTo(0d);
    }
}
