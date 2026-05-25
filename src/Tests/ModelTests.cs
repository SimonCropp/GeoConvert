public class ModelTests
{
    [Test]
    public async Task Position_to_string()
    {
        await Assert.That(new Position(1, 2).ToString()).IsEqualTo("(1 2)");
        await Assert.That(new Position(1, 2, 3).ToString()).IsEqualTo("(1 2 3)");
        await Assert.That(new Position(1, 2, 3, 4).ToString()).IsEqualTo("(1 2 3 4)");
        await Assert.That(new Position(1, 2, null, 4).ToString()).Contains("NaN");
    }

    [Test]
    public async Task Position_flags()
    {
        await Assert.That(new Position(1, 2).HasZ).IsFalse();
        await Assert.That(new Position(1, 2, 3).HasZ).IsTrue();
        await Assert.That(new Position(1, 2, 3, 4).HasM).IsTrue();
    }

    [Test]
    public async Task Point_xy_constructor()
    {
        var point = new Point(3, 4);
        await Assert.That(point.Coordinate.X).IsEqualTo(3d);
        await Assert.That(point.Coordinate.Y).IsEqualTo(4d);
    }

    [Test]
    public async Task Empty_point_has_empty_bounds()
    {
        var point = new Point(new(double.NaN, double.NaN));
        await Assert.That(point.IsEmpty).IsTrue();
        await Assert.That(point.GetBounds().IsEmpty).IsTrue();
    }

    [Test]
    public async Task Envelope_expansion_and_metrics()
    {
        var box = new Envelope(0, 0, 10, 5);
        await Assert.That(box.Width).IsEqualTo(10d);
        await Assert.That(box.Height).IsEqualTo(5d);
        await Assert.That(box.ExpandToInclude(Envelope.Empty)).IsEqualTo(box);
        await Assert.That(Envelope.Empty.ExpandToInclude(box)).IsEqualTo(box);
        await Assert.That(box.ExpandToInclude(new Position(20, 20)))
            .IsEqualTo(new(0, 0, 20, 20));
        await Assert.That(Envelope.Empty.Width).IsEqualTo(0d);
    }

    [Test]
    public async Task FeatureCollection_construction_and_add()
    {
#pragma warning disable IDE0028 // exercising the IEnumerable constructor on purpose
        var collection = new FeatureCollection(Sample.Mixed().Features);
#pragma warning restore IDE0028
        collection.Add(new LineString([new(0, 0), new(1, 1)]));
        await Assert.That(collection.Count).IsEqualTo(4);
        await Assert.That(collection.GetBounds().IsEmpty).IsFalse();
    }

    [Test]
    public async Task FeatureCollection_bounds_ignores_null_geometry()
    {
        var collection = new FeatureCollection { new Feature() };
        await Assert.That(collection.GetBounds().IsEmpty).IsTrue();
    }

    [Test]
    public async Task Geometry_z_and_m_flags()
    {
        var z = new Position(0, 0, 1);
        await Assert.That(new LineString([z]).HasZ).IsTrue();
        await Assert.That(new MultiPoint([z]).HasZ).IsTrue();
        await Assert.That(new Polygon([[z, z, z]]).HasZ).IsTrue();
        await Assert.That(new MultiLineString([new([z])]).HasZ).IsTrue();
        await Assert.That(new MultiPolygon([new([[z, z, z]])]).HasZ).IsTrue();
        await Assert.That(new GeometryCollection([new Point(z)]).HasZ).IsTrue();

        var m = new Position(0, 0, null, 9);
        await Assert.That(new LineString([m]).HasM).IsTrue();
        await Assert.That(new MultiPoint([m]).HasM).IsTrue();
        await Assert.That(new Polygon([[m, m]]).HasM).IsTrue();
        await Assert.That(new MultiLineString([new([m])]).HasM).IsTrue();
        await Assert.That(new MultiPolygon([new([[m, m]])]).HasM).IsTrue();
        await Assert.That(new GeometryCollection([new Point(m)]).HasM).IsTrue();
    }

    [Test]
    public async Task Empty_geometries()
    {
        await Assert.That(new LineString([]).IsEmpty).IsTrue();
        await Assert.That(new Polygon([]).IsEmpty).IsTrue();
        await Assert.That(new MultiPoint([]).IsEmpty).IsTrue();
        await Assert.That(new MultiLineString([]).IsEmpty).IsTrue();
        await Assert.That(new MultiPolygon([]).IsEmpty).IsTrue();
        await Assert.That(new GeometryCollection([]).IsEmpty).IsTrue();
        await Assert.That(new LineString([]).GetBounds().IsEmpty).IsTrue();
        await Assert.That(new MultiLineString([]).GetBounds().IsEmpty).IsTrue();
        await Assert.That(new MultiPolygon([]).GetBounds().IsEmpty).IsTrue();
        await Assert.That(new GeometryCollection([]).GetBounds().IsEmpty).IsTrue();
    }

    [Test]
    public async Task Polygon_ring_accessors()
    {
        var polygon = new Polygon(
        [
            [new(0, 0), new(4, 0), new(4, 4), new(0, 0)],
            [new(1, 1), new(2, 1), new(2, 2), new(1, 1)],
        ]);
        await Assert.That(polygon.ExteriorRing!.Count).IsEqualTo(4);
        await Assert.That(polygon.InteriorRings.Count()).IsEqualTo(1);
        await Assert.That(new Polygon([]).ExteriorRing).IsNull();
    }
}
