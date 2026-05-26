// Each format is written then read back; the recovered collection is rendered to GeoJSON and verified,
// so the snapshot shows exactly what survives a round trip through that format.
public class RoundTripTests
{
    [Test]
    public Task Roundtrip_geojson() =>
        VerifyRoundTrip(GeoFormat.GeoJson, Sample.Mixed());

    [Test]
    public Task Roundtrip_topojson() =>
        VerifyRoundTrip(GeoFormat.TopoJson, Sample.Mixed());

    [Test]
    public Task Roundtrip_kml() =>
        VerifyRoundTrip(GeoFormat.Kml, Sample.Mixed());

    [Test]
    public Task Roundtrip_kmz() =>
        VerifyRoundTrip(GeoFormat.Kmz, Sample.Mixed());

    [Test]
    public Task Roundtrip_wkt() =>
        VerifyRoundTrip(GeoFormat.Wkt, Sample.Mixed());

    [Test]
    public Task Roundtrip_wkb() =>
        VerifyRoundTrip(GeoFormat.Wkb, Sample.Mixed());

    [Test]
    public Task Roundtrip_csv() =>
        VerifyRoundTrip(GeoFormat.Csv, Sample.Mixed());

    [Test]
    public Task Roundtrip_geoparquet() =>
        VerifyRoundTrip(GeoFormat.GeoParquet, Sample.Mixed());

    [Test]
    public Task Roundtrip_flatgeobuf() =>
        VerifyRoundTrip(GeoFormat.FlatGeobuf, Sample.Mixed());

    [Test]
    public Task Roundtrip_gpx() =>
        VerifyRoundTrip(GeoFormat.Gpx, Sample.Points());

    [Test]
    public Task Roundtrip_shapefile() =>
        VerifyRoundTrip(GeoFormat.Shapefile, Sample.Polygons());

    // Layer-aware round trips: snapshot the tree (not just flattened features) to prove hierarchy survives.
    [Test]
    public Task Roundtrip_kml_layered() =>
        VerifyLayerRoundTrip(GeoFormat.Kml, Sample.Layered());

    [Test]
    public Task Roundtrip_topojson_layered() =>
        VerifyLayerRoundTrip(GeoFormat.TopoJson, Sample.Layered());

    [Test]
    public Task Roundtrip_kmz_layered() =>
        VerifyLayerRoundTrip(GeoFormat.Kmz, Sample.Layered());

    [Test]
    public Task Roundtrip_gpx_layered() =>
        VerifyLayerRoundTrip(GeoFormat.Gpx, Sample.GpxLayered());

    [Test]
    public async Task Roundtrip_shapefile_directory()
    {
        using var directory = new TempDirectory();
        Shapefile.Write(directory + Path.DirectorySeparatorChar, Sample.ShapefileBundle());
        var back = Shapefile.Read(directory);
        await Verify(TestSupport.AsLayerJson(back));
    }

    static Task VerifyLayerRoundTrip(GeoFormat format, FeatureCollection source) =>
        Verify(TestSupport.AsLayerJson(RoundTrip(format, source)));

    static Task VerifyRoundTrip(GeoFormat format, FeatureCollection source) =>
        Verify(GeoJson.WriteString(RoundTrip(format, source)));

    static FeatureCollection RoundTrip(GeoFormat format, FeatureCollection source)
    {
        if (format == GeoFormat.Shapefile)
        {
            using var directory = new TempDirectory();
            var path = Path.Combine(directory, "data.shp");
            Shapefile.Write(path, source);
            return Shapefile.Read(path);
        }

        using var stream = new MemoryStream();
        GeoConverter.Write(source, stream, format);
        stream.Position = 0;
        return GeoConverter.Read(stream, format);
    }
}
