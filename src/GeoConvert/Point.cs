namespace GeoConvert;

public sealed class Point(Position coordinate) : Geometry
{
    public Position Coordinate { get; } = coordinate;

    public override GeometryType Type => GeometryType.Point;

    public override bool IsEmpty => double.IsNaN(Coordinate.X) && double.IsNaN(Coordinate.Y);

    public override bool HasZ => Coordinate.HasZ;

    public override bool HasM => Coordinate.HasM;

    public override Envelope GetBounds() =>
        IsEmpty ? Envelope.Empty : new(Coordinate.X, Coordinate.Y, Coordinate.X, Coordinate.Y);

    public Point(double x, double y)
        : this(new(x, y))
    {
    }
}
