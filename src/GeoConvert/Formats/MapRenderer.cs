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

        if (options.Ocean is { } ocean)
        {
            // Paint the projection envelope first so every feature layer renders on top of it. For
            // Goode this fills each lobe with the ocean colour, leaving the inter-lobe gaps as the
            // canvas background — that's what makes the projection's lobed shape pop visually.
            foreach (var ring in projection.GetWorldEnvelopeRings())
            {
                canvas.FillPolygon([ring], ocean);
            }
        }

        foreach (var layer in layers)
        {
            DrawLayer(canvas, layer, projection, options);
        }

        Png.Write(stream, canvas.Pixels, canvas.Width, canvas.Height, options.Compression);
    }

    // Pre-order: a layer paints its own features first, then recurses into its children. Source-over
    // blending means whatever paints last sits on top, so children naturally appear over their parent
    // — pick layer styles via RenderOptions.LayerStyle to keep them visually distinct.
    static void DrawLayer(Canvas canvas, FeatureCollection layer, Projection projection, RenderOptions options)
    {
        var style = Resolve(options.LayerStyle?.Invoke(layer), options);
        foreach (var feature in layer.Features)
        {
            if (feature.Geometry is { } geometry)
            {
                Draw(canvas, geometry, projection, style);
            }
        }

        foreach (var child in layer.Children)
        {
            DrawLayer(canvas, child, projection, options);
        }
    }

    // Collapses the user-facing LayerStyle (any subset of overrides) into the four concrete values the
    // rasterizer needs, falling back to RenderOptions defaults for each null property independently.
    static ResolvedStyle Resolve(LayerStyle? overrides, RenderOptions options) =>
        new(
            overrides?.Stroke ?? options.Stroke,
            overrides?.Fill ?? options.Fill,
            overrides?.StrokeWidth ?? options.StrokeWidth,
            overrides?.PointRadius ?? options.PointRadius);

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
        // PreparePolygon yields one batch of rings per output piece. Non-interrupted projections
        // emit a single batch (all rings, projected as-is). Goode's interrupted form clips the
        // input rings to each lobe and emits one batch per lobe with content, so a polygon
        // straddling the boundary at lon=-40°N renders as two disjoint shapes — the lobe edges
        // close along the clip meridian, which is the visual hallmark of the projection.
        foreach (var rings in projection.PreparePolygon(polygon.Rings))
        {
            canvas.FillPolygon(rings, style.Fill);
            foreach (var ring in rings)
            {
                StrokeRing(canvas, ring, style);
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

    readonly record struct ResolvedStyle(Rgba Stroke, Rgba Fill, int StrokeWidth, int PointRadius);

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
        /// One batch of pixel rings per output piece. For non-interrupted projections that's a
        /// single batch containing every input ring projected as-is; for <see cref="MapProjection.Goode"/>
        /// the input rings are clipped to each lobe's lon/lat bounds and each non-empty result is
        /// projected through that lobe's central meridian — so the rasterizer fills and strokes
        /// each lobe's piece independently and the inter-lobe gap stays empty.
        /// </summary>
        public IEnumerable<(double X, double Y)[][]> PreparePolygon(IReadOnlyList<IReadOnlyList<Position>> rings)
        {
            if (kind != MapProjection.Goode)
            {
                yield return rings.Select(ToPixels).ToArray();
                yield break;
            }

            foreach (var lobe in GoodeLobes.AllLobes)
            {
                var batch = new List<(double X, double Y)[]>(rings.Count);
                foreach (var ring in rings)
                {
                    var clipped = GoodeLobes.ClipRing(ring, lobe);
                    // S-H can leave a sub-ring with <3 vertices if the polygon just grazes the lobe;
                    // skip those — FillPolygon would draw a degenerate sliver and StrokeRing would
                    // emit zero-length edges.
                    if (clipped.Count >= 3)
                    {
                        batch.Add(ToPixelsInLobe(clipped, lobe));
                    }
                }

                if (batch.Count > 0)
                {
                    yield return batch.ToArray();
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
                    // Clip the lobe rectangle to whatever the caller asked to render, so a partial
                    // world view (say, bounds limited to lat ≥ 0) only paints the visible lobes
                    // and the ocean stays inside the requested extent.
                    var visible = ClampLobeToBounds(lobe, inputBounds);
                    if (visible is { } region)
                    {
                        yield return SampleEnvelopePerimeter(region, 32, (lon, lat) => ProjectGoodeInLobe(lon, lat, lobe));
                    }
                }

                yield break;
            }

            // Non-Goode: the input bounds *is* the world envelope. Sampling matters for projections
            // whose perimeter curves on the canvas (Lambert, WebMercator); for PlateCarree the four
            // corners would suffice, but sampling at 16 costs nothing and keeps the code uniform.
            yield return SampleEnvelopePerimeter(inputBounds, 16, ProjectPoint);
        }

        static (double LonMin, double LonMax, double LatMin, double LatMax)? ClampLobeToBounds(GoodeLobes.Lobe lobe, Envelope bounds)
        {
            var lonMin = Math.Max(lobe.LonMin, bounds.MinX);
            var lonMax = Math.Min(lobe.LonMax, bounds.MaxX);
            var latMin = Math.Max(lobe.LatMin, bounds.MinY);
            var latMax = Math.Min(lobe.LatMax, bounds.MaxY);
            if (lonMin >= lonMax || latMin >= latMax)
            {
                return null;
            }

            return (lonMin, lonMax, latMin, latMax);
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
        /// <summary>Per-lobe parameters: central meridian (λ₀) and the lon/lat AABB that defines
        /// which input coordinates belong to this lobe.</summary>
        public readonly record struct Lobe(double CentralMeridian, double LonMin, double LonMax, double LatMin, double LatMax);

        // Conventional Goode's interrupted Homolosine: the north has 2 lobes meeting at lon=-40°
        // (cutting through the mid-Atlantic), the south has 4 meeting at lon=-100°, -20° and +80°
        // (cutting through the eastern Pacific, the south Atlantic, and the Indian Ocean). Every
        // central meridian sits in a continental landmass so the major continents stay intact
        // inside a lobe — that's the point of the interrupted form.
        public static readonly Lobe[] AllLobes =
        [
            // North: split at lon=-40°, so western lobe covers the Americas and the eastern lobe
            // covers Eurasia + Africa-N. Central meridians -100° (≈ central USA) and +30° (Egypt).
            new(CentralMeridian: -100, LonMin: -180, LonMax:  -40, LatMin:   0, LatMax:  90),
            new(CentralMeridian:   30, LonMin:  -40, LonMax:  180, LatMin:   0, LatMax:  90),

            // South: four lobes covering S-America, S-Africa, Australia, and the Pacific. Central
            // meridians chosen at the centre of each landmass.
            new(CentralMeridian: -160, LonMin: -180, LonMax: -100, LatMin: -90, LatMax:   0),
            new(CentralMeridian:  -60, LonMin: -100, LonMax:  -20, LatMin: -90, LatMax:   0),
            new(CentralMeridian:   20, LonMin:  -20, LonMax:   80, LatMin: -90, LatMax:   0),
            new(CentralMeridian:  140, LonMin:   80, LonMax:  180, LatMin: -90, LatMax:   0),
        ];

        /// <summary>The lobe a single point belongs to. Walked in array order, so a point exactly
        /// on a shared boundary (e.g. lon=-40° in the north) lands in the lower-lonMin neighbour;
        /// this is fine for per-point projection because both lobes' formulas agree at the
        /// boundary in lat-band terms — only the post-projection x differs.</summary>
        public static Lobe FindLobe(double longitude, double latitude)
        {
            foreach (var lobe in AllLobes)
            {
                if (longitude >= lobe.LonMin && longitude <= lobe.LonMax &&
                    latitude >= lobe.LatMin && latitude <= lobe.LatMax)
                {
                    return lobe;
                }
            }

            // Malformed input fell outside [-180, 180] × [-90, 90]; fall back to the lobe whose
            // central meridian is closest in lon so the projection still produces a finite point
            // instead of throwing. The renderer prefers a graceful degraded output over a crash.
            var best = AllLobes[0];
            var bestDistance = double.PositiveInfinity;
            foreach (var lobe in AllLobes)
            {
                if (latitude >= 0 != lobe.LatMin >= 0)
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

        /// <summary>Sutherland-Hodgman clip of a polygon ring against the lobe's lon/lat AABB. The
        /// returned ring is closed (S-H walks the input as a closed loop and re-stitches edges
        /// along each clip plane) and entirely within one lobe, so the renderer can project it
        /// through that lobe's central meridian without further checks.</summary>
        public static List<Position> ClipRing(IReadOnlyList<Position> ring, Lobe lobe)
        {
            // Four passes — one per half-plane. Each pass walks the current ring and emits a new
            // ring with the parts inside the half-plane plus interpolated intersection points
            // where edges cross the boundary.
            var result = ClipHalfPlane(ring,
                p => p.X >= lobe.LonMin,
                (a, b) => InterpolateToX(a, b, lobe.LonMin));
            result = ClipHalfPlane(result,
                p => p.X <= lobe.LonMax,
                (a, b) => InterpolateToX(a, b, lobe.LonMax));
            result = ClipHalfPlane(result,
                p => p.Y >= lobe.LatMin,
                (a, b) => InterpolateToY(a, b, lobe.LatMin));
            result = ClipHalfPlane(result,
                p => p.Y <= lobe.LatMax,
                (a, b) => InterpolateToY(a, b, lobe.LatMax));
            return result;
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

        static List<Position> ClipHalfPlane(
            IReadOnlyList<Position> ring,
            Func<Position, bool> inside,
            Func<Position, Position, Position> intersect)
        {
            var output = new List<Position>();
            if (ring.Count == 0)
            {
                return output;
            }

            for (var i = 0; i < ring.Count; i++)
            {
                var current = ring[i];
                var previous = ring[(i + ring.Count - 1) % ring.Count];
                var currentInside = inside(current);
                var previousInside = inside(previous);
                if (previousInside != currentInside)
                {
                    output.Add(intersect(previous, current));
                }

                if (currentInside)
                {
                    output.Add(current);
                }
            }

            return output;
        }

        static Position InterpolateToBoundary(Position a, Position b, Lobe lobeA, Lobe lobeB)
        {
            // Different hemispheres → split at the equator (lat=0). Otherwise the lobes share a
            // meridian — its longitude is whichever of A's lonMin/lonMax equals B's lonMax/lonMin.
            if (lobeA.LatMin != lobeB.LatMin)
            {
                return InterpolateToY(a, b, 0);
            }

            var boundaryLon = lobeA.LonMax == lobeB.LonMin
                ? lobeA.LonMax
                : lobeA.LonMin;
            return InterpolateToX(a, b, boundaryLon);
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
