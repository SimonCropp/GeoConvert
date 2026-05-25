namespace GeoConvert;

/// <summary>
/// A polygon. <see cref="Rings"/>[0] is the exterior ring; any further rings are holes. Each ring is a
/// closed sequence of positions (first and last coincide).
/// </summary>
public sealed class Polygon(IReadOnlyList<IReadOnlyList<Position>> rings) : Geometry
{
    public IReadOnlyList<IReadOnlyList<Position>> Rings { get; } = rings;

    public IReadOnlyList<Position>? ExteriorRing => Rings.Count > 0 ? Rings[0] : null;

    public IEnumerable<IReadOnlyList<Position>> InteriorRings => Rings.Skip(1);

    public override GeometryType Type => GeometryType.Polygon;

    public override bool IsEmpty => Rings.Count == 0 || Rings[0].Count == 0;

    public override bool HasZ => Rings.Any(_ => _.Any(_ => _.HasZ));

    public override bool HasM => Rings.Any(_ => _.Any(_ => _.HasM));

    public override Envelope GetBounds() => Bounds.Of(Rings.SelectMany(_ => _));
}
