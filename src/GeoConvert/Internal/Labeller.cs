/// <summary>
/// Greedy label placer for the PNG renderer. Each <see cref="TryPlace"/> call centres the requested
/// text on a pixel-space anchor and either accepts it (rendering via <see cref="StrokeFont"/> and
/// tracking the bounding box) or rejects it because it would extend off-canvas or overlap a
/// previously-placed label. No fancy candidate search — the first feature to ask for a spot wins
/// it, and later collisions just drop the loser. That's coarse, but it keeps dense maps readable
/// without a label-engine's worth of code: a real cartographic engine would also try alternate
/// offsets, rotate along lines, and prioritise by feature rank.
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
    /// Tries to draw <paramref name="text"/> centred on (<paramref name="anchorX"/>,
    /// <paramref name="anchorY"/>) in canvas pixel space. Returns true if the label was rendered;
    /// false if it would have extended off the canvas or overlapped a previously-placed label
    /// (including its halo if present).
    /// </summary>
    public bool TryPlace(string text, double anchorX, double anchorY, double size, Rgba color, Rgba? halo)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var (width, _) = StrokeFont.Measure(text, size);
        var unit = size / StrokeFont.CapHeight;

        // Centre the cap height on the anchor — descenders hang below, accent space sits above.
        // That matches what the eye reads as "the middle of the text"; centring the full ink box
        // would visually push caps upward by the descender depth.
        var leftX = anchorX - width / 2;
        var capTopY = anchorY - size / 2;
        var baselineY = capTopY + size;

        // Ink-bounds extents — what could be painted, not just where the caps sit. Descenders on
        // g/j/p/q/y reach DescenderBottom (-4 font units); accents and brackets reach AscenderTop
        // (+16 font units). Including both in the collision box stops a label with descenders
        // (Kingdom) from biting into the row below it (Germany), which the cap-only bbox missed.
        var ascenderRise = (StrokeFont.AscenderTop - StrokeFont.CapHeight) * unit;
        var descenderDrop = -StrokeFont.DescenderBottom * unit;
        var inkTopY = capTopY - ascenderRise;
        var inkBottomY = baselineY + descenderDrop;

        // Halo strokes extend exactly one pixel beyond the foreground stroke on each side
        // (StrokeFont uses haloWidth = strokeWidth + 2). So a 1-pixel collision pad is enough to
        // keep two labels' halos from grazing each other — anything larger steals room from
        // legitimate label placements. The pad does NOT scale with size because the halo's
        // extent past the foreground stroke is a fixed pixel ring regardless of how thick the
        // strokes themselves get.
        var pad = halo.HasValue ? 1d : 0;
        var bx0 = leftX - pad;
        var by0 = inkTopY - pad;
        var bx1 = leftX + width + pad;
        var by1 = inkBottomY + pad;

        // Off-canvas rejection. Any part of the (haloed) bbox sitting outside [0, W) × [0, H)
        // means part of the label would clip — drop it entirely rather than render a cropped word
        // that reads wrong. A real engine would offset the anchor inward; this one just bails.
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

        StrokeFont.Render(canvas, text, leftX, baselineY, size, color, halo);
        placed.Add((bx0, by0, bx1, by1));
        return true;
    }
}
