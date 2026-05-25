namespace GeoConvert;

public sealed class MultiPolygon(IReadOnlyList<Polygon> polygons) : Geometry
{
    public IReadOnlyList<Polygon> Polygons { get; } = polygons;

    public override GeometryType Type => GeometryType.MultiPolygon;

    public override bool IsEmpty => Polygons.Count == 0 || Polygons.All(_ => _.IsEmpty);

    public override bool HasZ => Polygons.Any(_ => _.HasZ);

    public override bool HasM => Polygons.Any(_ => _.HasM);

    public override Envelope GetBounds() =>
        Polygons.Aggregate(Envelope.Empty, (bounds, polygon) => bounds.ExpandToInclude(polygon.GetBounds()));
}
