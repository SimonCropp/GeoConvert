/// <summary>
/// Lobe geometry for Goode's *interrupted* Homolosine: the conventional 2-north, 4-south split
/// that runs the interrupt meridians through ocean basins so the major continents fall whole
/// inside one lobe. Holds the lobe table and the polygon/polyline clipping needed before
/// projection (so the rasterizer can fill and stroke each lobe's contribution independently
/// without painting across the inter-lobe gaps).
/// </summary>
static class GoodeLobes
{
    /// <summary>One axis-aligned lon/lat rectangle making up part of a lobe. A lobe with two
    /// rects represents a non-rectangular logical region (e.g. an L-shape with a tab or a
    /// notch); the two rects share an edge in lon/lat space, and the renderer projects both
    /// through the lobe's single central meridian so the pieces meet seamlessly on the
    /// canvas.</summary>
    public readonly record struct Rect(double LonMin, double LonMax, double LatMin, double LatMax);

    /// <summary>One logical lobe: a central meridian shared by every sub-rectangle and a
    /// hand-coded perimeter (clockwise corner list in lon/lat) for the outer envelope. The
    /// perimeter is the union outline of the rects — for a single-rect lobe it's the rect's
    /// four corners; for an L-shape it walks the L.</summary>
    public readonly record struct Lobe(
        double CentralMeridian,
        IReadOnlyList<Rect> Rects,
        IReadOnlyList<Position> Perimeter);

    // Goode's interrupted Homolosine with the conventional ocean-meridian cuts, plus a
    // *Greenland cut-out* on the north interruption: at lat ≥ 60° the cut steps from lon=-40°
    // east to lon=-10°, capturing Greenland (lon ≈ -73° to -12°) inside the Americas lobe so
    // it renders adjacent to Canada — Greenland is geographically Canada's neighbour, separated
    // from Europe by the Greenland Sea, so this anchoring reads more naturally than the
    // Eurasian-side variant. Iceland (lon ≈ -22°, lat ≈ 65°) goes with Greenland as a
    // consequence; the cut at -10° leaves continental Europe intact in the eastern lobe.
    public static readonly Lobe[] AllLobes =
    [
        // North west (Americas + Greenland + Iceland) — tab extends *east* above lat=60 to
        // pull Greenland into the Americas lobe.
        new(
            CentralMeridian: -100,
            Rects:
            [
                new(LonMin: -180, LonMax: -40, LatMin:  0, LatMax: 60),
                new(LonMin: -180, LonMax: -10, LatMin: 60, LatMax: 90),
            ],
            Perimeter:
            [
                new(-40, 0), new(-180, 0), new(-180, 90), new(-10, 90), new(-10, 60), new(-40, 60),
            ]),

        // North east (Eurasia + Africa-N) — main rect for lower lat, retracted rect for upper
        // lat (cut moved east from -40° to -10° to make room for Greenland in the west lobe).
        new(
            CentralMeridian: 30,
            Rects:
            [
                new(LonMin: -40, LonMax: 180, LatMin:  0, LatMax: 60),
                new(LonMin: -10, LonMax: 180, LatMin: 60, LatMax: 90),
            ],
            Perimeter:
            [
                new(180, 0), new(-40, 0), new(-40, 60), new(-10, 60), new(-10, 90), new(180, 90),
            ]),

        // South: four single-rectangle lobes covering S-America, S-Africa, Australia, and the
        // Pacific. Central meridians chosen at the centre of each landmass.
        new(
            CentralMeridian: -160,
            Rects: [new(LonMin: -180, LonMax: -100, LatMin: -90, LatMax: 0)],
            Perimeter: [new(-100, 0), new(-180, 0), new(-180, -90), new(-100, -90)]),
        new(
            CentralMeridian: -60,
            Rects: [new(LonMin: -100, LonMax: -20, LatMin: -90, LatMax: 0)],
            Perimeter: [new(-20, 0), new(-100, 0), new(-100, -90), new(-20, -90)]),
        new(
            CentralMeridian: 20,
            Rects: [new(LonMin: -20, LonMax: 80, LatMin: -90, LatMax: 0)],
            Perimeter: [new(80, 0), new(-20, 0), new(-20, -90), new(80, -90)]),
        new(
            CentralMeridian: 140,
            Rects: [new(LonMin: 80, LonMax: 180, LatMin: -90, LatMax: 0)],
            Perimeter: [new(180, 0), new(80, 0), new(80, -90), new(180, -90)]),
    ];

    /// <summary>The lobe a single point belongs to. Walks every sub-rect of every lobe — a
    /// point on a shared boundary (e.g. lon=-40° at lat=30°) lands in the first-matched lobe,
    /// which is fine because both lobes' projections agree along their shared edge.</summary>
    public static Lobe FindLobe(double longitude, double latitude)
    {
        foreach (var lobe in AllLobes)
        {
            foreach (var rect in lobe.Rects)
            {
                if (longitude >= rect.LonMin && longitude <= rect.LonMax &&
                    latitude >= rect.LatMin && latitude <= rect.LatMax)
                {
                    return lobe;
                }
            }
        }

        // Malformed input fell outside [-180, 180] × [-90, 90]; fall back to the lobe whose
        // central meridian is closest in lon so the projection still produces a finite point
        // instead of throwing. The renderer prefers a graceful degraded output over a crash.
        var best = AllLobes[0];
        var bestDistance = double.PositiveInfinity;
        foreach (var lobe in AllLobes)
        {
            // Each lobe is entirely in one hemisphere, so the first rect's lat sign identifies
            // it. Skip lobes on the wrong hemisphere so the fallback picks a sensible neighbour.
            if (latitude >= 0 != lobe.Rects[0].LatMin >= 0)
            {
                continue;
            }

            var distance = Math.Abs(longitude - lobe.CentralMeridian);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = lobe;
            }
        }

        return best;
    }

    /// <summary>True if any of the lobe's sub-rectangles intersects the input bounds. Used to
    /// skip lobes that aren't covered by the requested render extent.</summary>
    public static bool IntersectsBounds(Lobe lobe, Envelope bounds)
    {
        foreach (var rect in lobe.Rects)
        {
            if (rect.LonMax > bounds.MinX && rect.LonMin < bounds.MaxX &&
                rect.LatMax > bounds.MinY && rect.LatMin < bounds.MaxY)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sutherland-Hodgman clip of a polygon ring against the lobe's lon/lat AABB.
    /// Returns the clipped vertices along with a bitmask per vertex indicating which lobe
    /// boundary planes the vertex was introduced by (0 = original input vertex; non-zero =
    /// intersection point on one of the four lobe boundaries). The caller uses the tags to
    /// distinguish *real* polygon edges from clip-edge segments that close the clipped piece
    /// along the lobe boundary — real edges get stroked, clip edges don't, so a clipped
    /// continent reads as one shape rather than several with vertical slice marks inside.</summary>
    public static (List<Position> Vertices, List<int> BoundaryTags) ClipRingWithTags(IReadOnlyList<Position> ring, Rect rect)
    {
        // Each pass is one half-plane; intersection vertices it introduces are tagged with the
        // bit for that boundary. A vertex's tag accumulates across passes only if it gets
        // re-intersected on a subsequent plane, which for axis-aligned planes only happens at
        // the lobe corners — and corner-on-corner edges aren't a real concern.
        var verts = new List<Position>(ring);
        var tags = new List<int>(new int[ring.Count]);
        (verts, tags) = ClipHalfPlaneTagged(verts, tags,
            p => p.X >= rect.LonMin,
            (a, b) => InterpolateToX(a, b, rect.LonMin),
            introducedTag: 1);
        (verts, tags) = ClipHalfPlaneTagged(verts, tags,
            p => p.X <= rect.LonMax,
            (a, b) => InterpolateToX(a, b, rect.LonMax),
            introducedTag: 2);
        (verts, tags) = ClipHalfPlaneTagged(verts, tags,
            p => p.Y >= rect.LatMin,
            (a, b) => InterpolateToY(a, b, rect.LatMin),
            introducedTag: 4);
        (verts, tags) = ClipHalfPlaneTagged(verts, tags,
            p => p.Y <= rect.LatMax,
            (a, b) => InterpolateToY(a, b, rect.LatMax),
            introducedTag: 8);

        // Densify clip edges so the polygon's projected fill follows the lobe boundary's
        // Mollweide curve. The clip edge in lon/lat is a straight line (constant lon or lat)
        // with only two endpoints; projected through the lobe's central meridian, those two
        // points become two pixel-space points and the rasterizer draws a *straight* line
        // between them — but the lobe's actual border curves inward toward the pole. Adding
        // intermediate vertices along the straight lon/lat line lets each be projected
        // individually, so the resulting fill edge traces the lobe's curve and there's no
        // gap between a polygon like Antarctica and the lobe outline.
        return DensifyClipEdges(verts, tags, samplesPerEdge: 16);
    }

    static (List<Position>, List<int>) DensifyClipEdges(List<Position> verts, List<int> tags, int samplesPerEdge)
    {
        var output = new List<Position>(verts.Count);
        var outputTags = new List<int>(tags.Count);
        for (var i = 0; i < verts.Count; i++)
        {
            var current = verts[i];
            var next = verts[(i + 1) % verts.Count];
            var currentTag = tags[i];
            var nextTag = tags[(i + 1) % verts.Count];
            output.Add(current);
            outputTags.Add(currentTag);

            // Only edges where both endpoints share a boundary tag are clip-introduced edges
            // worth densifying — original polygon edges already trace the input geometry as
            // densely as the caller chose.
            var sharedTag = currentTag & nextTag;
            if (sharedTag == 0)
            {
                continue;
            }

            for (var j = 1; j < samplesPerEdge; j++)
            {
                var t = (double)j / samplesPerEdge;
                output.Add(new(
                    current.X + t * (next.X - current.X),
                    current.Y + t * (next.Y - current.Y)));
                outputTags.Add(sharedTag);
            }
        }

        return (output, outputTags);
    }

    /// <summary>Splits a polyline at every lobe boundary it crosses. Each emitted subpath is a
    /// contiguous run of vertices in one lobe (the boundary intersection is inserted at both
    /// ends of the split so the strokes reach all the way to the lobe edge before the gap).</summary>
    public static IEnumerable<(Lobe Lobe, List<Position> Positions)> SubdividePath(IReadOnlyList<Position> positions)
    {
        if (positions.Count < 2)
        {
            yield break;
        }

        var currentLobe = FindLobe(positions[0].X, positions[0].Y);
        var current = new List<Position> { positions[0] };
        for (var i = 1; i < positions.Count; i++)
        {
            var previous = positions[i - 1];
            var next = positions[i];
            var nextLobe = FindLobe(next.X, next.Y);
            if (nextLobe.Equals(currentLobe))
            {
                current.Add(next);
                continue;
            }

            // Crossed a boundary. Insert the boundary point at the end of the current subpath
            // and at the start of the next, so each subpath's stroke runs all the way to its
            // lobe edge. We pick one boundary even if both hemisphere and lon change in the
            // same segment — long segments crossing multiple boundaries are rare in real
            // geodata, and the visual approximation is invisible at typical densities.
            var split = InterpolateToBoundary(previous, next, currentLobe, nextLobe);
            current.Add(split);
            yield return (currentLobe, current);
            current = [split, next];
            currentLobe = nextLobe;
        }

        yield return (currentLobe, current);
    }

    static (List<Position>, List<int>) ClipHalfPlaneTagged(
        IReadOnlyList<Position> ring,
        IReadOnlyList<int> ringTags,
        Func<Position, bool> inside,
        Func<Position, Position, Position> intersect,
        int introducedTag)
    {
        var verts = new List<Position>();
        var tags = new List<int>();
        if (ring.Count == 0)
        {
            return (verts, tags);
        }

        for (var i = 0; i < ring.Count; i++)
        {
            var current = ring[i];
            var previous = ring[(i + ring.Count - 1) % ring.Count];
            var currentInside = inside(current);
            var previousInside = inside(previous);
            if (previousInside != currentInside)
            {
                verts.Add(intersect(previous, current));
                tags.Add(introducedTag);
            }

            if (currentInside)
            {
                verts.Add(current);
                tags.Add(ringTags[i]);
            }
        }

        return (verts, tags);
    }

    /// <summary>Walks a clipped ring and yields the maximal runs of consecutive non-clip edges
    /// as open polylines. An edge from vertex i to vertex (i+1)%n counts as a clip edge when
    /// both endpoints carry an overlapping boundary tag — i.e. both sit on the same lobe-AABB
    /// plane, the hallmark of an edge that S-H added to close the clipped piece along the
    /// lobe boundary rather than something tracing the original polygon.</summary>
    public static IEnumerable<(double X, double Y)[]> BuildStrokeChains((double X, double Y)[] ring, IReadOnlyList<int> tags)
    {
        // Callers (PreparePolygon) already filter rings with < 3 vertices, so the loop body
        // always sees a usable ring.
        var chain = new List<(double X, double Y)>();
        for (var i = 0; i < ring.Length; i++)
        {
            var next = (i + 1) % ring.Length;
            var isClipEdge = (tags[i] & tags[next]) != 0;
            if (chain.Count == 0)
            {
                chain.Add(ring[i]);
            }

            if (isClipEdge)
            {
                if (chain.Count >= 2)
                {
                    yield return chain.ToArray();
                }

                chain = new();
            }
            else
            {
                chain.Add(ring[next]);
            }
        }

        if (chain.Count >= 2)
        {
            yield return chain.ToArray();
        }
    }

    static Position InterpolateToBoundary(Position a, Position b, Lobe lobeA, Lobe lobeB)
    {
        // Different hemispheres → split at the equator. Use the first rect of each lobe to
        // identify hemisphere (every rect of one lobe is in the same hemisphere).
        if (lobeA.Rects[0].LatMin >= 0 != lobeB.Rects[0].LatMin >= 0)
        {
            return InterpolateToY(a, b, 0);
        }

        // Same hemisphere: find a meridian shared between any rect of A and any rect of B that
        // lies between a.X and b.X — that's the boundary the segment crosses. Throws via
        // .First() if no match exists, which would indicate a malformed lobe layout; for our
        // canonical lobes adjacent lobes always share a meridian.
        var lo = Math.Min(a.X, b.X);
        var hi = Math.Max(a.X, b.X);
        var meridian = lobeA.Rects
            .SelectMany(r => new[] { r.LonMin, r.LonMax })
            .First(m => m >= lo && m <= hi &&
                lobeB.Rects.Any(rb => rb.LonMin == m || rb.LonMax == m));
        return InterpolateToX(a, b, meridian);
    }

    static Position InterpolateToX(Position a, Position b, double x)
    {
        // Linear lon/lat interpolation along the segment. The segment can be vertical (a.X ==
        // b.X), but in that case it doesn't cross a vertical boundary, so this overload isn't
        // called with that input — guarded by the half-plane sign-change check.
        var t = (x - a.X) / (b.X - a.X);
        return new(x, a.Y + t * (b.Y - a.Y));
    }

    static Position InterpolateToY(Position a, Position b, double y)
    {
        var t = (y - a.Y) / (b.Y - a.Y);
        return new(a.X + t * (b.X - a.X), y);
    }
}
