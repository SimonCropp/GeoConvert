using G = TestSupport;

// Targeted regressions for the audit findings — each test pins a previously broken behavior so a
// regression on a single bug fails in isolation rather than getting buried in a wider snapshot.
public class BugFixTests
{
    // #1 — CsvParser used to swallow a lone CR without flushing the row, so classic-Mac (\r-only)
    // CSVs read as one giant row. CRLF must collapse to a single line break.
    [Test]
    public async Task Csv_parses_cr_only_line_endings()
    {
        var collection = Csv.ReadString("lon,lat,name\r1,2,A\r3,4,B\r");
        await Assert.That(collection.Count).IsEqualTo(2);
        await Assert.That(collection.Features[0].Properties["name"]).IsEqualTo("A");
        await Assert.That(collection.Features[1].Properties["name"]).IsEqualTo("B");
    }

    [Test]
    public async Task Csv_parses_crlf_line_endings_without_blank_rows()
    {
        var collection = Csv.ReadString("lon,lat,name\r\n1,2,A\r\n3,4,B\r\n");
        await Assert.That(collection.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Csv_parses_mixed_lf_line_endings()
    {
        var collection = Csv.ReadString("lon,lat,name\n1,2,A\n3,4,B");
        await Assert.That(collection.Count).IsEqualTo(2);
    }

    // #2 — Writing an empty Point (Position(NaN, NaN)) used to throw ArgumentException from
    // Utf8JsonWriter. Empty Point should emit `"coordinates": []` and round-trip back to empty.
    [Test]
    public async Task GeoJson_empty_point_writes_and_round_trips()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(new(double.NaN, double.NaN)))
        };
        var json = GeoJson.WriteString(source);
        await Assert.That(json).Contains("\"coordinates\": []");

        var back = GeoJson.ReadString(json);
        await Assert.That(((Point)back.Features[0].Geometry!).IsEmpty).IsTrue();
    }

    [Test]
    public async Task TopoJson_empty_point_writes_and_round_trips()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(new(double.NaN, double.NaN)))
        };
        var json = TopoJson.WriteString(source);
        await Assert.That(json).Contains("\"coordinates\": []");

        var back = TopoJson.ReadString(json);
        await Assert.That(((Point)back.Children[0].Features[0].Geometry!).IsEmpty).IsTrue();
    }

    // Non-finite ordinates on a populated geometry stay a hard error — the only well-defined empty is
    // an entirely-NaN Point. PositiveInfinity must produce GeoConvertException, not ArgumentException.
    [Test]
    public async Task GeoJson_rejects_infinite_coordinate()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(new(double.PositiveInfinity, 1)))
        };
        await Assert.That(G.ThrowsGeo(() => GeoJson.WriteString(source))).IsTrue();
    }

    [Test]
    public async Task TopoJson_rejects_infinite_coordinate()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(new(double.PositiveInfinity, 1)))
        };
        await Assert.That(G.ThrowsGeo(() => TopoJson.WriteString(source))).IsTrue();
    }

    // #3 — DBF field names are capped at 10 chars; two property keys sharing a 10-char prefix used to
    // collapse to one column on write, silently dropping one of the values on read.
    [Test]
    public async Task Shapefile_disambiguates_dbf_field_name_collisions()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(new(1, 2)))
            {
                Properties =
                {
                    ["PopulationDensity"] = 100L,
                    ["PopulationGrowth"] = 5L,
                },
            },
        };
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "data.shp");
        Shapefile.Write(path, source);
        var back = Shapefile.Read(path).Features[0];

        // Both source values should survive; the DBF field names diverge at the suffix.
        var values = back.Properties.Values.OfType<long>().OrderBy(_ => _).ToList();
        await Assert.That(values).IsEquivalentTo([5L, 100L]);
        await Assert.That(back.Properties.Keys.Count).IsEqualTo(2);
    }

    // #4 — Kmz.Read used to dispose the caller's stream via the default ZipArchive constructor.
    [Test]
    public async Task Kmz_read_leaves_caller_stream_open()
    {
        using var memory = new MemoryStream();
        Kmz.Write(memory, [new Feature(new Point(new(1, 2)))]);
        memory.Position = 0;
        Kmz.Read(memory);

        // If Kmz.Read had closed the stream, accessing Length would throw ObjectDisposedException.
        await Assert.That(memory.CanRead).IsTrue();
        await Assert.That(memory.Length > 0).IsTrue();
    }

    // #5 — Envelope.IsEmpty used to check only MinX, so a position with a single NaN ordinate produced
    // a partially-NaN envelope that read as non-empty and crashed downstream JSON writers.
    [Test]
    public async Task Envelope_is_empty_for_any_non_finite_corner()
    {
        await Assert.That(new Envelope(1, double.NaN, 1, 1).IsEmpty).IsTrue();
        await Assert.That(new Envelope(1, 1, double.NaN, 1).IsEmpty).IsTrue();
        await Assert.That(new Envelope(1, 1, 1, double.PositiveInfinity).IsEmpty).IsTrue();
        await Assert.That(new Envelope(1, 2, 3, 4).IsEmpty).IsFalse();
    }

    [Test]
    public async Task Envelope_partial_nan_bounds_dropped_on_collection()
    {
        // A NaN-Y position no longer poisons the collection bounds; the well-formed point's box wins.
        var collection = new FeatureCollection
        {
            new Feature(new Point(new(1, 2))),
            new Feature(new Point(new(5, double.NaN))),
        };
        var bounds = collection.GetBounds();
        await Assert.That(bounds.IsEmpty).IsFalse();
        await Assert.That(bounds.MinX).IsEqualTo(1d);
        await Assert.That(bounds.MaxX).IsEqualTo(1d);
    }

    // #6 — Nested JSON objects/arrays were read back as quoted strings on write; verify the JsonRaw
    // wrapper now round-trips them as JSON values.
    [Test]
    public async Task JsonRaw_round_trips_nested_object()
    {
        var source = new FeatureCollection
        {
            new Feature(new Point(new(0, 0)))
            {
                Properties =
                {
                    ["meta"] = new JsonRaw("""{"k":1}"""),
                },
            },
        };

        var json = GeoJson.WriteString(source);
        // The nested object emits unquoted (not as `"{\"k\":1}"`).
        await Assert.That(json).Contains("\"meta\": {");

        var back = GeoJson.ReadString(json).Features[0];
        await Assert.That(((JsonRaw)back.Properties["meta"]!).Json).IsEqualTo("""{"k":1}""");
    }

    [Test]
    public async Task JsonRaw_tostring_returns_underlying_json() =>
        await Assert.That(new JsonRaw("""{"a":1}""").ToString()).IsEqualTo("""{"a":1}""");

    // Property values that happen to be NaN/Infinity must not crash the JSON writer either; they
    // serialize as JSON null (the format has no scalar for them).
    [Test]
    public async Task JsonValue_writes_non_finite_property_as_null()
    {
        var feature = new Feature(new Point(new(0, 0)))
        {
            Properties =
            {
                ["bad"] = double.NaN,
            },
        };
        var json = GeoJson.WriteString([feature]);
        await Assert.That(json).Contains("\"bad\": null");
    }

    // #7 — GPX and KML used to surface raw FormatException/IndexOutOfRangeException on malformed
    // input. Every parse path must funnel through GeoConvertException.
    [Test]
    public async Task Gpx_rejects_malformed_lat_attribute()
    {
        const string gpx =
            """<?xml version="1.0"?><gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1"><wpt lat="oops" lon="0"/></gpx>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx));
        await Assert.That(G.ThrowsGeo(() => Gpx.Read(stream))).IsTrue();
    }

    [Test]
    public async Task Gpx_rejects_missing_lat_attribute()
    {
        const string gpx =
            """<?xml version="1.0"?><gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1"><wpt lon="0"/></gpx>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx));
        await Assert.That(G.ThrowsGeo(() => Gpx.Read(stream))).IsTrue();
    }

    [Test]
    public async Task Gpx_rejects_malformed_ele_element()
    {
        const string gpx =
            """<?xml version="1.0"?><gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1"><wpt lat="1" lon="2"><ele>oops</ele></wpt></gpx>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx));
        await Assert.That(G.ThrowsGeo(() => Gpx.Read(stream))).IsTrue();
    }

    [Test]
    public async Task Kml_rejects_malformed_coordinate()
    {
        const string kml =
            """
            <?xml version="1.0"?><kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <Placemark><Point><coordinates>oops,1</coordinates></Point></Placemark>
            </Document></kml>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kml));
        await Assert.That(G.ThrowsGeo(() => Kml.Read(stream))).IsTrue();
    }

    [Test]
    public async Task Kml_rejects_single_value_coordinate_tuple()
    {
        const string kml =
            """
            <?xml version="1.0"?><kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <Placemark><Point><coordinates>1</coordinates></Point></Placemark>
            </Document></kml>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kml));
        await Assert.That(G.ThrowsGeo(() => Kml.Read(stream))).IsTrue();
    }

    // #8 — WKB MultiPoint/MultiLineString/MultiPolygon used to cast sub-geometries without type
    // checking, raising InvalidCastException on a malformed file. The cast must convert to
    // GeoConvertException with a useful message.
    [Test]
    public async Task Wkb_multipoint_rejects_non_point_member()
    {
        // little-endian MultiPoint with 1 element, but the element is a LineString (type 2).
        byte[] bytes =
        [
            1, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            1, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        await Assert.That(G.ThrowsGeo(() => Wkb.ParseGeometry(bytes))).IsTrue();
    }

    [Test]
    public async Task Wkb_multilinestring_rejects_non_line_member()
    {
        // MultiLineString of 1 element where the element is a Point.
        byte[] bytes =
        [
            1, 0x05, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            1, 0x01, 0x00, 0x00, 0x00,
            .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(0d),
        ];
        await Assert.That(G.ThrowsGeo(() => Wkb.ParseGeometry(bytes))).IsTrue();
    }

    [Test]
    public async Task Wkb_multipolygon_rejects_non_polygon_member()
    {
        // MultiPolygon of 1 element where the element is a Point.
        byte[] bytes =
        [
            1, 0x06, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            1, 0x01, 0x00, 0x00, 0x00,
            .. BitConverter.GetBytes(0d), .. BitConverter.GetBytes(0d),
        ];
        await Assert.That(G.ThrowsGeo(() => Wkb.ParseGeometry(bytes))).IsTrue();
    }

    // #9 — GeoJSON writer used to emit polygon rings in whatever orientation came in; RFC 7946 §3.1.6
    // requires exterior CCW, holes CW. A shapefile (CW exteriors) → GeoJSON write must flip the rings.
    [Test]
    public async Task GeoJson_writes_polygons_with_rfc_7946_orientation()
    {
        // Construct with CW exterior + CCW hole (opposite of RFC), confirm the writer re-orients.
        var cwExterior = new List<Position>
        {
            new(0, 0),
            new(0, 4),
            new(4, 4),
            new(4, 0),
            new(0, 0)
        };
        var ccwHole = new List<Position>
        {
            new(1, 1),
            new(1, 2),
            new(2, 2),
            new(2, 1),
            new(1, 1)
        };
        var source = new FeatureCollection
        {
            new Feature(new Polygon([cwExterior, ccwHole])),
        };

        var back = GeoJson.ReadString(GeoJson.WriteString(source));
        var polygon = (Polygon)back.Features[0].Geometry!;
        await Assert.That(Ring.IsClockwise(polygon.Rings[0])).IsFalse(); // exterior must be CCW
        await Assert.That(Ring.IsClockwise(polygon.Rings[1])).IsTrue();  // hole must be CW
    }

    // #10 — A file with the "fgb" prefix but a wrong second-half-magic used to slip past the read
    // path and crash deeper in. The full 8-byte check must reject it cleanly.
    [Test]
    public async Task FlatGeobuf_rejects_partial_magic()
    {
        // "fgb\x03" matches the first half but the second half differs from the canonical magic.
        byte[] data = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x99];
        using var stream = new MemoryStream(data);
        await Assert.That(G.ThrowsGeo(() => FlatGeobuf.Read(stream))).IsTrue();
    }

    // #11 — A shapefile whose polygon contains two clockwise rings (two separate exteriors) must
    // produce a MultiPolygon rather than collapsing into a single polygon-with-hole. Even though the
    // bug was about CCW-first files specifically, exercising the orientation-driven split also
    // protects the existing well-behaved path.
    [Test]
    public async Task Shapefile_two_clockwise_rings_become_multi_polygon()
    {
        var multi = new MultiPolygon(
        [
            new([[new(0, 0), new(0, 4), new(4, 4), new(4, 0), new(0, 0)]]),
            new([[new(10, 10), new(10, 14), new(14, 14), new(14, 10), new(10, 10)]]),
        ]);

        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "data.shp");
        Shapefile.Write(path, [new Feature(multi)]);
        var back = Shapefile.Read(path).Features[0].Geometry;
        await Assert.That(back).IsTypeOf<MultiPolygon>();
        await Assert.That(((MultiPolygon)back!).Polygons.Count).IsEqualTo(2);
    }

    // #12 — A maliciously-large declared decompressed size in a Snappy block must error out, not
    // allocate gigabytes or overflow int. The varint encodes 0xFFFFFFFF (>=4GB).
    [Test]
    public async Task Snappy_rejects_oversized_block_length()
    {
        // 5-byte varint for 0xFFFFFFFF: 0xFF 0xFF 0xFF 0xFF 0x0F. No further block bytes.
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F];
        await Assert.That(G.ThrowsGeo(() => Snappy.Decompress(data))).IsTrue();
    }

    // #13 — A truncated WKB (declares a type but cuts off mid-record) used to leak
    // IndexOutOfRangeException; the wrapper converts to GeoConvertException.
    [Test]
    public async Task Wkb_truncated_input_surfaces_geoconvert_exception()
    {
        // Declares a Point (type 1) but supplies no coordinates.
        byte[] truncated = [1, 0x01, 0x00, 0x00, 0x00];
        using var stream = new MemoryStream(truncated);
        await Assert.That(G.ThrowsGeo(() => Wkb.Read(stream))).IsTrue();
        await Assert.That(G.ThrowsGeo(() => Wkb.ParseGeometry(truncated))).IsTrue();
    }

    [Test]
    public async Task FlatGeobuf_truncated_input_surfaces_geoconvert_exception()
    {
        // Valid magic, but the size-prefixed header is cut off mid-structure.
        byte[] truncated = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00, 0x10, 0x00];
        using var stream = new MemoryStream(truncated);
        await Assert.That(G.ThrowsGeo(() => FlatGeobuf.Read(stream))).IsTrue();
    }

    [Test]
    public async Task FlatGeobuf_size_prefix_overruns_buffer_surfaces_geoconvert_exception()
    {
        // Magic + a size prefix that declares 1000 bytes follow, but only 4 do. Without the
        // length-bounded read this would walk off the end of the GetBuffer() backing array.
        byte[] truncated = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00, 0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        using var stream = new MemoryStream(truncated);
        await Assert.That(G.ThrowsGeo(() => FlatGeobuf.Read(stream))).IsTrue();
    }

    // The above XML-malformed inputs already exercise the GeoConvertException re-throw path. These
    // exercise the *other* catch (Exception) branch where a non-GCE BCL exception (XmlException for
    // garbage input) gets translated into a GeoConvertException.
    [Test]
    public async Task Gpx_wraps_xml_exception()
    {
        using var stream = new MemoryStream("not really xml at all"u8.ToArray());
        await Assert.That(G.ThrowsGeo(() => Gpx.Read(stream))).IsTrue();
    }

    [Test]
    public async Task Kml_wraps_xml_exception()
    {
        using var stream = new MemoryStream("not really xml at all"u8.ToArray());
        await Assert.That(G.ThrowsGeo(() => Kml.Read(stream))).IsTrue();
    }

    // Wkb.Read (the streaming path) needs its own coverage of the GeoConvertException re-throw
    // branch — feed it a stream that decodes a single record with an unknown type code.
    [Test]
    public async Task Wkb_read_stream_rethrows_geoconvert_exception()
    {
        byte[] bytes = [1, 99, 0, 0, 0];
        using var stream = new MemoryStream(bytes);
        await Assert.That(G.ThrowsGeo(() => Wkb.Read(stream))).IsTrue();
    }

    // Shapefile decomposition: a CCW ring whose bbox lies inside the prior CW exterior is a hole
    // (covered already by Sample.Polygons in roundtrip tests), while a CCW ring whose bbox sits
    // outside must start its own polygon — exercise the else branch directly with a crafted record.
    [Test]
    public async Task Shapefile_stray_ccw_ring_starts_new_polygon()
    {
        // One Polygon shape record with two parts: a CW exterior at (0,0)-(4,4) and a CCW ring at
        // (100,100)-(104,104). The CCW ring's bbox isn't contained in the exterior, so the reader
        // breaks it into its own polygon — the file decodes as a MultiPolygon, not Polygon+hole.
        // (In Y-up coords, CW means negative signed area: walk right→down→left→up.)
        var data = BuildPolygonShapefile(
        [
            // CW exterior
            [new(0, 4), new(4, 4), new(4, 0), new(0, 0), new(0, 4)],
            // CCW
            [new(100, 100), new(104, 100), new(104, 104), new(100, 104), new(100, 100)],
        ]);

        using var stream = new MemoryStream(data);
        var collection = Shapefile.Read(stream, null);
        await Assert.That(collection.Features[0].Geometry).IsTypeOf<MultiPolygon>();
        await Assert.That(((MultiPolygon)collection.Features[0].Geometry!).Polygons.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Shapefile_contained_ccw_ring_becomes_hole()
    {
        var data = BuildPolygonShapefile(
        [
            // CW exterior
            [new(0, 10), new(10, 10), new(10, 0), new(0, 0), new(0, 10)],
            // CCW hole inside
            [new(1, 1), new(2, 1), new(2, 2), new(1, 2), new(1, 1)],
        ]);

        using var stream = new MemoryStream(data);
        var collection = Shapefile.Read(stream, null);
        var polygon = (Polygon)collection.Features[0].Geometry!;
        await Assert.That(polygon.Rings.Count).IsEqualTo(2);
    }

    // Dbf.UniqueName has a 999-suffix safety bound. Verify the throw fires when the budget is
    // exhausted — i.e. >999 unique keys all sharing the same 10-char prefix.
    [Test]
    public async Task Dbf_throws_when_field_name_disambiguator_exhausted()
    {
        var feature = new Feature(new Point(new(0, 0)));
        // 1001 keys that share the same first 10 chars: "common1234" + suffix.
        for (var i = 0; i < 1001; i++)
        {
            feature.Properties[$"common1234_long_key_{i:D5}"] = (long)i;
        }

        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "out.shp");
        await Assert.That(G.ThrowsGeo(() => Shapefile.Write(path, [feature])))
            .IsTrue();
    }

    // Build a minimal valid .shp byte stream holding one Polygon record with the given rings. Used
    // by the Shapefile_*_ring tests to feed crafted multi-part records into the parser.
    static byte[] BuildPolygonShapefile(IReadOnlyList<IReadOnlyList<Position>> rings)
    {
        var totalPoints = rings.Sum(_ => _.Count);
        // header(100) + record header(8) + content[shape(4) + bbox(32) + parts/points(8) + parts*4 + points*16]
        var contentBytes = 4 + 32 + 8 + rings.Count * 4 + totalPoints * 16;
        var data = new byte[100 + 8 + contentBytes];

        // .shp header: file code, file length in 16-bit words, version 1000, shape type 5 (Polygon).
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0), 9994);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(24), (100 + 8 + contentBytes) / 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), 5);

        // Record header: record number 1, content length in 16-bit words.
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(100), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(104), contentBytes / 2);

        // Polygon content: shape type, bbox (zeroed), part count, point count, part starts, points.
        var content = data.AsSpan(108);
        BinaryPrimitives.WriteInt32LittleEndian(content, 5);
        BinaryPrimitives.WriteInt32LittleEndian(content[36..], rings.Count);
        BinaryPrimitives.WriteInt32LittleEndian(content[40..], totalPoints);

        var partStart = 0;
        for (var i = 0; i < rings.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(content[(44 + i * 4)..], partStart);
            partStart += rings[i].Count;
        }

        var offset = 44 + rings.Count * 4;
        foreach (var ring in rings)
        {
            foreach (var position in ring)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(content[offset..], position.X);
                BinaryPrimitives.WriteDoubleLittleEndian(content[(offset + 8)..], position.Y);
                offset += 16;
            }
        }

        return data;
    }
}
