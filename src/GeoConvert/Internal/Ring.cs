/// <summary>Ring orientation helpers (shoelace signed area).</summary>
static class Ring
{
    public static double SignedArea(IReadOnlyList<Position> ring)
    {
        if (ring.Count < 3)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum / 2;
    }

    /// <summary>True when the ring winds clockwise (negative shoelace area in lon/lat space).</summary>
    public static bool IsClockwise(IReadOnlyList<Position> ring) =>
        SignedArea(ring) < 0;

    public static IReadOnlyList<Position> Orient(IReadOnlyList<Position> ring, bool clockwise)
    {
        if (IsClockwise(ring) == clockwise)
        {
            return ring;
        }

        var reversed = new List<Position>(ring);
        reversed.Reverse();
        return reversed;
    }
}
