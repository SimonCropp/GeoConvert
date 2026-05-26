public class XmlFormatTests
{
    static FeatureCollection ReadKml(string kml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kml));
        return Kml.Read(stream);
    }

    static FeatureCollection ReadGpx(string gpx)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx));
        return Gpx.Read(stream);
    }

    [Test]
    public async Task Kml_skips_unknown_elements()
    {
        // Real KML carries styles, snippets, schema data, altitude modes, etc. that we ignore.
        var collection = ReadKml(
            """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <name>Doc</name>
              <Placemark>
                <name>A</name><description>d</description><styleUrl>#s</styleUrl>
                <ExtendedData>
                  <SchemaData/>
                  <Data name="k"><displayName>K</displayName><value>v</value></Data>
                </ExtendedData>
                <Point><extrude>1</extrude><coordinates>1,2</coordinates></Point>
              </Placemark>
              <Placemark>
                <Polygon><tessellate>1</tessellate>
                  <outerBoundaryIs><altitudeMode>clampToGround</altitudeMode>
                    <LinearRing><coordinates>0,0 1,0 1,1 0,0</coordinates></LinearRing></outerBoundaryIs>
                </Polygon>
              </Placemark>
              <Placemark>
                <MultiGeometry><Snippet>x</Snippet><Point><coordinates>5,6</coordinates></Point></MultiGeometry>
              </Placemark>
            </Document></kml>
            """);

        await Assert.That(collection.Count).IsEqualTo(3);
        await Assert.That(collection.Features[0].Properties["name"]).IsEqualTo("A");
        await Assert.That(collection.Features[0].Properties["k"]).IsEqualTo("v");
        await Assert.That(collection.Features[0].Geometry).IsTypeOf<Point>();
        await Assert.That(collection.Features[1].Geometry).IsTypeOf<Polygon>();
        await Assert.That(collection.Features[2].Geometry).IsTypeOf<MultiPoint>();
    }

    [Test]
    public async Task Gpx_skips_unknown_elements()
    {
        var collection = ReadGpx(
            """
            <gpx xmlns="http://www.topografix.com/GPX/1/1">
              <metadata><name>m</name></metadata>
              <wpt lat="2" lon="1"><time>t</time><ele>5</ele><name>W</name><desc>wd</desc></wpt>
              <rte><number>1</number><name>R</name><desc>rd</desc>
                <rtept lat="0" lon="0"/><rtept lat="1" lon="1"/></rte>
              <trk><type>x</type><name>T</name><desc>td</desc>
                <trkseg><speed>9</speed><trkpt lat="0" lon="0"/><trkpt lat="1" lon="1"/></trkseg></trk>
            </gpx>
            """);

        await Assert.That(collection.Count).IsEqualTo(3);
        // GPX reads each category (wpt/rte/trk) into its own child layer; use the recursive enumerator
        // to address the flat sequence regardless of layering.
        var waypoint = (Point)collection.ElementAt(0).Geometry!;
        await Assert.That(waypoint.Coordinate.Z).IsEqualTo(5d);
        await Assert.That(collection.ElementAt(0).Properties["description"]).IsEqualTo("wd");
        await Assert.That(collection.ElementAt(1).Properties["description"]).IsEqualTo("rd");
        await Assert.That(collection.ElementAt(2).Properties["description"]).IsEqualTo("td");
    }

    [Test]
    public async Task Kml_round_trips_name_and_description()
    {
        var feature = new Feature(new Point(1, 2))
        {
            Properties =
            {
                ["name"] = "A",
                ["description"] = "desc",
                ["other"] = "x"
            }
        };

        var back = TestSupport.RoundtripStream([feature], GeoFormat.Kml).Features[0];
        await Assert.That(back.Properties["name"]).IsEqualTo("A");
        await Assert.That(back.Properties["description"]).IsEqualTo("desc");
        await Assert.That(back.Properties["other"]).IsEqualTo("x");
    }

    [Test]
    public async Task Kml_reads_empty_point()
    {
        var collection = ReadKml(
            """<kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><Point></Point></Placemark></kml>""");
        await Assert.That(((Point)collection.Features[0].Geometry!).IsEmpty).IsTrue();
    }

    [Test]
    public async Task Kml_mixed_multigeometry_becomes_collection()
    {
        var collection = ReadKml(
            """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><MultiGeometry>
            <Point><coordinates>1,2</coordinates></Point>
            <LineString><coordinates>0,0 1,1</coordinates></LineString>
            </MultiGeometry></Placemark></kml>
            """);
        await Assert.That(collection.Features[0].Geometry).IsTypeOf<GeometryCollection>();
    }

    [Test]
    public async Task Kml_ignores_data_without_name()
    {
        var collection = ReadKml(
            """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Placemark>
            <ExtendedData><Data><value>v</value></Data></ExtendedData>
            <Point><coordinates>1,2</coordinates></Point>
            </Placemark></kml>
            """);
        await Assert.That(collection.Features[0].Properties.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Gpx_reads_route_as_line()
    {
        var collection = ReadGpx(
            """
            <gpx xmlns="http://www.topografix.com/GPX/1/1"><rte><name>r</name>
            <rtept lat="1" lon="2"/><rtept lat="3" lon="4"/></rte></gpx>
            """);
        await Assert.That(collection.ElementAt(0).Geometry).IsTypeOf<LineString>();
        await Assert.That(collection.ElementAt(0).Properties["name"]).IsEqualTo("r");
    }

    [Test]
    public async Task Gpx_reads_multi_segment_track_as_multiline()
    {
        var collection = ReadGpx(
            """
            <gpx xmlns="http://www.topografix.com/GPX/1/1"><trk>
            <trkseg><trkpt lat="0" lon="0"/><trkpt lat="1" lon="1"/></trkseg>
            <trkseg><trkpt lat="2" lon="2"/><trkpt lat="3" lon="3"/></trkseg>
            </trk></gpx>
            """);
        await Assert.That(collection.ElementAt(0).Geometry).IsTypeOf<MultiLineString>();
    }

    [Test]
    public async Task Gpx_writes_multipoint_as_waypoints()
    {
        var source = new FeatureCollection { new Feature(new MultiPoint([new(1, 2), new(3, 4)])) };
        var back = TestSupport.RoundtripStream(source, GeoFormat.Gpx);
        await Assert.That(back.Count).IsEqualTo(2);
        await Assert.That(back.ElementAt(0).Geometry).IsTypeOf<Point>();
    }

    [Test]
    public async Task Gpx_writes_polygon_rings_as_track_segments()
    {
        // GPX has no area type, so a polygon's rings (exterior then holes) become track segments and
        // read back as a multi line string.
        var source = new FeatureCollection
        {
            new Feature(new Polygon(
            [
                [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
                [new(1, 1), new(2, 1), new(2, 2), new(1, 2), new(1, 1)],
            ]))
        };
        var back = (MultiLineString)TestSupport.RoundtripStream(source, GeoFormat.Gpx).ElementAt(0).Geometry!;
        await Assert.That(back.LineStrings.Count).IsEqualTo(2);
        await Assert.That(back.LineStrings[0].Positions.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Gpx_writes_multipolygon_rings_into_single_track()
    {
        var source = new FeatureCollection
        {
            new Feature(new MultiPolygon(
            [
                new([[new(0, 0), new(1, 0), new(1, 1), new(0, 0)]]),
                new([[new(5, 5), new(6, 5), new(6, 6), new(5, 5)]]),
            ]))
        };
        var back = (MultiLineString)TestSupport.RoundtripStream(source, GeoFormat.Gpx).ElementAt(0).Geometry!;
        await Assert.That(back.LineStrings.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Gpx_writes_collection_members_in_turn()
    {
        var source = new FeatureCollection
        {
            new Feature(new GeometryCollection([new Point(7, 8), new LineString([new(0, 0), new(1, 1)])]))
        };
        // The point becomes a waypoint and the line a track, so the collection reads back as two features.
        var back = TestSupport.RoundtripStream(source, GeoFormat.Gpx);
        await Assert.That(back.Count).IsEqualTo(2);
        await Assert.That(back.ElementAt(0).Geometry).IsTypeOf<Point>();
        await Assert.That(back.ElementAt(1).Geometry).IsTypeOf<LineString>();
    }
}
