public class DetectFormatTests
{
    [Test]
    [Arguments("a.geojson", GeoFormat.GeoJson)]
    [Arguments("a.json", GeoFormat.GeoJson)]
    [Arguments("a.topojson", GeoFormat.TopoJson)]
    [Arguments("a.shp", GeoFormat.Shapefile)]
    [Arguments("a.fgb", GeoFormat.FlatGeobuf)]
    [Arguments("a.kml", GeoFormat.Kml)]
    [Arguments("a.kmz", GeoFormat.Kmz)]
    [Arguments("a.gpx", GeoFormat.Gpx)]
    [Arguments("a.wkt", GeoFormat.Wkt)]
    [Arguments("a.wkb", GeoFormat.Wkb)]
    [Arguments("a.csv", GeoFormat.Csv)]
    [Arguments("a.parquet", GeoFormat.GeoParquet)]
    [Arguments("a.geoparquet", GeoFormat.GeoParquet)]
    [Arguments("a.png", GeoFormat.Png)]
    [Arguments("DATA.GeoJSON", GeoFormat.GeoJson)]
    public async Task Detect(string path, GeoFormat expected) =>
        await Assert.That(GeoConverter.DetectFormat(path)).IsEqualTo(expected);

    [Test]
    public async Task UnknownExtensionThrows()
    {
        var threw = false;
        try
        {
            GeoConverter.DetectFormat("data.unknown");
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
