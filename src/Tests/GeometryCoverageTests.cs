// Round-trips every geometry type through each format and checks the type sequence survives, exercising
// every per-geometry-type read and write branch.
public class GeometryCoverageTests
{
    static FeatureCollection AllTypes(bool includeCollection)
    {
        var collection = new FeatureCollection
        {
            new Feature(new Point(new(1, 2, 3))),
            new Feature(new LineString([new(0, 0), new(1, 1), new(2, 0)])),
            new Feature(new Polygon(
            [
                [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
                [new(1, 1), new(2, 1), new(2, 2), new(1, 1)],
            ])),
            new Feature(new MultiPoint([new(0, 0), new(1, 1)])),
            new Feature(new MultiLineString([new([new(0, 0), new(1, 1)]), new([new(2, 2), new(3, 3)])])),
            new Feature(new MultiPolygon(
            [
                new([[new(0, 0), new(1, 0), new(1, 1), new(0, 0)]]),
                new([[new(5, 5), new(6, 5), new(6, 6), new(5, 5)]]),
            ])),
        };

        if (includeCollection)
        {
            collection.Add(new GeometryCollection([new Point(7, 8), new LineString([new(0, 0), new(1, 1)])]));
        }

        return collection;
    }

    [Test]
    [Arguments(GeoFormat.GeoJson)]
    [Arguments(GeoFormat.Wkt)]
    [Arguments(GeoFormat.Wkb)]
    [Arguments(GeoFormat.Kml)]
    [Arguments(GeoFormat.FlatGeobuf)]
    public async Task Preserves_all_geometry_types(GeoFormat format)
    {
        var source = AllTypes(includeCollection: true);
        var result = TestSupport.RoundtripStream(source, format);
        await Assert.That(TestSupport.Types(result)).IsEquivalentTo(TestSupport.Types(source));
    }

    [Test]
    public async Task TopoJson_preserves_simple_types()
    {
        // TopoJSON geometry objects do not nest GeometryCollections.
        var source = AllTypes(includeCollection: false);
        var result = TestSupport.RoundtripStream(source, GeoFormat.TopoJson);
        await Assert.That(TestSupport.Types(result)).IsEquivalentTo(TestSupport.Types(source));
    }

    [Test]
    public async Task Wkt_preserves_z_ordinate()
    {
        var result = TestSupport.RoundtripStream(
            [new Feature(new Point(new(1, 2, 3)))],
            GeoFormat.Wkt);
        await Assert.That(((Point)result.Features[0].Geometry!).Coordinate.Z).IsEqualTo(3d);
    }
}
