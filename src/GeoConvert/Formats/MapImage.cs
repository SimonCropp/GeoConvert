namespace GeoConvert;

/// <summary>
/// Renders a <see cref="FeatureCollection"/> to a PNG raster, clipped to a bounding box. This is a
/// write-only export (a PNG cannot be read back into features). Built on a small software rasterizer and
/// a hand-rolled PNG encoder, with no third-party dependencies.
/// </summary>
public static class MapRenderer
{
    public static byte[] RenderPng(FeatureCollection collection, RenderOptions? options = null)
    {
        using var memory = new MemoryStream();
        RenderPng(collection, memory, options);
        return memory.ToArray();
    }

    public static void RenderPng(FeatureCollection collection, string path, RenderOptions? options = null)
    {
        using var stream = File.Create(path);
        RenderPng(collection, stream, options);
    }

    public static void RenderPng(FeatureCollection collection, Stream stream, RenderOptions? options = null)
    {
        options ??= new();

        var bounds = options.Bounds ?? collection.GetBounds();
        if (bounds.IsEmpty)
        {
            throw new GeoConvertException(
                "Cannot render PNG: the collection is empty. Provide RenderOptions.Bounds.");
        }

        if (options.Width <= 0)
        {
            throw new GeoConvertException("RenderOptions.Width must be positive.");
        }

        var projection = new Projection(bounds, options);
        var canvas = new Canvas(projection.Width, projection.Height, options.Background);

        foreach (var feature in collection)
        {
            if (feature.Geometry is { } geometry)
            {
                Draw(canvas, geometry, projection, options);
            }
        }

        Png.Write(stream, canvas.Pixels, canvas.Width, canvas.Height);
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

    /// <summary>Maps longitude/latitude into pixel space: uniform scale, centered, with the Y axis flipped.</summary>
    sealed class Projection
    {
        readonly Envelope bounds;
        readonly double scale;
        readonly double offsetX;
        readonly double offsetY;

        public Projection(Envelope bounds, RenderOptions options)
        {
            this.bounds = bounds;
            var boundsWidth = bounds.Width > 0 ? bounds.Width : 1;
            var boundsHeight = bounds.Height > 0 ? bounds.Height : 1;

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
            var x = offsetX + (position.X - bounds.MinX) * scale;
            var y = Height - offsetY - (position.Y - bounds.MinY) * scale;
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
    }
}
