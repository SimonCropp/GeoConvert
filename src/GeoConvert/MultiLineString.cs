namespace GeoConvert;

public sealed class MultiLineString(IReadOnlyList<LineString> lineStrings) : Geometry
{
    public IReadOnlyList<LineString> LineStrings { get; } = lineStrings;

    public override GeometryType Type => GeometryType.MultiLineString;

    public override bool IsEmpty =>
        LineStrings.Count == 0 ||
        LineStrings.All(_ => _.IsEmpty);

    public override bool HasZ => LineStrings.Any(_ => _.HasZ);

    public override bool HasM => LineStrings.Any(_ => _.HasM);

    public override Envelope GetBounds() =>
        LineStrings.Aggregate(Envelope.Empty, (bounds, line) => bounds.ExpandToInclude(line.GetBounds()));
}
