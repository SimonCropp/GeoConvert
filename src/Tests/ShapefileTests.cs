public class ShapefileTests
{
    [Test]
    public async Task Point_shapefile()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(1, 2)),
            new Feature(new Point(3, 4))
        };
        var back = TestSupport.RoundtripShapefile(source);
        await Assert.That(back.Features[1].Geometry).IsTypeOf<Point>();
    }

    [Test]
    public async Task MultiPoint_shapefile()
    {
        var source = new FeatureCollection
        {
            new Feature(new MultiPoint([new(1, 2), new(3, 4)]))
        };
        var back = TestSupport.RoundtripShapefile(source);
        await Assert.That(back.Features[0].Geometry).IsTypeOf<MultiPoint>();
    }

    [Test]
    public async Task Polyline_shapefile()
    {
        var source = new FeatureCollection
        {
            new Feature(new LineString([new(0, 0), new(1, 1)])),
            new Feature(new MultiLineString([new([new(2, 2), new(3, 3)]), new([new(4, 4), new(5, 5)])])),
        };
        var back = TestSupport.RoundtripShapefile(source);
        await Assert.That(back.Features[0].Geometry).IsTypeOf<LineString>();
        await Assert.That(back.Features[1].Geometry).IsTypeOf<MultiLineString>();
    }

    [Test]
    public async Task Reads_without_dbf()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "d.shp");
        Shapefile.Write(path, [new Feature(new Point(1, 2))]);
        File.Delete(Path.ChangeExtension(path, ".dbf"));
        var back = Shapefile.Read(path);
        await Assert.That(back.Features[0].Properties.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Attribute_types_round_trip()
    {
        var a = new Feature(new Point(0, 0))
        {
            Properties =
            {
                ["flag"] = true,
                ["count"] = 3L,
                ["ratio"] = 2.5d,
                ["label"] = "x"
            }
        };

        var b = new Feature(new Point(1, 1))
        {
            Properties =
            {
                ["flag"] = false,
                ["count"] = null,
                ["ratio"] = null,
                ["label"] = null
            }
        };

        // no flag => logical '?'
        var c = new Feature(new Point(2, 2));

        var back = TestSupport.RoundtripShapefile([a, b, c]);

        await Assert.That((bool)back.Features[0].Properties["flag"]!).IsTrue();
        await Assert.That(back.Features[0].Properties["count"]).IsEqualTo(3L);
        await Assert.That(back.Features[0].Properties["ratio"]).IsEqualTo(2.5d);
        await Assert.That(back.Features[0].Properties["label"]).IsEqualTo("x");
        await Assert.That((bool)back.Features[1].Properties["flag"]!).IsFalse();
        await Assert.That(back.Features[1].Properties["count"]).IsNull();
        await Assert.That(back.Features[2].Properties["flag"]).IsNull();
    }

    [Test]
    public async Task Null_and_empty_geometry()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(1, 2)), new Feature()
        };
        var back = TestSupport.RoundtripShapefile(source);
        await Assert.That(back.Features[1].Geometry).IsNull();
    }

    [Test]
    public async Task All_null_geometry_collection()
    {
        var source = new FeatureCollection
        {
            new Feature(), new Feature()
        };
        var back = TestSupport.RoundtripShapefile(source);
        await Assert.That(back.Count).IsEqualTo(2);
        await Assert.That(back.Features[0].Geometry).IsNull();
    }

    [Test]
    public async Task Mixed_geometry_categories_throw()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(0, 0)),
            new Feature(new LineString([new(0, 0), new(1, 1)])),
        };
        await Assert.That(TestSupport.ThrowsGeo(() => TestSupport.RoundtripShapefile(source))).IsTrue();
    }
}
