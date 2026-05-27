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
        var feature = TopoJson.ReadString(topology).Children[0].Features[0];
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
        await Assert.That(back.ElementAt(0).Geometry).IsTypeOf<MultiLineString>();
        await Assert.That(back.ElementAt(0).Properties["description"]).IsEqualTo("d");
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
        var features = new FeatureCollection
        {
            new Feature(new Polygon([])),
            new Feature(new Point(1, 1))
        };
        var png = MapRenderer.RenderPng(
            features,
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

    [Test]
    public async Task FeatureCollection_collection_initializer_adds_child_layer()
    {
        var inner = new FeatureCollection { Name = "inner" };
        inner.Add(new Feature(new Point(3, 4)));
        var root = new FeatureCollection
        {
            new Feature(new Point(1, 2)),
            inner
        };
        await Assert.That(root.Children.Count).IsEqualTo(1);
        await Assert.That(root.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Kml_nested_document_becomes_child()
    {
        const string kml =
            """<kml xmlns="http://www.opengis.net/kml/2.2"><Document><Document><name>nested</name><Placemark><Point><coordinates>1,2</coordinates></Point></Placemark></Document></Document></kml>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kml));
        var collection = Kml.Read(stream);
        await Assert.That(collection.Children[0].Name).IsEqualTo("nested");
    }

    [Test]
    public async Task Kml_unknown_container_child_is_skipped()
    {
        const string kml =
            """<kml xmlns="http://www.opengis.net/kml/2.2"><Document><Style id="x"/><Placemark><Point><coordinates>1,2</coordinates></Point></Placemark></Document></kml>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kml));
        var collection = Kml.Read(stream);
        await Assert.That(collection.Features.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Kmz_empty_archive_throws()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("readme.txt");
        }

        memory.Position = 0;
        await Assert.That(G.ThrowsGeo(() => Kmz.Read(memory))).IsTrue();
    }

    [Test]
    public async Task Kmz_multi_document_becomes_layered_root()
    {
        const string oneKml =
            """<kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark><name>p1</name><Point><coordinates>1,2</coordinates></Point></Placemark></Document></kml>""";
        const string twoKml =
            """<kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark><name>p2</name><Point><coordinates>3,4</coordinates></Point></Placemark></Document></kml>""";

        using var memory = new MemoryStream();
        await using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var entry = await archive.CreateEntry("one.kml").OpenAsync())
            {
                entry.Write(Encoding.UTF8.GetBytes(oneKml));
            }

            await using (var entry = await archive.CreateEntry("two.kml").OpenAsync())
            {
                entry.Write(Encoding.UTF8.GetBytes(twoKml));
            }
        }

        memory.Position = 0;
        var collection = Kmz.Read(memory);
        await Assert.That(collection.Children.Count).IsEqualTo(2);
        // child.Name defaults to entry filename when the document has no <name>
        await Assert.That(collection.Children[0].Name).IsEqualTo("one");
    }

    [Test]
    public async Task Kmz_compression_level_is_honored()
    {
        // The ZIP local file header records the chosen compression method (0 = stored, 8 = deflate),
        // so writing the same KML at NoCompression vs Optimal must produce different on-disk method
        // bytes — and the stored entry should be strictly larger than the deflated one.
        var features = new FeatureCollection();
        for (var i = 0; i < 50; i++)
        {
            features.Add(new Feature(new Point(new(i, i))));
        }

        using var stored = new MemoryStream();
        Kmz.Write(stored, features, CompressionLevel.NoCompression);

        using var deflated = new MemoryStream();
        Kmz.Write(deflated, features, CompressionLevel.Optimal);

        await Assert.That(stored.Length).IsGreaterThan(deflated.Length);

        // Both must still round-trip.
        stored.Position = 0;
        var read = Kmz.Read(stored);
        await Assert.That(read.Count).IsEqualTo(50);
    }

    [Test]
    public async Task TopoJson_writes_unique_keys_for_duplicate_layer_names()
    {
        var first = new FeatureCollection { Name = "data" };
        first.Add(new Feature(new Point(1, 2)));
        var second = new FeatureCollection { Name = "data" };
        second.Add(new Feature(new Point(3, 4)));
        var root = new FeatureCollection { first, second };
        var topology = TopoJson.WriteString(root);
        // Second "data" must be disambiguated; first stays as "data".
        await Assert.That(topology).Contains("\"data_1\":");
    }

    [Test]
    public async Task Gpx_category_write_handles_root_and_non_category_features()
    {
        // Layered input that mixes a root-level feature, a category layer ("waypoints"), and a
        // non-category sibling layer — exercises the fallback dispatch inside the category-write path.
        var waypoints = new FeatureCollection
        {
            Name = "waypoints"
        };
        waypoints.Add(new Feature(new Point(1, 2)));
        var stray = new FeatureCollection
        {
            Name = "misc"
        };
        stray.Add(new Feature(new LineString([new(0, 0), new(1, 1)])));
        var root = new FeatureCollection
        {
            new Feature(new Point(9, 9)), waypoints, stray
        };

        using var stream = new MemoryStream();
        Gpx.Write(stream, root);
        stream.Position = 0;
        var back = Gpx.Read(stream);
        // 1 root wpt + 1 waypoints layer wpt = 2 wpts; the stray line writes as trk.
        await Assert.That(back.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Shapefile_directory_read_tolerates_missing_dbf()
    {
        using var directory = new TempDirectory();
        // Write one shapefile through the normal path, then delete its .dbf to mimic an old/partial dataset.
        Shapefile.Write(Path.Combine(directory, "geom.shp"), Sample.Polygons());
        File.Delete(Path.Combine(directory, "geom.dbf"));

        var bundle = Shapefile.Read(directory);
        await Assert.That(bundle.Children.Count).IsEqualTo(1);
        await Assert.That(bundle.Children[0].Features[0].Properties.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Shapefile_directory_write_emits_root_features_as_data_shp()
    {
        var child = new FeatureCollection { Name = "extras" };
        child.Add(new Feature(new Point(5, 5)));
        var root = new FeatureCollection
        {
            new Feature(new Point(1, 1)),
            new Feature(new Point(2, 2)),
            child
        };

        using var directory = new TempDirectory();
        Shapefile.Write(Path.Combine(directory, "bundle") + Path.DirectorySeparatorChar, root);
        var datasetPath = Path.Combine(directory, "bundle");

        await Assert.That(File.Exists(Path.Combine(datasetPath, "data.shp"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(datasetPath, "extras.shp"))).IsTrue();

        var back = Shapefile.Read(datasetPath);
        await Assert.That(back.Children.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Xml_read_children_skips_text_nodes()
    {
        // Significant text mixed with elements forces the non-element branch in ReadChildren.
        using var stream = new MemoryStream("<root>text<child/></root>"u8.ToArray());
        using var reader = XmlReader.Create(stream);
        // ReSharper disable once MethodHasAsyncOverload
        reader.MoveToContent();
        var seen = 0;
        Xml.ReadChildren(reader, () =>
        {
            seen++;
            reader.Skip();
        });

        await Assert.That(seen).IsEqualTo(1);
    }
}
