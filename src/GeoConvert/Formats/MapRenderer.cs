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

        public Projection(Envelope bounds, RenderOptions options)
        {
            kind = options.Projection;
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
            var projectedY = ProjectLatitude(position.Y);
            var x = offsetX + (position.X - projectedBounds.MinX) * scale;
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
            // X is linear in both projections supported here, so projecting the corners suffices.
            if (kind == MapProjection.PlateCarree)
            {
                return bounds;
            }

            return new(bounds.MinX, ProjectLatitude(bounds.MinY), bounds.MaxX, ProjectLatitude(bounds.MaxY));
        }

        double ProjectLatitude(double latitude)
        {
            if (kind == MapProjection.PlateCarree)
            {
                return latitude;
            }

            var clamped = Math.Clamp(latitude, -WebMercatorMaxLatitude, WebMercatorMaxLatitude);
            var radians = clamped * Math.PI / 180;
            // Scale back to degree-equivalent units so the projected envelope reads in the same unit as
            // longitude — the downstream pixel math is scale-invariant either way, but this keeps the
            // aspect ratio of a degree-square patch at the equator equal to 1 in both projections.
            return Math.Log(Math.Tan(Math.PI / 4 + radians / 2)) * 180 / Math.PI;
        }
    }
}
