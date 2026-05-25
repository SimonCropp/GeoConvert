static class Bounds
{
    public static Envelope Of(IEnumerable<Position> positions)
    {
        var bounds = Envelope.Empty;
        foreach (var position in positions)
        {
            bounds = bounds.ExpandToInclude(position);
        }

        return bounds;
    }
}
