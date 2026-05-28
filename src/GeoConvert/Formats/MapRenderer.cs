namespace GeoConvert;

/// <summary>
/// Renders a <see cref="FeatureCollection"/> to a PNG raster, clipped to a bounding box. This is a
/// write-only export (a PNG cannot be read back into features). Built on a small software rasterizer and
/// a hand-rolled PNG encoder, with no third-party dependencies.
/// </summary>
public static class MapRenderer
{
    // Standard Web Mercator latitude cutoff: ln(tan) blows up at ±90°, and ±85.0511° is where the
    // projected square world meets its longitudinal width — the convention every tile provider uses.
    internal const double WebMercatorMaxLatitude = 85.05112877980659;

    /// <summary>
    /// The conventional <see cref="RenderOptions.Bounds"/> for a Web Mercator world map: longitude spans
    /// the full ±180° and latitude is the ±85.0511° cutoff that makes the projected world a 1:1 square,
    /// matching the layout used by every tiled-map provider. Pass this when rendering a global view
    /// under <see cref="MapProjection.WebMercator"/>; for any subregion just supply real data bounds.
    /// </summary>
    public static Envelope WebMercatorWorldBounds { get; } =
        new(-180, -WebMercatorMaxLatitude, 180, WebMercatorMaxLatitude);

    public static byte[] RenderPng(FeatureCollection features, RenderOptions? options = null) =>
        RenderPng([features], options);

    public static void RenderPng(FeatureCollection features, string path, RenderOptions? options = null) =>
        RenderPng([features], path, options);

    public static void RenderPng(FeatureCollection features, Stream stream, RenderOptions? options = null) =>
        RenderPng([features], stream, options);

    /// <summary>
    /// Renders multiple <see cref="FeatureCollection"/>s as stacked top-level layers, in order — the
    /// first paints under, the last on top under source-over blending. Each collection is treated as
    /// its own layer for <see cref="RenderOptions.LayerStyle"/> (and any nested
    /// <see cref="FeatureCollection.Children"/> still recurse from there). When
    /// <see cref="RenderOptions.Bounds"/> is null the rendered extent is the union of all input
    /// collections.
    /// </summary>
    public static byte[] RenderPng(IReadOnlyList<FeatureCollection> layers, RenderOptions? options = null)
    {
        options ??= new();
        var bounds = Validate(layers, options);
        using var memory = new MemoryStream();
        Render(layers, memory, options, bounds);
        return memory.ToArray();
    }

    public static void RenderPng(IReadOnlyList<FeatureCollection> layers, string path, RenderOptions? options = null)
    {
        options ??= new();
        // Validate before File.Create so a throw leaves the destination untouched instead of stranding
        // a 0-byte file. Mid-render stream failures (disk full, etc.) can still leave a partial file,
        // but those are unrecoverable I/O errors where a partial file is the conventional signal.
        var bounds = Validate(layers, options);
        using var stream = File.Create(path);
        Render(layers, stream, options, bounds);
    }

    public static void RenderPng(IReadOnlyList<FeatureCollection> layers, Stream stream, RenderOptions? options = null)
    {
        options ??= new();
        var bounds = Validate(layers, options);
        Render(layers, stream, options, bounds);
    }

    static Envelope Validate(IReadOnlyList<FeatureCollection> layers, RenderOptions options)
    {
        var bounds = options.Bounds ?? UnionBounds(layers);
        if (bounds.IsEmpty)
        {
            throw new GeoConvertException(
                "Cannot render PNG: the features is empty. Provide RenderOptions.Bounds.");
        }

        if (options.Width <= 0)
        {
            throw new GeoConvertException("RenderOptions.Width must be positive.");
        }

        return bounds;
    }

    static Envelope UnionBounds(IReadOnlyList<FeatureCollection> layers)
    {
        var bounds = Envelope.Empty;
        foreach (var layer in layers)
        {
            bounds = bounds.ExpandToInclude(layer.GetBounds());
        }

        return bounds;
    }

    static void Render(IReadOnlyList<FeatureCollection> layers, Stream stream, RenderOptions options, Envelope bounds)
    {
        var projection = new Projection(bounds, options);
        var canvas = new Canvas(projection.Width, projection.Height, options.Background);

        // StrokeAutoScale: derive a multiplier from the implicit zoom (canvas/bbox ratio) so the
        // same scene rendered at a tighter bbox or bigger canvas gets proportionally thicker
        // strokes, matching what tile-map stylesheets do across zoom levels. When the flag is
        // off, the multiplier is 1.0 — the threading is the same in both cases so there's no
        // branch on every feature.
        var strokeMultiplier = options.StrokeAutoScale ? ComputeStrokeMultiplier(canvas, bounds) : 1.0;

        if (options.Ocean is { } ocean)
        {
            // Paint the projection envelope first so every feature layer renders on top of it. For
            // Goode this fills each lobe with the ocean colour, leaving the inter-lobe gaps as the
            // canvas background — that's what makes the projection's lobed shape pop visually.
            foreach (var ring in projection.GetWorldEnvelopeRings())
            {
                canvas.FillPolygon([ring], ocean);
            }

            // Outline each lobe with the regular stroke colour so the envelope reads as a clear
            // border around the world even where the inside is bare ocean. For Goode the equator
            // edge is intentionally omitted from the stroke (otherwise the north and south lobes'
            // top/bottom edges would double up into a thick horizontal line bisecting the map).
            foreach (var chain in projection.GetWorldEnvelopeStrokes())
            {
                for (var i = 0; i + 1 < chain.Length; i++)
                {
                    canvas.StrokeLine(chain[i].X, chain[i].Y, chain[i + 1].X, chain[i + 1].Y, options.StrokeWidth * strokeMultiplier, options.Stroke);
                }
            }
        }

        foreach (var layer in layers)
        {
            DrawLayer(canvas, layer, projection, options, strokeMultiplier);
        }

        // Labels run after every geometry pass so they sit on top of all fills and strokes —
        // burying a label under a later layer's fill would defeat the point. A single Labeller is
        // shared across every layer so collisions are global: a child layer's label can't overlap a
        // parent layer's label, even though their geometry passes paint independently.
        var labeller = new Labeller(canvas);
        foreach (var layer in layers)
        {
            DrawLabels(layer, projection, options, labeller, strokeMultiplier);
        }

        Png.Write(stream, canvas.Pixels, canvas.Width, canvas.Height, options.Compression);
    }

    // Pre-order: a layer paints its own features first, then recurses into its children. Source-over
    // blending means whatever paints last sits on top, so children naturally appear over their parent
    // — pick layer styles via RenderOptions.LayerStyle to keep them visually distinct.
    static void DrawLayer(Canvas canvas, FeatureCollection layer, Projection projection, RenderOptions options, double strokeMultiplier)
    {
        var style = Resolve(options.LayerStyle?.Invoke(layer), options, strokeMultiplier);
        foreach (var feature in layer.Features)
        {
            if (feature.Geometry is { } geometry)
            {
                Draw(canvas, geometry, projection, style);
            }
        }

        foreach (var child in layer.Children)
        {
            DrawLayer(canvas, child, projection, options, strokeMultiplier);
        }
    }

    // Collapses the user-facing LayerStyle (any subset of overrides) into the four concrete values the
    // rasterizer needs, falling back to RenderOptions defaults for each null property independently.
    // The strokeMultiplier multiplies both StrokeWidth and PointRadius — it's 1.0 unless
    // StrokeAutoScale is on, in which case it follows the zoom-derived factor from Render().
    static ResolvedStyle Resolve(LayerStyle? overrides, RenderOptions options, double strokeMultiplier) =>
        new(
            overrides?.Stroke ?? options.Stroke,
            overrides?.Fill ?? options.Fill,
            (overrides?.StrokeWidth ?? options.StrokeWidth) * strokeMultiplier,
            (overrides?.PointRadius ?? options.PointRadius) * strokeMultiplier);

    /// <summary>
    /// Derives a stroke-width multiplier from the canvas/bbox ratio — the static-render equivalent
    /// of tile-map zoom-aware styling. Uses the smaller of the horizontal and vertical
    /// pixels-per-degree (the axis that actually fits the rendered extent), converts to an
    /// implicit zoom via the tile-map convention (zoom = log2(width-at-360° / 256)), then grows
    /// the multiplier by 1.15× per zoom level with zoom 10 (country-scale) as the
    /// multiplier-of-1 baseline. Clamped to [0.25, 6] so a degenerate bbox doesn't blow the
    /// multiplier to infinity or zero.
    /// </summary>
    static double ComputeStrokeMultiplier(Canvas canvas, Envelope bounds)
    {
        var pixelsPerDegree = Math.Min(canvas.Width / bounds.Width, canvas.Height / bounds.Height);
        var zoom = Math.Log2(pixelsPerDegree * 360.0 / 256);
        var multiplier = Math.Pow(1.15, zoom - 10);
        return Math.Clamp(multiplier, 0.25, 6);
    }

    static void Draw(Canvas canvas, Geometry geometry, Projection projection, ResolvedStyle style)
    {
        switch (geometry)
        {
            case Point point:
                var (px, py) = projection.ToPixel(point.Coordinate);
                canvas.FillDisc(px, py, style.PointRadius, style.Stroke);
                break;
            case MultiPoint multiPoint:
                foreach (var position in multiPoint.Positions)
                {
                    var (x, y) = projection.ToPixel(position);
                    canvas.FillDisc(x, y, style.PointRadius, style.Stroke);
                }

                break;
            case LineString line:
                StrokePath(canvas, line.Positions, projection, style);
                break;
            case MultiLineString multiLine:
                foreach (var child in multiLine.LineStrings)
                {
                    StrokePath(canvas, child.Positions, projection, style);
                }

                break;
            case Polygon polygon:
                DrawPolygon(canvas, polygon, projection, style);
                break;
            case MultiPolygon multiPolygon:
                foreach (var child in multiPolygon.Polygons)
                {
                    DrawPolygon(canvas, child, projection, style);
                }

                break;
            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    Draw(canvas, child, projection, style);
                }

                break;
        }
    }

    static void DrawPolygon(Canvas canvas, Polygon polygon, Projection projection, ResolvedStyle style)
    {
        // PreparePolygon yields one batch per output piece — for Goode that's one per lobe with
        // content. Fill uses the clipped closed rings (so the lobe-boundary closure participates
        // in even-odd fill), while strokes use the open polyline chains that omit any
        // clip-boundary edges — otherwise a clipped continent like Antarctica would render with a
        // dark vertical stroke down each lobe meridian, reading as a thin slice through the shape.
        foreach (var batch in projection.PreparePolygon(polygon.Rings))
        {
            canvas.FillPolygon(batch.Fill, style.Fill);
            foreach (var chain in batch.Strokes)
            {
                StrokeRing(canvas, chain, style);
            }
        }
    }

    static void StrokePath(Canvas canvas, IReadOnlyList<Position> positions, Projection projection, ResolvedStyle style)
    {
        // PrepareLine yields one subpath per lobe the line crosses (just the input line itself for
        // non-interrupted projections). Each subpath stays in one lobe so consecutive vertices
        // never straddle the interrupt gap.
        foreach (var subpath in projection.PrepareLine(positions))
        {
            for (var i = 0; i + 1 < subpath.Length; i++)
            {
                canvas.StrokeLine(subpath[i].X, subpath[i].Y, subpath[i + 1].X, subpath[i + 1].Y, style.StrokeWidth, style.Stroke);
            }
        }
    }

    static void StrokeRing(Canvas canvas, (double X, double Y)[] ring, ResolvedStyle style)
    {
        for (var i = 0; i + 1 < ring.Length; i++)
        {
            canvas.StrokeLine(ring[i].X, ring[i].Y, ring[i + 1].X, ring[i + 1].Y, style.StrokeWidth, style.Stroke);
        }
    }

    // Pre-order walk matching DrawLayer's order: a parent's labels are placed before its children's,
    // so on collision the higher-up-the-tree label wins. That mirrors the typical cartographic
    // hierarchy (country labels outrank state labels outrank city labels) when callers build their
    // layer tree from coarse-to-fine.
    static void DrawLabels(FeatureCollection layer, Projection projection, RenderOptions options, Labeller labeller, double strokeMultiplier)
    {
        var style = ResolveLabel(options.LayerStyle?.Invoke(layer), options, strokeMultiplier);
        if (style.Label != null)
        {
            // Process features highest-priority-first within the layer. When the caller provided
            // a priority callback, it decides — typical pattern is to pull from a feature
            // property (population, label-rank, etc.) or capture a lookup table in the closure.
            // Without one, fall back to the default geometric rule: polygon area / line length /
            // points last, so on overlap the bigger feature anchors its label first. Greedy
            // collision then drops the loser. List.Sort is a stable sort in .NET, so ties
            // preserve file order — important so a uniformly-priority layer doesn't get its
            // labels shuffled in ways the caller didn't ask for.
            var priorityFn = style.Priority ?? (Func<Feature, double>)(f => LabelPriority(f.Geometry));
            var sorted = new List<Feature>(layer.Features);
            sorted.Sort((a, b) => priorityFn(b).CompareTo(priorityFn(a)));
            foreach (var feature in sorted)
            {
                if (feature.Geometry is not { } geometry)
                {
                    continue;
                }

                var text = style.Label(feature);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (ComputeAnchor(geometry, projection) is { } anchor)
                {
                    // Point anchors get an Imhof candidate ring around the dot — pointOffset is
                    // the gap between the dot edge and the nearer label edge. PointRadius reads
                    // straight out of the resolved style (already × strokeMultiplier), plus a
                    // small constant pad so the label doesn't kiss the dot. Polygon and line
                    // anchors pass pointOffset=0 → Labeller centres the label on the anchor,
                    // which is what the interior of a feature should do.
                    var pointOffset = anchor.Kind == AnchorKind.Point ? style.PointRadius + 2 : 0;
                    labeller.TryPlace(text, anchor.X, anchor.Y, style.Size, style.Color, style.Halo, pointOffset);
                }
            }
        }

        foreach (var child in layer.Children)
        {
            DrawLabels(child, projection, options, labeller, strokeMultiplier);
        }
    }

    // Relative "how important is this label" score used to order placement within a layer. Computed
    // in lon/lat so it's projection-independent — the absolute number is meaningless, only the
    // ordering matters. Polygon area trumps line length trumps point (constant 0) on the
    // assumption that bigger map features carry more important names; that aligns with the
    // common cartographic convention of country > state > city. GeometryCollection takes the max
    // of its children so a country represented as Polygon+annotations still ranks like a country.
    static double LabelPriority(Geometry? geometry)
    {
        switch (geometry)
        {
            case Polygon polygon:
                return polygon.Rings.Count == 0 ? 0 : Math.Abs(Ring.SignedArea(polygon.Rings[0]));
            case MultiPolygon multiPolygon:
                var total = 0d;
                foreach (var p in multiPolygon.Polygons)
                {
                    if (p.Rings.Count > 0)
                    {
                        total += Math.Abs(Ring.SignedArea(p.Rings[0]));
                    }
                }

                return total;
            case LineString line:
                return PathLength(line.Positions);
            case MultiLineString multiLine:
                var length = 0d;
                foreach (var l in multiLine.LineStrings)
                {
                    length += PathLength(l.Positions);
                }

                return length;
            case GeometryCollection collection:
                var best = 0d;
                foreach (var child in collection.Geometries)
                {
                    var priority = LabelPriority(child);
                    if (priority > best)
                    {
                        best = priority;
                    }
                }

                return best;
            default:
                return 0;
        }
    }

    static double PathLength(IReadOnlyList<Position> positions)
    {
        var total = 0d;
        for (var i = 0; i + 1 < positions.Count; i++)
        {
            total += SegmentLength(positions[i], positions[i + 1]);
        }

        return total;
    }

    // Mirrors Resolve for the label knobs: per-layer overrides take precedence, falling back to the
    // RenderOptions defaults independently per property. Label itself can be left null on the layer
    // to inherit the options-wide default (the typical "label every layer using this property" case).
    // PointRadius is folded in (multiplied by strokeMultiplier exactly as the geometry pass does it)
    // so DrawLabels can size the Imhof candidate ring's offset to clear the dot the renderer drew.
    static ResolvedLabelStyle ResolveLabel(LayerStyle? overrides, RenderOptions options, double strokeMultiplier) =>
        new(
            overrides?.Label ?? options.Label,
            overrides?.LabelSize ?? options.LabelSize,
            overrides?.LabelColor ?? options.LabelColor,
            overrides?.LabelHalo ?? options.LabelHalo,
            overrides?.LabelPriority ?? options.LabelPriority,
            (overrides?.PointRadius ?? options.PointRadius) * strokeMultiplier);

    // Pixel-space anchor for a label, paired with its kind so DrawLabels knows whether to centre
    // the label (Area: polygon centroid / line midpoint) or walk the Imhof ring around it (Point:
    // dot, multi-point first vertex). Polygons use the signed-area-weighted centroid of their
    // outer ring; lines use the arclength midpoint; multi-* picks the largest sub-piece (so a
    // multi-polygon country like New Zealand labels on the North Island, not Stewart Island).
    // For non-linear projections (Lambert, Goode) the centroid is computed in lon/lat then
    // projected — strictly that's not the projected centroid, but it's the right ballpark for
    // label placement at this fidelity. GeometryCollection descends into its first member with a
    // usable anchor and inherits that child's kind.
    static AnchorPoint? ComputeAnchor(Geometry geometry, Projection projection)
    {
        switch (geometry)
        {
            case Point point:
            {
                var (px, py) = projection.ToPixel(point.Coordinate);
                return new AnchorPoint(px, py, AnchorKind.Point);
            }
            case MultiPoint multiPoint:
                if (multiPoint.Positions.Count == 0)
                {
                    return null;
                }
                var (mx, my) = projection.ToPixel(multiPoint.Positions[0]);
                return new AnchorPoint(mx, my, AnchorKind.Point);
            case LineString line:
                return LineAnchor(line.Positions, projection) is { } lineAnchor
                    ? new AnchorPoint(lineAnchor.X, lineAnchor.Y, AnchorKind.Area)
                    : null;
            case MultiLineString multiLine:
                return LongestLineAnchor(multiLine.LineStrings, projection) is { } multiLineAnchor
                    ? new AnchorPoint(multiLineAnchor.X, multiLineAnchor.Y, AnchorKind.Area)
                    : null;
            case Polygon polygon:
                if (polygon.Rings.Count == 0)
                {
                    return null;
                }
                return PolygonAnchor(polygon.Rings[0], projection) is { } polygonAnchor
                    ? new AnchorPoint(polygonAnchor.X, polygonAnchor.Y, AnchorKind.Area)
                    : null;
            case MultiPolygon multiPolygon:
                return LargestPolygonAnchor(multiPolygon.Polygons, projection) is { } multiPolygonAnchor
                    ? new AnchorPoint(multiPolygonAnchor.X, multiPolygonAnchor.Y, AnchorKind.Area)
                    : null;
            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    if (ComputeAnchor(child, projection) is { } anchor)
                    {
                        return anchor;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    static (double X, double Y)? LineAnchor(IReadOnlyList<Position> positions, Projection projection)
    {
        if (positions.Count == 0)
        {
            return null;
        }

        if (positions.Count == 1)
        {
            return projection.ToPixel(positions[0]);
        }

        // Total length first; midpoint is the position at half the cumulative arclength. Computed
        // in lon/lat — for non-linear projections the projected midpoint of a long line could drift
        // off the line slightly, but for the per-segment lengths typical of real geodata the
        // difference is invisible against the label-collision tolerance.
        var total = 0.0;
        for (var i = 0; i + 1 < positions.Count; i++)
        {
            total += SegmentLength(positions[i], positions[i + 1]);
        }

        if (total == 0)
        {
            // All vertices coincide — treat as a point. Without this, the search loop would
            // divide by zero on the first segment.
            return projection.ToPixel(positions[0]);
        }

        var target = total / 2;
        var accum = 0.0;
        // Always returns: the final iteration's `accum >= target` (after summing all segments) is
        // accum == total >= total/2 = target, and the `i + 2 == positions.Count` clause handles
        // floating-point drift where the cumulative sum can fall an ulp short of `total`. So the
        // loop is guaranteed to hit its return on or before the last segment.
        for (var i = 0; ; i++)
        {
            var segment = SegmentLength(positions[i], positions[i + 1]);
            accum += segment;
            if (accum >= target || i + 2 == positions.Count)
            {
                // `segment > 0` here: the only path that could reach this return with a zero-length
                // segment is the `accum >= target` branch fired on a zero-length segment after
                // accum was already < target — impossible, since a zero-length segment doesn't
                // change accum. The clamp covers the FP-drift fall-through where the computed t
                // could land an ulp outside [0, 1].
                var t = Math.Clamp((target - (accum - segment)) / segment, 0, 1);
                var from = positions[i];
                var to = positions[i + 1];
                return projection.ToPixel(new(from.X + t * (to.X - from.X), from.Y + t * (to.Y - from.Y)));
            }
        }
    }

    static (double X, double Y)? LongestLineAnchor(IReadOnlyList<LineString> lines, Projection projection)
    {
        LineString? longest = null;
        var longestLength = -1.0;
        foreach (var line in lines)
        {
            var length = 0.0;
            for (var i = 0; i + 1 < line.Positions.Count; i++)
            {
                length += SegmentLength(line.Positions[i], line.Positions[i + 1]);
            }

            if (length > longestLength)
            {
                longestLength = length;
                longest = line;
            }
        }

        return longest == null ? null : LineAnchor(longest.Positions, projection);
    }

    static (double X, double Y)? PolygonAnchor(IReadOnlyList<Position> ring, Projection projection)
    {
        if (ring.Count < 3)
        {
            return null;
        }

        // Signed-area-weighted centroid (the standard shoelace centroid). Handles closed rings
        // (first == last) and unclosed equally because the duplicate-vertex edge contributes a
        // zero cross product. For a self-intersecting or zero-area ring the formula collapses;
        // fall back to the arithmetic mean of vertices so we still emit *some* anchor — placing
        // the label even slightly off is better than dropping it silently for a malformed input.
        double cx = 0;
        double cy = 0;
        double areaSum = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            var cross = p.X * q.Y - q.X * p.Y;
            areaSum += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }

        if (Math.Abs(areaSum) < 1e-12)
        {
            double sumX = 0;
            double sumY = 0;
            foreach (var position in ring)
            {
                sumX += position.X;
                sumY += position.Y;
            }

            return projection.ToPixel(new(sumX / ring.Count, sumY / ring.Count));
        }

        return projection.ToPixel(new(cx / (3 * areaSum), cy / (3 * areaSum)));
    }

    static (double X, double Y)? LargestPolygonAnchor(IReadOnlyList<Polygon> polygons, Projection projection)
    {
        Polygon? largest = null;
        var largestArea = -1.0;
        foreach (var polygon in polygons)
        {
            if (polygon.Rings.Count == 0)
            {
                continue;
            }

            // Absolute signed area — orientation only signals winding, not size.
            var area = Math.Abs(Ring.SignedArea(polygon.Rings[0]));
            if (area > largestArea)
            {
                largestArea = area;
                largest = polygon;
            }
        }

        return largest == null ? null : PolygonAnchor(largest.Rings[0], projection);
    }

    static double SegmentLength(Position a, Position b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    readonly record struct ResolvedStyle(Rgba Stroke, Rgba Fill, double StrokeWidth, double PointRadius);

    readonly record struct ResolvedLabelStyle(Func<Feature, string?>? Label, double Size, Rgba Color, Rgba? Halo, Func<Feature, double>? Priority, double PointRadius);

    // Whether a label's anchor came from a point feature (anchor IS the feature; label should sit
    // beside the dot, walking the Imhof candidate ring) or from a polygon/line interior (anchor is
    // the centroid / midpoint; label should sit ON it). GeometryCollection inherits its kind from
    // the first child that yields a usable anchor.
    enum AnchorKind { Point, Area }

    readonly record struct AnchorPoint(double X, double Y, AnchorKind Kind);

    /// <summary>One projected polygon piece: closed rings to fill (even-odd) plus the open
    /// polylines to stroke. Fill and Strokes diverge for interrupted Goode, where the clipped
    /// fill ring includes synthetic edges along the lobe boundary that the strokes omit.</summary>
    readonly record struct PolygonBatch((double X, double Y)[][] Fill, (double X, double Y)[][] Strokes);

    /// <summary>
    /// Maps longitude/latitude into pixel space: first through the chosen <see cref="MapProjection"/>
    /// (planar coords), then a uniform scale that fits the projected extent into the canvas, centered,
    /// with the Y axis flipped.
    /// </summary>
    sealed class Projection
    {
        MapProjection kind;
        Envelope inputBounds;
        Envelope projectedBounds;
        double scale;
        double offsetX;
        double offsetY;
        LambertParameters? lambert;

        public Projection(Envelope bounds, RenderOptions options)
        {
            inputBounds = bounds;
            kind = Resolve(options.Projection, bounds);
            // Lambert's per-bounds parameters (standard parallels, reference origin) are derived once
            // from the input envelope; every ProjectPoint call reuses them. If the bounds degenerate to
            // a cone-flattening case (equator-symmetric or zero-height latitude span), the projection
            // silently falls back to PlateCarree so the renderer still produces a sensible image.
            if (kind == MapProjection.Lambert)
            {
                lambert = LambertParameters.TryFrom(bounds);
                if (lambert == null)
                {
                    kind = MapProjection.PlateCarree;
                }
            }

            projectedBounds = ProjectEnvelope(bounds);
            var boundsWidth = projectedBounds.Width > 0 ? projectedBounds.Width : 1;
            var boundsHeight = projectedBounds.Height > 0 ? projectedBounds.Height : 1;

            Width = options.Width;
            Height = options.Height > 0
                ? options.Height
                : Math.Max(1, (int)Math.Round(options.Width * boundsHeight / boundsWidth));

            var drawWidth = Math.Max(1, Width - 2 * options.Padding);
            var drawHeight = Math.Max(1, Height - 2 * options.Padding);
            scale = Math.Min(drawWidth / boundsWidth, drawHeight / boundsHeight);
            offsetX = (Width - boundsWidth * scale) / 2;
            offsetY = (Height - boundsHeight * scale) / 2;
        }

        public int Width { get; }

        public int Height { get; }

        public (double X, double Y) ToPixel(Position position)
        {
            var (projectedX, projectedY) = ProjectPoint(position.X, position.Y);
            return ToPixelFromProjected(projectedX, projectedY);
        }

        public (double X, double Y)[] ToPixels(IReadOnlyList<Position> positions)
        {
            var result = new (double X, double Y)[positions.Count];
            for (var i = 0; i < positions.Count; i++)
            {
                result[i] = ToPixel(positions[i]);
            }

            return result;
        }

        /// <summary>
        /// One batch per output piece. For non-interrupted projections that's a single batch
        /// holding every input ring projected as-is; for <see cref="MapProjection.Goode"/> each
        /// input ring is clipped to each lobe's lon/lat bounds and the non-empty results are
        /// projected through that lobe's central meridian — one batch per lobe with content.
        /// <para>
        /// Each batch separates <see cref="PolygonBatch.Fill"/> (closed rings, for the rasterizer's
        /// even-odd fill) from <see cref="PolygonBatch.Strokes"/> (open polylines, with clip-edge
        /// segments removed for Goode so the stroke doesn't paint a visible "slice" along the lobe
        /// boundary where a continent was cut).
        /// </para>
        /// </summary>
        public IEnumerable<PolygonBatch> PreparePolygon(IReadOnlyList<IReadOnlyList<Position>> rings)
        {
            if (kind != MapProjection.Goode)
            {
                var pixels = rings.Select(ToPixels).ToArray();
                // For non-interrupted projections fill and stroke trace the same rings — no
                // clipping happens, so every edge is "real".
                yield return new(pixels, pixels);
                yield break;
            }

            foreach (var lobe in GoodeLobes.AllLobes)
            {
                var fills = new List<(double X, double Y)[]>(rings.Count);
                var strokes = new List<(double X, double Y)[]>();
                foreach (var ring in rings)
                {
                    // Clip the ring against *each* sub-rectangle of the lobe and project every
                    // non-empty piece. Multi-rect lobes (the Greenland cut-out shape) emit
                    // multiple pieces that share a central meridian, so the pieces meet
                    // seamlessly in projected space. Internal seams between pieces are not
                    // stroked because both endpoints share a boundary tag.
                    foreach (var rect in lobe.Rects)
                    {
                        var (vertices, tags) = GoodeLobes.ClipRingWithTags(ring, rect);
                        if (vertices.Count < 3)
                        {
                            // S-H can leave a sub-ring with <3 vertices if the polygon just
                            // grazes the rect; skip those — FillPolygon would draw a degenerate
                            // sliver and the stroke chains would emit zero-length edges.
                            continue;
                        }

                        var pixelRing = ToPixelsInLobe(vertices, lobe);
                        fills.Add(pixelRing);
                        foreach (var chain in GoodeLobes.BuildStrokeChains(pixelRing, tags))
                        {
                            strokes.Add(chain);
                        }
                    }
                }

                if (fills.Count > 0)
                {
                    yield return new(fills.ToArray(), strokes.ToArray());
                }
            }
        }

        /// <summary>
        /// One pixel subpath per lobe the input line crosses. For non-interrupted projections the
        /// line is yielded as a single projected subpath; for Goode, the line is split at every
        /// hemisphere and lon-lobe boundary it crosses so each emitted subpath stays inside one
        /// lobe and the stroke doesn't jump across an interrupt.
        /// </summary>
        public IEnumerable<(double X, double Y)[]> PrepareLine(IReadOnlyList<Position> positions)
        {
            if (kind != MapProjection.Goode)
            {
                yield return ToPixels(positions);
                yield break;
            }

            foreach (var subpath in GoodeLobes.SubdividePath(positions))
            {
                yield return ToPixelsInLobe(subpath.Positions, subpath.Lobe);
            }
        }

        /// <summary>
        /// The projection's world envelope as one or more closed rings in pixel space — what
        /// <see cref="RenderOptions.Ocean"/> paints under the features. For
        /// <see cref="MapProjection.Goode"/> that's six lobes; for the rectangular projections
        /// it's the input bounds (which for a non-linear projection like Lambert still curves on
        /// the canvas). Each ring is densely sampled along the input perimeter so non-linear
        /// projections capture the curvature instead of cutting corners.
        /// </summary>
        public IEnumerable<(double X, double Y)[]> GetWorldEnvelopeRings()
        {
            if (kind == MapProjection.Goode)
            {
                foreach (var lobe in GoodeLobes.AllLobes)
                {
                    // Skip lobes the caller's bounds doesn't reach — a north-only render shouldn't
                    // paint the southern lobes' envelopes.
                    if (GoodeLobes.IntersectsBounds(lobe, inputBounds))
                    {
                        yield return SampleLobePerimeter(lobe, samplesPerEdge: 32, openChain: false);
                    }
                }

                yield break;
            }

            // Non-Goode: the input bounds *is* the world envelope. Sampling matters for projections
            // whose perimeter curves on the canvas (Lambert, WebMercator); for PlateCarree the four
            // corners would suffice, but sampling at 16 costs nothing and keeps the code uniform.
            yield return SampleEnvelopePerimeter(inputBounds, 16, ProjectPoint);
        }

        /// <summary>
        /// The projection's world envelope as open polylines suitable for stroking the outer
        /// border. For Goode each lobe's <c>lat=0</c> edge is omitted, so the equator doesn't
        /// render as a thick horizontal line bisecting the map — north and south lobes' top/bottom
        /// edges sit on the same projected y at the equator, and stroking both would double up.
        /// For other projections the closed ring is wrapped back to its first vertex so the
        /// stroke loops.
        /// </summary>
        public IEnumerable<(double X, double Y)[]> GetWorldEnvelopeStrokes()
        {
            if (kind == MapProjection.Goode)
            {
                foreach (var lobe in GoodeLobes.AllLobes)
                {
                    if (GoodeLobes.IntersectsBounds(lobe, inputBounds))
                    {
                        yield return SampleLobePerimeter(lobe, samplesPerEdge: 32, openChain: true);
                    }
                }

                yield break;
            }

            // Non-Goode: the closed envelope, wrapped back to the first vertex so the stroke loops.
            foreach (var ring in GetWorldEnvelopeRings())
            {
                var closed = new (double X, double Y)[ring.Length + 1];
                Array.Copy(ring, closed, ring.Length);
                closed[^1] = ring[0];
                yield return closed;
            }
        }

        /// <summary>Walks the lobe's hand-coded perimeter clockwise in lon/lat, densely sampling
        /// each edge and projecting through the lobe's central meridian. For an open chain (the
        /// border stroke), the lat=0 equator edge is skipped — north and south lobes share that
        /// edge, so stroking it would double the equator line.</summary>
        (double X, double Y)[] SampleLobePerimeter(GoodeLobes.Lobe lobe, int samplesPerEdge, bool openChain)
        {
            var perimeter = lobe.Perimeter;
            var n = perimeter.Count;

            // For an open stroke chain, find the equator edge and start walking AFTER it. This
            // gives one contiguous chain of (n-1) edges in our lobe layouts (each lobe has
            // exactly one edge at lat=0). LINQ .First throws if no equator edge is present, which
            // would mean a malformed perimeter — preferable to silently rendering wrong output.
            var startEdge = 0;
            var edgeCount = n;
            if (openChain)
            {
                var equatorEdge = Enumerable.Range(0, n)
                    .First(i => perimeter[i].Y == 0 && perimeter[(i + 1) % n].Y == 0);
                startEdge = (equatorEdge + 1) % n;
                edgeCount = n - 1;
            }

            var points = new List<(double X, double Y)>(edgeCount * samplesPerEdge + 1);
            for (var k = 0; k < edgeCount; k++)
            {
                var edgeIdx = (startEdge + k) % n;
                var from = perimeter[edgeIdx];
                var to = perimeter[(edgeIdx + 1) % n];
                // Emit samplesPerEdge points per edge (t = 0/N, 1/N, ..., (N-1)/N) — the next
                // edge's first sample picks up the endpoint. For the final edge of an open chain
                // we also emit the endpoint so the polyline reaches the lobe's final corner.
                var lastEdgeOfOpenChain = openChain && k == edgeCount - 1;
                var max = lastEdgeOfOpenChain ? samplesPerEdge : samplesPerEdge - 1;
                for (var j = 0; j <= max; j++)
                {
                    var t = (double)j / samplesPerEdge;
                    var lon = from.X + t * (to.X - from.X);
                    var lat = from.Y + t * (to.Y - from.Y);
                    var (px, py) = ProjectGoodeInLobe(lon, lat, lobe);
                    points.Add(ToPixelFromProjected(px, py));
                }
            }

            return points.ToArray();
        }

        (double X, double Y)[] SampleEnvelopePerimeter(Envelope region, int samplesPerEdge, Func<double, double, (double X, double Y)> project) =>
            SampleEnvelopePerimeter((region.MinX, region.MaxX, region.MinY, region.MaxY), samplesPerEdge, project);

        (double X, double Y)[] SampleEnvelopePerimeter(
            (double LonMin, double LonMax, double LatMin, double LatMax) region,
            int samplesPerEdge,
            Func<double, double, (double X, double Y)> project)
        {
            // Walk the lon/lat rectangle clockwise, sampling each of the four edges. Each edge
            // omits its endpoint vertex (the next edge picks it up) so corners aren't duplicated.
            // The returned ring is in pixel space and ready for FillPolygon.
            var ring = new (double X, double Y)[samplesPerEdge * 4];
            var write = 0;
            for (var i = 0; i < samplesPerEdge; i++)
            {
                var t = (double)i / samplesPerEdge;
                ring[write++] = SampleEdge(region.LonMin, region.LatMin, region.LonMax, region.LatMin, t, project);
            }

            for (var i = 0; i < samplesPerEdge; i++)
            {
                var t = (double)i / samplesPerEdge;
                ring[write++] = SampleEdge(region.LonMax, region.LatMin, region.LonMax, region.LatMax, t, project);
            }

            for (var i = 0; i < samplesPerEdge; i++)
            {
                var t = (double)i / samplesPerEdge;
                ring[write++] = SampleEdge(region.LonMax, region.LatMax, region.LonMin, region.LatMax, t, project);
            }

            for (var i = 0; i < samplesPerEdge; i++)
            {
                var t = (double)i / samplesPerEdge;
                ring[write++] = SampleEdge(region.LonMin, region.LatMax, region.LonMin, region.LatMin, t, project);
            }

            return ring;
        }

        (double X, double Y) SampleEdge(double lonStart, double latStart, double lonEnd, double latEnd, double t, Func<double, double, (double X, double Y)> project)
        {
            var lon = lonStart + t * (lonEnd - lonStart);
            var lat = latStart + t * (latEnd - latStart);
            var (px, py) = project(lon, lat);
            return ToPixelFromProjected(px, py);
        }

        (double X, double Y)[] ToPixelsInLobe(IReadOnlyList<Position> positions, GoodeLobes.Lobe lobe)
        {
            // Project each lat/lon through the *specific* lobe (not FindLobe), then reuse the same
            // scale-and-centre transform as the regular pipeline. Going via the lobe directly is
            // essential at the clipped boundary, where the vertex sits exactly on the shared
            // meridian and FindLobe would deterministically pick one neighbour — projecting
            // through the wrong central meridian would put the boundary edge at the wrong x and
            // close the lobe in the wrong place.
            var result = new (double X, double Y)[positions.Count];
            for (var i = 0; i < positions.Count; i++)
            {
                var (px, py) = ProjectGoodeInLobe(positions[i].X, positions[i].Y, lobe);
                result[i] = ToPixelFromProjected(px, py);
            }

            return result;
        }

        (double X, double Y) ToPixelFromProjected(double projectedX, double projectedY)
        {
            var x = offsetX + (projectedX - projectedBounds.MinX) * scale;
            var y = Height - offsetY - (projectedY - projectedBounds.MinY) * scale;
            return (x, y);
        }

        Envelope ProjectEnvelope(Envelope bounds)
        {
            switch (kind)
            {
                case MapProjection.WebMercator:
                    // X is linear, Y is monotonic in latitude, so projecting the corners still suffices.
                    return new(
                        bounds.MinX,
                        ProjectWebMercatorY(bounds.MinY),
                        bounds.MaxX,
                        ProjectWebMercatorY(bounds.MaxY));
                case MapProjection.Lambert:
                case MapProjection.Goode:
                    // Lambert's parallels curve and meridians fan out; Goode's meridians taper toward
                    // the poles inside the Mollweide caps. Either way the corners alone undershoot the
                    // AABB, so sample the perimeter — 16 samples per edge captures the curvature without
                    // visibly affecting fit (both projections are smooth).
                    return SampleEnvelope(bounds, 16);
                default:
                    // PlateCarree: X and Y are both linear in lon/lat, so the corners are the extreme.
                    return bounds;
            }
        }

        Envelope SampleEnvelope(Envelope bounds, int samples)
        {
            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            for (var i = 0; i <= samples; i++)
            {
                var t = (double)i / samples;
                var lon = bounds.MinX + t * (bounds.MaxX - bounds.MinX);
                var lat = bounds.MinY + t * (bounds.MaxY - bounds.MinY);
                Visit(lon, bounds.MinY);
                Visit(lon, bounds.MaxY);
                Visit(bounds.MinX, lat);
                Visit(bounds.MaxX, lat);
            }

            return new(minX, minY, maxX, maxY);

            void Visit(double lon, double lat)
            {
                var (px, py) = ProjectPoint(lon, lat);
                if (px < minX)
                {
                    minX = px;
                }

                if (px > maxX)
                {
                    maxX = px;
                }

                if (py < minY)
                {
                    minY = py;
                }

                if (py > maxY)
                {
                    maxY = py;
                }
            }
        }

        (double X, double Y) ProjectPoint(double longitude, double latitude)
        {
            switch (kind)
            {
                case MapProjection.WebMercator:
                    return (longitude, ProjectWebMercatorY(latitude));
                case MapProjection.Lambert:
                    return lambert!.Project(longitude, latitude);
                case MapProjection.Goode:
                    return ProjectGoode(longitude, latitude);
                default:
                    return (longitude, latitude);
            }
        }

        static double ProjectWebMercatorY(double latitude)
        {
            var clamped = Math.Clamp(latitude, -WebMercatorMaxLatitude, WebMercatorMaxLatitude);
            var radians = clamped * Math.PI / 180;
            // Scale back to degree-equivalent units so the projected envelope reads in the same unit as
            // longitude — the downstream pixel math is scale-invariant either way, but this keeps the
            // aspect ratio of a degree-square patch at the equator equal to 1 in both projections.
            return Math.Log(Math.Tan(Math.PI / 4 + radians / 2)) * 180 / Math.PI;
        }

        // Goode's Homolosine interrupted into 2 northern and 4 southern lobes (the conventional
        // land-favouring split — meridians of interrupt run through ocean basins so continents fall
        // inside lobes rather than spanning them). Within each lobe the projection is the classic
        // Homolosine: sinusoidal between ±transition latitude (40°44'11.8") and Mollweide outside
        // that band, joined with a small vertical offset to make y continuous at the seam. The
        // conventional transition latitude makes the x scale continuous too, so the seam reads as
        // smooth inside each lobe.
        const double goodeTransitionLatitude = 40.7368 * Math.PI / 180;
        static readonly double goodeTransitionTheta = SolveMollweideTheta(goodeTransitionLatitude);

        // The Mollweide y at the transition latitude minus the sinusoidal y at the same latitude —
        // subtract this from northern-hemisphere Mollweide y (add for southern) so the seam reads
        // smooth instead of jumping.
        static readonly double goodeYShift = Math.Sqrt(2) * Math.Sin(goodeTransitionTheta) - goodeTransitionLatitude;

        // Mollweide x = (2√2/π) · (λ − λ₀) · cos(θ). The constant is what makes the projection
        // equal-area on the unit sphere.
        static readonly double mollweideXFactor = 2 * Math.Sqrt(2) / Math.PI;

        static (double X, double Y) ProjectGoode(double longitude, double latitude) =>
            ProjectGoodeInLobe(longitude, latitude, GoodeLobes.FindLobe(longitude, latitude));

        static (double X, double Y) ProjectGoodeInLobe(double longitude, double latitude, GoodeLobes.Lobe lobe)
        {
            // The lobe's central meridian is the reference longitude for the projection within that
            // lobe; offset the input lon by it, project through the uninterrupted Homolosine, then
            // translate the result back so the lobe sits at its true geographic x at the equator
            // (where x_local = lon - centralMeridian, x_world = lon).
            var (xLocal, y) = ProjectGoodeUninterrupted(longitude - lobe.CentralMeridian, latitude);
            return (xLocal + lobe.CentralMeridian, y);
        }

        static (double X, double Y) ProjectGoodeUninterrupted(double longitude, double latitude)
        {
            // Clamp off the pole. Mollweide's auxiliary angle θ converges to ±π/2 at the pole, where
            // f'(θ) = 4cos²(θ) vanishes and Newton blows up; the clamp keeps the solver in its
            // well-conditioned interior. The 0.001° shaved off the pole is invisible at any sensible
            // image size.
            var phi = Math.Clamp(latitude, -89.999, 89.999) * Math.PI / 180;
            var lambda = longitude * Math.PI / 180;

            double xRad;
            double yRad;
            if (Math.Abs(phi) <= goodeTransitionLatitude)
            {
                // Sinusoidal — equal-area on the band around the equator. y is just latitude; x is
                // longitude scaled by cos(φ), so parallels stay straight and meridians curve in.
                xRad = lambda * Math.Cos(phi);
                yRad = phi;
            }
            else
            {
                // Mollweide caps — equal-area at higher latitudes. The y offset (sign-flipped per
                // hemisphere) glues the cap onto the sinusoidal band without a vertical jump.
                var theta = SolveMollweideTheta(phi);
                xRad = mollweideXFactor * lambda * Math.Cos(theta);
                var mollweideY = Math.Sqrt(2) * Math.Sin(theta);
                yRad = phi >= 0 ? mollweideY - goodeYShift : mollweideY + goodeYShift;
            }

            // Convert back to degree-equivalent units so the projected envelope reads in the same
            // scale as longitude — matches WebMercator's and Lambert's output units.
            return (xRad * 180 / Math.PI, yRad * 180 / Math.PI);
        }

        static double SolveMollweideTheta(double phi)
        {
            // Mollweide's auxiliary angle θ from 2θ + sin(2θ) = π sin(φ). Snyder's recommended
            // initial guess asin(2φ/π) is already inside the basin of attraction, so 8 Newton
            // iterations reach full double precision well off the pole — the upstream lat clamp
            // keeps φ off ±π/2 where f'(θ) = 4cos²(θ) collapses. A fixed loop avoids a convergence
            // branch the coverage gate would otherwise need a dedicated test for.
            var target = Math.PI * Math.Sin(phi);
            var theta = Math.Asin(2 * phi / Math.PI);
            for (var i = 0; i < 8; i++)
            {
                var f = 2 * theta + Math.Sin(2 * theta) - target;
                var derivative = 4 * Math.Cos(theta) * Math.Cos(theta);
                theta -= f / derivative;
            }

            return theta;
        }

        // Thresholds for Auto. Above the world cutoffs the bounds approach full-globe coverage and
        // Goode's equal-area Homolosine is the honest world projection; between the world and
        // regional cutoffs the data is continental and the LCC cone unfolds too far (parallels grow
        // visibly curved), so PlateCarree is the conventional fallback; under the regional cutoffs
        // Lambert is right. The cutoffs are deliberately conservative — Africa (latSpan ≈ 73°) routes
        // to PlateCarree, Asia (lonSpan ≈ 165°) stays PlateCarree, while a true world view (lonSpan
        // 360°) picks Goode.
        const double autoLatitudeSpanLimit = 60;
        const double autoLongitudeSpanLimit = 90;
        const double autoWorldLatitudeSpan = 90;
        const double autoWorldLongitudeSpan = 180;

        static MapProjection Resolve(MapProjection requested, Envelope bounds)
        {
            if (requested != MapProjection.Auto)
            {
                return requested;
            }

            if (bounds.Width >= autoWorldLongitudeSpan || bounds.Height >= autoWorldLatitudeSpan)
            {
                return MapProjection.Goode;
            }

            if (bounds.Width >= autoLongitudeSpanLimit || bounds.Height >= autoLatitudeSpanLimit)
            {
                return MapProjection.PlateCarree;
            }

            // Lambert handles its own degenerate cases (equator-symmetric or zero-span bounds) by
            // returning null from TryFrom, which the renderer then falls back to PlateCarree — so we
            // can pick Lambert unconditionally here and let that path handle the edge.
            return MapProjection.Lambert;
        }
    }

    /// <summary>
    /// Spherical Lambert Conformal Conic with two standard parallels picked from the input bounds.
    /// Working on the unit sphere — earth radius drops out because the renderer applies a uniform
    /// scale-to-fit afterwards — and the output is converted back to degree-equivalent units so the
    /// envelope reads in the same scale as the other projections.
    /// </summary>
    sealed class LambertParameters
    {
        // Reference longitude (radians) — the central meridian of the projection.
        readonly double lambda0;

        // How tightly the cone wraps the globe — the ratio between an angle on the unrolled cone and
        // the corresponding longitude span (so a 360° trip around the parallel becomes coneConstant·360°
        // on the flat map). Snyder's Working Manual calls this n; it's sin(φ₁) for a tangent cone, or
        // derived from both standard parallels for a secant cone. Sign follows the hemisphere: positive
        // for northern bounds (cone opens downward), negative for southern — signals which pole the
        // cone's apex points away from.
        readonly double coneConstant;

        // Radial scale of the cone — the numerator in ρ = coneScale / tan(π/4 + φ/2)^coneConstant,
        // controlling how far each parallel sits from the cone's apex. Snyder's Working Manual calls
        // this F; coneScale = cos(φ₁) · tan(π/4 + φ₁/2)^coneConstant / coneConstant.
        readonly double coneScale;

        // ρ at the reference parallel φ₀ — the "false northing" baseline so the origin maps to y = 0.
        readonly double rho0;

        LambertParameters(double lambda0, double coneConstant, double coneScale, double rho0)
        {
            this.lambda0 = lambda0;
            this.coneConstant = coneConstant;
            this.coneScale = coneScale;
            this.rho0 = rho0;
        }

        public static LambertParameters? TryFrom(Envelope bounds)
        {
            // Auto-pick standard parallels at the 1/6 and 5/6 marks of the data's latitude range — the
            // de facto convention used by national mapping agencies for country-scale LCC layouts. The
            // reference origin is the centre of the bounds.
            var minLat = bounds.MinY;
            var maxLat = bounds.MaxY;
            var span = maxLat - minLat;
            var phi1 = (minLat + span / 6) * Math.PI / 180;
            var phi2 = (maxLat - span / 6) * Math.PI / 180;
            var phi0 = (minLat + maxLat) / 2 * Math.PI / 180;
            var lambda0 = (bounds.MinX + bounds.MaxX) / 2 * Math.PI / 180;

            double coneConstant;
            if (Math.Abs(phi1 - phi2) < 1e-10)
            {
                // Single standard parallel (zero-height latitude span): cone tangent at φ₁.
                coneConstant = Math.Sin(phi1);
            }
            else
            {
                coneConstant = Math.Log(Math.Cos(phi1) / Math.Cos(phi2)) /
                    Math.Log(Math.Tan(Math.PI / 4 + phi2 / 2) / Math.Tan(Math.PI / 4 + phi1 / 2));
            }

            // coneConstant → 0 means the cone has unfolded into a cylinder (bounds straddle the equator
            // symmetrically, or sit exactly on it); the LCC formulas degenerate and ρ blows up. Signal
            // the caller to fall back to a different projection rather than emit NaN pixels.
            if (!double.IsFinite(coneConstant) || Math.Abs(coneConstant) < 1e-6)
            {
                return null;
            }

            var coneScale = Math.Cos(phi1) * Math.Pow(Math.Tan(Math.PI / 4 + phi1 / 2), coneConstant) / coneConstant;
            var rho0 = coneScale / Math.Pow(Math.Tan(Math.PI / 4 + phi0 / 2), coneConstant);
            return new(lambda0, coneConstant, coneScale, rho0);
        }

        public (double X, double Y) Project(double longitude, double latitude)
        {
            // Clamp away from the pole on the cone's opposite side, where tan(π/4 + φ/2) reaches 0 or
            // ∞ and ρ diverges. Sensible country-scale bounds never trip this; it's a defensive guard
            // against malformed input reaching the rasterizer.
            var phi = Math.Clamp(latitude, -89.999, 89.999) * Math.PI / 180;
            var lambda = longitude * Math.PI / 180;
            var rho = coneScale / Math.Pow(Math.Tan(Math.PI / 4 + phi / 2), coneConstant);
            var theta = coneConstant * (lambda - lambda0);
            var x = rho * Math.Sin(theta);
            var y = rho0 - rho * Math.Cos(theta);
            // Convert to degree-equivalent units (matches the WebMercator output unit) so the scale-to-
            // fit envelope reads in the same range as longitude. The ratio is preserved, so this only
            // affects how the projected coordinates *look* in the envelope, not the rendered aspect.
            return (x * 180 / Math.PI, y * 180 / Math.PI);
        }
    }

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
}
