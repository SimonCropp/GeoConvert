static class Sample
{
    // A tiny two-feature collection: a triangle polygon and a labelled point. Enough for the codecs to
    // round-trip and for the renderer to produce a non-empty PNG.
    public const string GeoJson =
        """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "name": "Land" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[0, 0], [10, 0], [5, 8], [0, 0]]]
              }
            },
            {
              "type": "Feature",
              "properties": { "name": "City" },
              "geometry": {
                "type": "Point",
                "coordinates": [5, 3]
              }
            }
          ]
        }
        """;

    public static byte[] GeoJsonBytes { get; } = Encoding.UTF8.GetBytes(GeoJson);
}
