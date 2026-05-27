using G = TestSupport;

// Defensive/error branches: writers reject an unknown geometry; readers reject malformed input.
public class DefensiveTests
{
    static FeatureCollection Bad() =>
        [new Feature(new G.BadGeometry())];

    [Test]
    public async Task Writers_reject_unknown_geometry()
    {
        await Assert.That(G.ThrowsGeo(() => GeoJson.WriteString(Bad()))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => TopoJson.WriteString(Bad()))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Wkt.WriteString(Bad()))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Wkt.Format(new G.BadGeometry()))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Wkb.ToBytes(new G.BadGeometry()))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Write(Bad(), GeoFormat.Kml))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Write(Bad(), GeoFormat.Gpx))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Write(Bad(), GeoFormat.FlatGeobuf))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Write(Bad(), GeoFormat.GeoParquet))).IsTrue();
        await Assert.That(G.ThrowsGeo(WriteBadShapefile)).IsTrue();
    }

    static void Write(FeatureCollection features, GeoFormat format)
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(features, stream, format);
    }

    static void WriteBadShapefile()
    {
        using var directory = new TempDirectory();
        Shapefile.Write(Path.Combine(directory, "bad.shp"), Bad());
    }

    [Test]
    public async Task Wkb_rejects_unknown_type()
    {
        // Byte order (little-endian) then geometry type 99.
        var bytes = new byte[] { 1, 99, 0, 0, 0 };
        await Assert.That(G.ThrowsGeo(() => Wkb.ParseGeometry(bytes))).IsTrue();
    }

    [Test]
    public async Task Shapefile_rejects_unknown_shape_type()
    {
        var data = new byte[112];
        // Record header (big-endian): record number 1, content length 2 words (4 bytes).
        data[103] = 1;
        data[107] = 2;
        // Record content: shape type 99 (little-endian).
        data[108] = 99;

        using var stream = new MemoryStream(data);
        await Assert.That(G.ThrowsGeo(() => Shapefile.Read(stream, null))).IsTrue();
    }

    [Test]
    public async Task FlatGeobuf_rejects_bad_magic()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        await Assert.That(G.ThrowsGeo(() => FlatGeobuf.Read(stream))).IsTrue();
    }

    [Test]
    public async Task GeoParquet_rejects_bad_magic()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        await Assert.That(G.ThrowsGeo(() => GeoParquet.Read(stream))).IsTrue();
    }

    [Test]
    public async Task GeoParquet_rejects_corrupt_footer()
    {
        // Valid PAR1 magic at both ends but a footer length that points outside the buffer.
        byte[] data = [0x50, 0x41, 0x52, 0x31, 0xFF, 0xFF, 0xFF, 0x7F, 0x50, 0x41, 0x52, 0x31];
        using var stream = new MemoryStream(data);
        await Assert.That(G.ThrowsGeo(() => GeoParquet.Read(stream))).IsTrue();
    }

    [Test]
    public async Task Converter_rejects_unsupported_format()
    {
        using var stream = new MemoryStream();
        await Assert.That(G.ThrowsGeo(() => GeoConverter.Read(stream, (GeoFormat)99))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => GeoConverter.Write(new(), stream, (GeoFormat)99)))
            .IsTrue();
    }

    [Test]
    public async Task GeoJson_rejects_malformed_input()
    {
        await Assert.That(G.ThrowsGeo(() => GeoJson.ReadString("{}"))).IsTrue();
        await Assert.That(G.ThrowsGeo(() =>
            GeoJson.ReadString("""{"type":"Feature","geometry":{"type":"Circle","coordinates":[1,2]}}"""))).IsTrue();
    }

    [Test]
    public async Task TopoJson_rejects_unknown_geometry()
    {
        const string topology =
            """{"type":"Topology","objects":{"d":{"type":"Circle"}},"arcs":[]}""";
        await Assert.That(G.ThrowsGeo(() => TopoJson.ReadString(topology))).IsTrue();
    }
}
