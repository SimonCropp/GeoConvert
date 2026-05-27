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

    public static byte[] RenderPng(FeatureCollection features, RenderOptions? options = null)
    {
        options ??= new();
        var bounds = Validate(features, options);
        using var memory = new MemoryStream();
        Render(features, memory, options, bounds);
        return memory.ToArray();
    }

    public static void RenderPng(FeatureCollection features, string path, RenderOptions? options = null)
    {
        options ??= new();
        // Validate before File.Create so a throw leaves the destination untouched instead of stranding
        // a 0-byte file. Mid-render stream failures (disk full, etc.) can still leave a partial file,
        // but those are unrecoverable I/O errors where a partial file is the conventional signal.
        var bounds = Validate(features, options);
        using var stream = File.Create(path);
        Render(features, stream, options, bounds);
    }

    public static void RenderPng(FeatureCollection features, Stream stream, RenderOptions? options = null)
    {
        options ??= new();
        var bounds = Validate(features, options);
        Render(features, stream, options, bounds);
    }

    static Envelope Validate(FeatureCollection features, RenderOptions options)
    {
        var bounds = options.Bounds ?? features.GetBounds();
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

    static void Render(FeatureCollection features, Stream stream, RenderOptions options, Envelope bounds)
    {
        var projection = new Projection(bounds, options);
        var canvas = new Canvas(projection.Width, projection.Height, options.Background);

        foreach (var feature in features)
        {
            if (feature.Geometry is { } geometry)
            {
                Draw(canvas, geometry, projection, options);
            }
        }

        Png.Write(stream, canvas.Pixels, canvas.Width, canvas.Height, options.Compression);
    }

    static void Draw(Canvas canvas, Geometry geometry, Projection projection, RenderOptions options)
    {
        switch (geometry)
        {
            case Point point:
                var (px, py) = projection.ToPixel(point.Coordinate);
                canvas.FillDisc(px, py, options.PointRadius, options.Stroke);
                break;
            case MultiPoint multiPoint:
                foreach (var position in multiPoint.Positions)
                {
                    var (x, y) = projection.ToPixel(position);
                    canvas.FillDisc(x, y, options.PointRadius, options.Stroke);
                }

                break;
            case LineString line:
                StrokePath(canvas, line.Positions, projection, options);
                break;
            case MultiLineString multiLine:
                foreach (var child in multiLine.LineStrings)
                {
                    StrokePath(canvas, child.Positions, projection, options);
                }

                break;
            case Polygon polygon:
                DrawPolygon(canvas, polygon, projection, options);
                break;
            case MultiPolygon multiPolygon:
                foreach (var child in multiPolygon.Polygons)
                {
                    DrawPolygon(canvas, child, projection, options);
                }

                break;
            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    Draw(canvas, child, projection, options);
                }

                break;
        }
    }

    static void DrawPolygon(Canvas canvas, Polygon polygon, Projection projection, RenderOptions options)
    {
        var rings = polygon.Rings.Select(projection.ToPixels).ToArray();
        canvas.FillPolygon(rings, options.Fill);
        foreach (var ring in rings)
        {
            StrokeRing(canvas, ring, options);
        }
    }

    static void StrokePath(Canvas canvas, IReadOnlyList<Position> positions, Projection projection, RenderOptions options)
    {
        for (var i = 0; i + 1 < positions.Count; i++)
        {
            var (x0, y0) = projection.ToPixel(positions[i]);
            var (x1, y1) = projection.ToPixel(positions[i + 1]);
            canvas.StrokeLine(x0, y0, x1, y1, options.StrokeWidth, options.Stroke);
        }
    }

    static void StrokeRing(Canvas canvas, (double X, double Y)[] ring, RenderOptions options)
    {
        for (var i = 0; i + 1 < ring.Length; i++)
        {
            canvas.StrokeLine(ring[i].X, ring[i].Y, ring[i + 1].X, ring[i + 1].Y, options.StrokeWidth, options.Stroke);
        }
    }

    /// <summary>
    /// Maps longitude/latitude into pixel space: first through the chosen <see cref="MapProjection"/>
    /// (planar coords), then a uniform scale that fits the projected extent into the canvas, centered,
    /// with the Y axis flipped.
    /// </summary>
    sealed class Projection
    {
        MapProjection kind;
        Envelope projectedBounds;
        double scale;
        double offsetX;
        double offsetY;
        LambertParameters? lambert;

        public Projection(Envelope bounds, RenderOptions options)
        {
            kind = options.Projection;
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
            var x = offsetX + (projectedX - projectedBounds.MinX) * scale;
            var y = Height - offsetY - (projectedY - projectedBounds.MinY) * scale;
            return (x, y);
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
                    // Meridians fan out and parallels curve, so the corners alone undershoot the AABB.
                    // Sampling the perimeter at a handful of points captures the curvature without
                    // visibly affecting fit (the projection is smooth, so 16 samples per edge is plenty).
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

        // Cone constant (sin of the standard parallel for one-parallel LCC; derived from the two
        // parallels otherwise). Sign follows the hemisphere: positive for northern bounds (cone opens
        // downward), negative for southern, signalling which pole the cone's apex points away from.
        readonly double n;

        // Scaling constant F = cos(φ₁) · tan(π/4 + φ₁/2)^n / n, holding the projection's overall scale.
        readonly double F;

        // ρ at the reference parallel φ₀ — the "false northing" baseline so the origin maps to y = 0.
        readonly double rho0;

        LambertParameters(double lambda0, double n, double F, double rho0)
        {
            this.lambda0 = lambda0;
            this.n = n;
            this.F = F;
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
            var phi0 = ((minLat + maxLat) / 2) * Math.PI / 180;
            var lambda0 = ((bounds.MinX + bounds.MaxX) / 2) * Math.PI / 180;

            double n;
            if (Math.Abs(phi1 - phi2) < 1e-10)
            {
                // Single standard parallel (zero-height latitude span): cone tangent at φ₁.
                n = Math.Sin(phi1);
            }
            else
            {
                n = Math.Log(Math.Cos(phi1) / Math.Cos(phi2)) /
                    Math.Log(Math.Tan(Math.PI / 4 + phi2 / 2) / Math.Tan(Math.PI / 4 + phi1 / 2));
            }

            // n → 0 means the cone has unfolded into a cylinder (bounds straddle the equator
            // symmetrically, or sit exactly on it); the LCC formulas degenerate and ρ blows up. Signal
            // the caller to fall back to a different projection rather than emit NaN pixels.
            if (!double.IsFinite(n) || Math.Abs(n) < 1e-6)
            {
                return null;
            }

            var F = Math.Cos(phi1) * Math.Pow(Math.Tan(Math.PI / 4 + phi1 / 2), n) / n;
            var rho0 = F / Math.Pow(Math.Tan(Math.PI / 4 + phi0 / 2), n);
            return new(lambda0, n, F, rho0);
        }

        public (double X, double Y) Project(double longitude, double latitude)
        {
            // Clamp away from the pole on the cone's opposite side, where tan(π/4 + φ/2) reaches 0 or
            // ∞ and ρ diverges. Sensible country-scale bounds never trip this; it's a defensive guard
            // against malformed input reaching the rasterizer.
            var phi = Math.Clamp(latitude, -89.999, 89.999) * Math.PI / 180;
            var lambda = longitude * Math.PI / 180;
            var rho = F / Math.Pow(Math.Tan(Math.PI / 4 + phi / 2), n);
            var theta = n * (lambda - lambda0);
            var x = rho * Math.Sin(theta);
            var y = rho0 - rho * Math.Cos(theta);
            // Convert to degree-equivalent units (matches the WebMercator output unit) so the scale-to-
            // fit envelope reads in the same range as longitude. The ratio is preserved, so this only
            // affects how the projected coordinates *look* in the envelope, not the rendered aspect.
            return (x * 180 / Math.PI, y * 180 / Math.PI);
        }
    }
}
