using G = TestSupport;

// Targets the last few uncovered branches across the codebase.
public class CoverageMopUpTests
{
    [Test]
    public async Task FeatureCollection_non_generic_enumerator()
    {
        var enumerator = ((IEnumerable)Sample.Mixed()).GetEnumerator();
        await Assert.That(enumerator.MoveNext()).IsTrue();
    }

    [Test]
    public async Task Converter_shapefile_stream_throws()
    {
        using var stream = new MemoryStream();
        await Assert.That(G.ThrowsGeo(() => GeoConverter.Read(stream, GeoFormat.Shapefile))).IsTrue();
        await Assert.That(G.ThrowsGeo(() =>
            GeoConverter.Write(new(), stream, GeoFormat.Shapefile))).IsTrue();
    }

    [Test]
    public async Task Wkb_to_bytes_and_measure_only()
    {
        await Assert.That(Wkb.ToBytes(new Point(1, 2)).Length).IsGreaterThan(0);

        var source = new FeatureCollection
        {
            new Feature(new Point(new(1, 2, null, 5)))
        };
        var back = (Point)G.RoundtripStream(source, GeoFormat.Wkb).Features[0].Geometry!;
        await Assert.That(back.Coordinate.M).IsEqualTo(5d);
        await Assert.That(back.Coordinate.Z).IsNull();
    }

    [Test]
    public async Task TopoJson_reads_id_and_reversed_arc()
    {
        const string topology =
            """
            {"type":"Topology","objects":{"d":{"type":"GeometryCollection","geometries":[
              {"type":"LineString","arcs":[-1],"id":9}]}},
             "arcs":[[[0,0],[1,1]]]}
            """;
        var feature = TopoJson.ReadString(topology).Features[0];
        await Assert.That((long)feature.Id!).IsEqualTo(9L);
        var line = (LineString)feature.Geometry!;
        // arc reversed
        await Assert.That(line.Positions[0].X).IsEqualTo(1d);
    }

    [Test]
    public async Task Kml_placemark_without_geometry()
    {
        using var stream = new MemoryStream("""<kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><name>x</name></Placemark></kml>"""u8.ToArray());
        await Assert.That(Kml.Read(stream).Features[0].Geometry).IsNull();
    }

    [Test]
    public async Task Gpx_writes_multiline_null_and_description()
    {
        var line = new Feature(
            new MultiLineString(
            [
                new([new(0, 0), new(1, 1)]), new([new(2, 2), new(3, 3)])
            ]))
        {
            Properties =
            {
                ["name"] = "t",
                ["description"] = "d"
            }
        };

        var back = G.RoundtripStream(
        [
            line,
            new Feature()
        ],
        GeoFormat.Gpx);
        await Assert.That(back.Count).IsEqualTo(1);
        await Assert.That(back.Features[0].Geometry).IsTypeOf<MultiLineString>();
        await Assert.That(back.Features[0].Properties["description"]).IsEqualTo("d");
    }

    [Test]
    public async Task Csv_handles_crlf_and_missing_trailing_newline()
    {
        await Assert.That(Csv.ReadString("a,b\r\n1,2\r\n").Count).IsEqualTo(1);
        await Assert.That(Csv.ReadString("a,b\n1,2").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Shapefile_truncated_record_is_ignored()
    {
        var data = new byte[108];
        // record number 1
        data[103] = 1;
        // claims 100 words (200 bytes) but none follow
        data[107] = 100;
        using var stream = new MemoryStream(data);
        await Assert.That(Shapefile.Read(stream, null).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Shapefile_truncates_oversized_numeric()
    {
        var feature = new Feature(new Point(0, 0))
        {
            Properties =
            {
                // 19 digits, wider than the clamped field
                ["big"] = long.MaxValue
            }
        };
        var back = G.RoundtripShapefile([feature]);
        await Assert.That(back.Features[0].Properties["big"]).IsNotNull();
    }

    [Test]
    public async Task Png_handles_empty_polygon()
    {
        var collection = new FeatureCollection
        {
            new Feature(new Polygon([])),
            new Feature(new Point(1, 1))
        };
        var png = MapRenderer.RenderPng(
            collection,
            new()
        {
            Bounds = new Envelope(0, 0, 2, 2),
            Width = 32,
            Height = 32
        });
        await Assert.That(png.Length).IsGreaterThan(8);
    }

    [Test]
    public async Task FlatGeobuf_columns_widening_and_null_geometry()
    {
        var a = new Feature(new Point(0, 0))
        {
            Properties =
            {
                ["empty"] = null,
                ["num"] = 1L,
                ["mixed"] = "a",
                ["flag"] = true,
                ["text"] = "hello",
                ["dec"] = 1.5d
            }
        };

        var b = new Feature(new Point(1, 1))
        {
            Properties =
            {
                ["num"] = 2.5d, // long -> double widening
                ["mixed"] = 3L // string + long -> string
            }
        };

        var back = G.RoundtripStream(
            [
                a,
                b,
                new Feature()
            ],
            GeoFormat.FlatGeobuf);
        await Assert.That(back.Count).IsEqualTo(3);
        await Assert.That(back.Features[2].Geometry).IsNull();
    }
}
