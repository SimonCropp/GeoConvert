/// <summary>
/// Greedy label placer for the PNG renderer. Each <see cref="TryPlace"/> call either centres a label
/// on the anchor (for polygon centroids and line midpoints, which are the *interior* of the feature
/// and the label belongs *on* them) or walks an Imhof-style candidate ring around the anchor (for
/// point features, where the anchor is the point itself and the label belongs *beside* the dot,
/// not over it). The candidate ring is the classical 8-position list — upper-right preferred, then
/// upper-left, the two lower corners, the cardinals, the bottom slot last — that Eduard Imhof
/// (1962/1975) and Pinhas Yoeli (1972) tabulated as the preference order for cartographic point
/// labels. First candidate that clears the canvas edge and every previously-placed bbox wins;
/// otherwise the label is dropped. Christensen/Marks/Shieber (1995) proved general point-label
/// placement NP-hard, so a real engine bolts simulated annealing and weighted scoring on top of
/// this ring; here the ring alone is enough to keep dots and labels visually distinct on a
/// typical map without bloating the renderer.
/// </summary>
sealed class Labeller
{
    readonly Canvas canvas;

    // Axis-aligned bounding boxes (inclusive low, exclusive high) of every placed label. A linear
    // scan is fine for the feature counts a single PNG can readably display (a few hundred labels
    // max before the map becomes a smudge); a spatial index would only matter past that.
    readonly List<(double X0, double Y0, double X1, double Y1)> placed = [];

    public Labeller(Canvas canvas) =>
        this.canvas = canvas;

    /// <summary>Number of labels successfully placed so far. Exposed for tests.</summary>
    public int PlacedCount => placed.Count;

    /// <summary>
    /// Tries to draw <paramref name="text"/> near (<paramref name="anchorX"/>,
    /// <paramref name="anchorY"/>) in canvas pixel space. When <paramref name="pointOffset"/> is
    /// zero (default) the label is centred on the anchor — the polygon-centroid / line-midpoint
    /// behaviour. When positive, the anchor is treated as a point feature: the candidate ring is
    /// walked in the Imhof preference order (NE → NW → SE → SW → E → W → N → S) until one fits
    /// off the canvas edges and clear of every previously-placed bbox, or all eight fail and the
    /// label is dropped. The offset is the gap between the point and the *nearer* edge of the
    /// label box, so callers typically pass <c>point-radius + small padding</c> to keep the label
    /// from kissing the dot.
    /// <para>When <paramref name="knockout"/> is non-null, a filled rectangle of that colour is
    /// painted over the label's bbox before the halo/text strokes — the cartographic "mask" style
    /// that erases (or, for a semi-transparent colour, dims) the underlying geometry under the
    /// label. Knockout and <paramref name="halo"/> are independent: typical use is knockout for the
    /// backdrop and halo set to null, but both can coexist.</para>
    /// </summary>
    public bool TryPlace(string text, double anchorX, double anchorY, double size, Rgba color, Rgba? halo, double pointOffset = 0, Rgba? knockout = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var (width, _) = StrokeFont.Measure(text, size);

        if (pointOffset > 0)
        {
            // Imhof preference order: diagonals before cardinals (a diagonal label is less likely
            // to be parallel to a nearby road / coastline and hide it), upper before lower (above
            // a dot reads as "naming" it; below can be confused with the next row's point), and
            // right before left within each pair (left-to-right reading puts the next eye-saccade
            // to the right of the dot). The "bottom" slot is last because labels sagging below a
            // point are the easiest to misattribute. Sequence: NE, NW, SE, SW, E, W, N, S.
            foreach (var (leftX, baselineY) in PointCandidates(anchorX, anchorY, width, size, pointOffset))
            {
                if (TryPlaceAt(text, leftX, baselineY, width, size, color, halo, knockout))
                {
                    return true;
                }
            }

            return false;
        }

        // Centre the cap height on the anchor — descenders hang below, accent space sits above.
        // That matches what the eye reads as "the middle of the text"; centring the full ink box
        // would visually push caps upward by the descender depth.
        var centredLeftX = anchorX - width / 2;
        var centredBaselineY = anchorY + size / 2;
        return TryPlaceAt(text, centredLeftX, centredBaselineY, width, size, color, halo, knockout);
    }

    static IEnumerable<(double LeftX, double BaselineY)> PointCandidates(double anchorX, double anchorY, double width, double size, double offset)
    {
        // For each candidate, leftX is the x-coordinate of the label's left edge and baselineY
        // sits one cap height below the cap top. "offset" is the gap between the point and the
        // nearest edge of the label box, in pixels. For diagonals the label sits offset pixels
        // away in both x and y; for cardinals only along the relevant axis.
        // NE: label below-up-and-right; baseline sits `offset` above the point (cap rises from
        // there).
        yield return (anchorX + offset, anchorY - offset);
        // NW
        yield return (anchorX - offset - width, anchorY - offset);
        // SE: cap top sits `offset` below the point; baseline is one cap height further down.
        yield return (anchorX + offset, anchorY + offset + size);
        // SW
        yield return (anchorX - offset - width, anchorY + offset + size);
        // E (right, vertically centred): same centred-baseline math as the polygon/line case.
        yield return (anchorX + offset, anchorY + size / 2);
        // W
        yield return (anchorX - offset - width, anchorY + size / 2);
        // N (above, horizontally centred)
        yield return (anchorX - width / 2, anchorY - offset);
        // S (below, horizontally centred)
        yield return (anchorX - width / 2, anchorY + offset + size);
    }

    bool TryPlaceAt(string text, double leftX, double baselineY, double width, double size, Rgba color, Rgba? halo, Rgba? knockout)
    {
        var unit = size / StrokeFont.CapHeight;
        var capTopY = baselineY - size;

        // Ink-bounds extents — what could be painted, not just where the caps sit. Descenders on
        // g/j/p/q/y reach DescenderBottom (-4 font units); accents and combining marks reach
        // AscenderTop (+17 font units). Including both in the collision box stops a label with
        // descenders (Kingdom) from biting into the row below it (Germany), which the cap-only
        // bbox missed.
        var ascenderRise = (StrokeFont.AscenderTop - StrokeFont.CapHeight) * unit;
        var descenderDrop = -StrokeFont.DescenderBottom * unit;
        var inkTopY = capTopY - ascenderRise;
        var inkBottomY = baselineY + descenderDrop;

        // Halo strokes extend two pixels beyond the foreground stroke on each side (StrokeFont
        // uses haloWidth = strokeWidth + 4). A 2-pixel collision pad is enough to keep two
        // labels' halos from grazing each other. Knockout draws a solid rect over the same bbox
        // so it needs the pad too — without it the rect would clip glyph descenders on labels
        // whose ink reaches the edge. The pad does NOT scale with size because the halo's extent
        // past the foreground stroke is a fixed pixel ring regardless of how thick the strokes
        // themselves get.
        var pad = halo.HasValue || knockout.HasValue ? 2d : 0;
        var bx0 = leftX - pad;
        var by0 = inkTopY - pad;
        var bx1 = leftX + width + pad;
        var by1 = inkBottomY + pad;

        // Off-canvas rejection. Any part of the (haloed) bbox sitting outside [0, W) × [0, H)
        // means part of the label would clip — drop it entirely rather than render a cropped word
        // that reads wrong.
        if (bx0 < 0 || by0 < 0 || bx1 > canvas.Width || by1 > canvas.Height)
        {
            return false;
        }

        // Greedy overlap rejection against every previously placed bbox.
        foreach (var box in placed)
        {
            if (bx0 < box.X1 && bx1 > box.X0 && by0 < box.Y1 && by1 > box.Y0)
            {
                return false;
            }
        }

        if (knockout is { } knockoutColor)
        {
            // Paint the backdrop before halo + text so both render over (and source-over blend
            // with) the knockout colour rather than the underlying geometry. A semi-transparent
            // knockout reads as a dimming wash; a fully-opaque one fully erases the geometry.
            canvas.FillRect(bx0, by0, bx1, by1, knockoutColor);
        }

        StrokeFont.Render(canvas, text, leftX, baselineY, size, color, halo);
        placed.Add((bx0, by0, bx1, by1));
        return true;
    }
}
