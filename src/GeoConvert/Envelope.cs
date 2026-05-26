namespace GeoConvert;

/// <summary>
/// An axis-aligned 2D bounding box. An empty envelope has <see cref="IsEmpty"/> true and NaN bounds.
/// </summary>
public readonly record struct Envelope(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Envelope Empty { get; } = new(double.NaN, double.NaN, double.NaN, double.NaN);

    public bool IsEmpty =>
        // Any non-finite component disqualifies the envelope: a partial-NaN bbox (e.g. from a Position
        // with one NaN ordinate) would otherwise look populated and crash the JSON serializers downstream.
        !double.IsFinite(MinX) || !double.IsFinite(MinY) || !double.IsFinite(MaxX) || !double.IsFinite(MaxY);

    public double Width => IsEmpty ? 0 : MaxX - MinX;

    public double Height => IsEmpty ? 0 : MaxY - MinY;

    public Envelope ExpandToInclude(Position position) =>
        ExpandToInclude(position.X, position.Y);

    public Envelope ExpandToInclude(double x, double y)
    {
        if (IsEmpty)
        {
            return new(x, y, x, y);
        }

        return new(
            Math.Min(MinX, x),
            Math.Min(MinY, y),
            Math.Max(MaxX, x),
            Math.Max(MaxY, y));
    }

    public Envelope ExpandToInclude(Envelope other)
    {
        if (other.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        return new(
            Math.Min(MinX, other.MinX),
            Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX),
            Math.Max(MaxY, other.MaxY));
    }
}
