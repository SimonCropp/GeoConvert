namespace GeoConvert;

public sealed class MultiPoint(IReadOnlyList<Position> positions) : Geometry
{
    public IReadOnlyList<Position> Positions { get; } = positions;

    public override GeometryType Type => GeometryType.MultiPoint;

    public override bool IsEmpty => Positions.Count == 0;

    public override bool HasZ => Positions.Any(_ => _.HasZ);

    public override bool HasM => Positions.Any(_ => _.HasM);

    public override Envelope GetBounds() => Bounds.Of(Positions);
}
