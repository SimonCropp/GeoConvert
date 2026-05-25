public class JsonFormatTests
{
    [Test]
    public async Task GeoJson_reads_feature_root()
    {
        var collection = GeoJson.ReadString(
            """{"type":"Feature","id":7,"geometry":{"type":"Point","coordinates":[1,2]},"properties":{"n":"a"}}""");
        await Assert.That(collection.Count).IsEqualTo(1);
        await Assert.That(collection.Features[0].Id).IsEqualTo(7L);
        await Assert.That(collection.Features[0].Properties["n"]).IsEqualTo("a");
    }

    [Test]
    public async Task GeoJson_reads_bare_geometry_root()
    {
        var collection = GeoJson.ReadString("""{"type":"Point","coordinates":[3,4]}""");
        await Assert.That(collection.Features[0].Geometry).IsTypeOf<Point>();
    }

    [Test]
    public async Task GeoJson_reads_string_id_and_null_geometry()
    {
        var collection = GeoJson.ReadString(
            """{"type":"FeatureCollection","features":[{"type":"Feature","id":"x","geometry":null,"properties":{}}]}""");
        await Assert.That(collection.Features[0].Id).IsEqualTo("x");
        await Assert.That(collection.Features[0].Geometry).IsNull();
    }

    [Test]
    public async Task GeoJson_writes_id_and_null_geometry()
    {
        var feature = new Feature {
            Id = 5L,
            Properties =
            {
                ["k"] = "v"
            }
        };
        var json = GeoJson.WriteString([feature]);
        await Assert.That(json).Contains("\"id\": 5");
        await Assert.That(json).Contains("\"geometry\": null");

        var back = GeoJson.ReadString(json);
        await Assert.That(back.Features[0].Id).IsEqualTo(5L);
    }

    [Test]
    public async Task GeoJson_preserves_property_types()
    {
        var feature = new Feature(new Point(0, 0))
        {
            Properties =
            {
                ["s"] = "text",
                ["i"] = 7L,
                ["d"] = 2.5d,
                ["b"] = true,
                ["b2"] = false,
                ["nothing"] = null
            }
        };
        var back = GeoJson.ReadString(GeoJson.WriteString([feature])).Features[0];
        await Assert.That(back.Properties["s"]).IsEqualTo("text");
        await Assert.That(back.Properties["i"]).IsEqualTo(7L);
        await Assert.That(back.Properties["d"]).IsEqualTo(2.5d);
        await Assert.That((bool)back.Properties["b"]!).IsTrue();
        await Assert.That((bool)back.Properties["b2"]!).IsFalse();
        await Assert.That(back.Properties["nothing"]).IsNull();
    }

    [Test]
    public async Task GeoJson_flattens_nested_property_to_json()
    {
        var collection = GeoJson.ReadString(
            """{"type":"Feature","geometry":null,"properties":{"obj":{"a":1},"arr":[1,2]}}""");
        await Assert.That(collection.Features[0].Properties["obj"]).IsTypeOf<string>();
        await Assert.That(collection.Features[0].Properties["arr"]).IsTypeOf<string>();
    }

    [Test]
    public async Task GeoJson_writes_non_scalar_property_as_string()
    {
        var feature = new Feature(new Point(0, 0))
        {
            Properties =
            {
                ["when"] = new DateTime(2020, 1, 1)
            }
        };
        var json = GeoJson.WriteString([feature]);
        await Assert.That(json).Contains("when");
    }

    [Test]
    public async Task TopoJson_decodes_transform_and_arcs()
    {
        const string topology =
            """
            {"type":"Topology",
             "transform":{"scale":[2,2],"translate":[10,20]},
             "objects":{"d":{"type":"GeometryCollection","geometries":[
                {"type":"Point","coordinates":[0,0]},
                {"type":"LineString","arcs":[0]}]}},
             "arcs":[[[0,0],[1,1],[1,1]]]}
            """;
        var collection = TopoJson.ReadString(topology);
        await Assert.That(collection.Count).IsEqualTo(2);
        var point = (Point)collection.Features[0].Geometry!;
        await Assert.That(point.Coordinate.X).IsEqualTo(10d);
        await Assert.That(point.Coordinate.Y).IsEqualTo(20d);
        var line = (LineString)collection.Features[1].Geometry!;
        await Assert.That(line.Positions.Count).IsEqualTo(3);
        await Assert.That(line.Positions[2].X).IsEqualTo(14d);
    }

    [Test]
    public async Task TopoJson_writes_id_and_null_geometry()
    {
        var feature = new Feature { Id = "z" };
        var json = TopoJson.WriteString([feature]);
        await Assert.That(json).Contains("\"id\": \"z\"");
        await Assert.That(json).Contains("null");
    }
}
