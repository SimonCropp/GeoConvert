public class WktTests
{
    [Test]
    [Arguments("POINT (1 2)")]
    [Arguments("POINT Z (1 2 3)")]
    [Arguments("POINT EMPTY")]
    [Arguments("LINESTRING (0 0, 1 1, 2 0)")]
    [Arguments("POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 2 1, 2 2, 1 1))")]
    [Arguments("MULTIPOINT ((0 0), (1 1))")]
    [Arguments("MULTILINESTRING ((0 0, 1 1), (2 2, 3 3))")]
    [Arguments("MULTIPOLYGON (((0 0, 1 0, 1 1, 0 0)), ((2 2, 3 2, 3 3, 2 2)))")]
    [Arguments("GEOMETRYCOLLECTION (POINT (1 2), LINESTRING (0 0, 1 1))")]
    public async Task RoundTrips(string wkt) =>
        await Assert.That(Wkt.Format(Wkt.ParseGeometry(wkt))).IsEqualTo(wkt);

    [Test]
    public async Task MultiPointVariantsAreEquivalent()
    {
        var bare = Wkt.ParseGeometry("MULTIPOINT (0 0, 1 1)");
        var parenthesised = Wkt.ParseGeometry("MULTIPOINT ((0 0), (1 1))");
        await Assert.That(Wkt.Format(bare)).IsEqualTo(Wkt.Format(parenthesised));
    }

    [Test]
    public async Task ParsesThreeOrdinatesAsZ()
    {
        var point = (Point)Wkt.ParseGeometry("POINT (1 2 3)");
        await Assert.That(point.Coordinate.Z).IsEqualTo(3d);
    }
}
