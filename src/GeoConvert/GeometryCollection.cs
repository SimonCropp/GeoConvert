namespace GeoConvert;

public sealed class GeometryCollection(IReadOnlyList<Geometry> geometries) : Geometry
{
    public IReadOnlyList<Geometry> Geometries { get; } = geometries;

    public override GeometryType Type => GeometryType.GeometryCollection;

    public override bool IsEmpty => Geometries.Count == 0 || Geometries.All(_ => _.IsEmpty);

    public override bool HasZ => Geometries.Any(_ => _.HasZ);

    public override bool HasM => Geometries.Any(_ => _.HasM);

    public override Envelope GetBounds() =>
        Geometries.Aggregate(Envelope.Empty, (bounds, geometry) => bounds.ExpandToInclude(geometry.GetBounds()));
}
