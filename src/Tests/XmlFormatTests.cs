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
