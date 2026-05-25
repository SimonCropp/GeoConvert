public class TextFormatTests
{
    [Test]
    [Arguments("POINT EMPTY")]
    [Arguments("LINESTRING EMPTY")]
    [Arguments("POLYGON EMPTY")]
    [Arguments("MULTIPOINT EMPTY")]
    [Arguments("MULTILINESTRING EMPTY")]
    [Arguments("MULTIPOLYGON EMPTY")]
    [Arguments("GEOMETRYCOLLECTION EMPTY")]
    [Arguments("POINT M (1 2 3)")]
    [Arguments("POINT ZM (1 2 3 4)")]
    [Arguments("LINESTRING ZM (1 2 3 4, 5 6 7 8)")]
    public async Task Wkt_round_trips(string wkt) =>
        await Assert.That(Wkt.Format(Wkt.ParseGeometry(wkt))).IsEqualTo(wkt);

    [Test]
    [Arguments("POINT (1 x)")]
    [Arguments("POINT (1 2) extra")]
    [Arguments("FOO (1 2)")]
    [Arguments("FOO EMPTY")]
    [Arguments("(1 2)")]
    [Arguments("POINT 1 2)")]
    [Arguments("POINT (1 2e)")]
    [Arguments("POINT (1 2")]
    public async Task Wkt_rejects_invalid(string wkt) =>
        await Assert.That(TestSupport.ThrowsGeo(() => Wkt.ParseGeometry(wkt))).IsTrue();

    [Test]
    public async Task Wkt_parses_four_untagged_ordinates()
    {
        var point = (Point)Wkt.ParseGeometry("POINT (1 2 3 4)");
        await Assert.That(point.Coordinate.Z).IsEqualTo(3d);
        await Assert.That(point.Coordinate.M).IsEqualTo(4d);
    }

    [Test]
    public async Task Csv_reads_lon_lat_columns()
    {
        var collection = Csv.ReadString("lon,lat,name\n1.5,2.5,A\n3,4,B\n");
        await Assert.That(collection.Count).IsEqualTo(2);
        var point = (Point)collection.Features[0].Geometry!;
        await Assert.That(point.Coordinate.X).IsEqualTo(1.5d);
        await Assert.That(collection.Features[0].Properties["name"]).IsEqualTo("A");
    }

    [Test]
    public async Task Csv_without_geometry_column()
    {
        var collection = Csv.ReadString("name,pop\nA,10\n");
        await Assert.That(collection.Features[0].Geometry).IsNull();
        await Assert.That(collection.Features[0].Properties["pop"]).IsEqualTo(10L);
    }

    [Test]
    public async Task Csv_empty_input() =>
        await Assert.That(Csv.ReadString("").Count).IsEqualTo(0);

    [Test]
    public async Task Csv_quotes_round_trip()
    {
        var feature = new Feature(new Point(1, 2))
        {
            Properties =
            {
                ["note"] = "a,b\"c\nd"
            }
        };
        var text = Csv.WriteString([feature]);
        var back = Csv.ReadString(text).Features[0];
        await Assert.That(back.Properties["note"]).IsEqualTo("a,b\"c\nd");
    }

    [Test]
    public async Task Csv_geometry_header_alias() =>
        await Assert.That(Csv.ReadString("geometry,name\n\"POINT (1 2)\",A\n").Features[0].Geometry)
            .IsTypeOf<Point>();

    [Test]
    public async Task Wkb_reads_extended_and_big_endian()
    {
        // EWKB little-endian POINT Z (1 2 3): type 0x80000001.
        byte[] ewkbZ =
        [
            1, 0x01, 0x00, 0x00, 0x80,
            .. BitConverter.GetBytes(1d), .. BitConverter.GetBytes(2d), .. BitConverter.GetBytes(3d),
        ];
        var point = (Point)Wkb.ParseGeometry(ewkbZ);
        await Assert.That(point.Coordinate.Z).IsEqualTo(3d);

        // EWKB with SRID flag (0x20000001): skip the 4-byte SRID then x,y.
        byte[] ewkbSrid =
        [
            1, 0x01, 0x00, 0x00, 0x20, 0xE6, 0x10, 0x00, 0x00,
            .. BitConverter.GetBytes(5d), .. BitConverter.GetBytes(6d),
        ];
        var srid = (Point)Wkb.ParseGeometry(ewkbSrid);
        await Assert.That(srid.Coordinate.X).IsEqualTo(5d);

        // Big-endian (XDR) POINT (1 2).
        byte[] bigEndian =
        [
            0, 0x00, 0x00, 0x00, 0x01,
            .. Reverse(BitConverter.GetBytes(1d)), .. Reverse(BitConverter.GetBytes(2d)),
        ];
        var be = (Point)Wkb.ParseGeometry(bigEndian);
        await Assert.That(be.Coordinate.Y).IsEqualTo(2d);
    }

    [Test]
    public async Task Wkb_round_trips_measure()
    {
        var source = new FeatureCollection { new Feature(new Point(new(1, 2, 3, 4))) };
        var back = (Point)TestSupport.RoundtripStream(source, GeoFormat.Wkb).Features[0].Geometry!;
        await Assert.That(back.Coordinate.M).IsEqualTo(4d);
    }

    [Test]
    public async Task Wkb_multipoint_preserves_z_and_m()
    {
        // Members carry Z-only, M-only and ZM ordinates, exercising each per-point dimension tag.
        var source = new FeatureCollection
        {
            new Feature(new MultiPoint([new(1, 2, 3), new(4, 5, null, 6), new(7, 8, 9, 10)])),
        };
        var back = (MultiPoint)TestSupport.RoundtripStream(source, GeoFormat.Wkb).Features[0].Geometry!;
        await Assert.That(back.Positions[0].Z).IsEqualTo(3d);
        await Assert.That(back.Positions[1].M).IsEqualTo(6d);
        await Assert.That(back.Positions[2].Z).IsEqualTo(9d);
        await Assert.That(back.Positions[2].M).IsEqualTo(10d);
    }

    static byte[] Reverse(byte[] bytes)
    {
        Array.Reverse(bytes);
        return bytes;
    }
}
