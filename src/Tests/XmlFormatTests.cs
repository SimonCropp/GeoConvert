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
        var waypoint = (Point)collection.Features[0].Geometry!;
        await Assert.That(waypoint.Coordinate.Z).IsEqualTo(5d);
        await Assert.That(collection.Features[0].Properties["description"]).IsEqualTo("wd");
        await Assert.That(collection.Features[1].Properties["description"]).IsEqualTo("rd");
        await Assert.That(collection.Features[2].Properties["description"]).IsEqualTo("td");
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
        await Assert.That(collection.Features[0].Geometry).IsTypeOf<LineString>();
        await Assert.That(collection.Features[0].Properties["name"]).IsEqualTo("r");
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
        await Assert.That(collection.Features[0].Geometry).IsTypeOf<MultiLineString>();
    }

    [Test]
    public async Task Gpx_writes_multipoint_as_waypoints()
    {
        var source = new FeatureCollection { new Feature(new MultiPoint([new(1, 2), new(3, 4)])) };
        var back = TestSupport.RoundtripStream(source, GeoFormat.Gpx);
        await Assert.That(back.Count).IsEqualTo(2);
        await Assert.That(back.Features[0].Geometry).IsTypeOf<Point>();
    }

    [Test]
    public async Task Gpx_rejects_polygon()
    {
        var source = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(1, 0), new(1, 1), new(0, 0)]])),
        };
        await Assert.That(TestSupport.ThrowsGeo(() =>
        {
            using var stream = new MemoryStream();
            Gpx.Write(stream, source);
        })).IsTrue();
    }
}
