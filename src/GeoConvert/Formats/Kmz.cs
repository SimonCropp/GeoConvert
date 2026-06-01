namespace GeoConvert;

/// <summary>
/// Reads and writes KMZ — a ZIP archive wrapping one or more KML documents. A KMZ with a single
/// <c>.kml</c> entry round-trips as the root layer (whose folder structure is preserved as
/// <see cref="FeatureCollection.Children"/> via <see cref="Kml"/>); an archive with multiple
/// <c>.kml</c> entries becomes a root whose children are the parsed documents, named after their
/// entry filenames. On write, the full layered tree is stored as a single <c>doc.kml</c> — any
/// multi-document structure read in is preserved as nested KML folders rather than separate
/// archive entries.
/// </summary>
public static class Kmz
{
    public static FeatureCollection Read(Stream stream) =>
        Read(stream, null);

    internal static FeatureCollection Read(Stream stream, ProgressReporter? progress)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var kmlEntries = archive.Entries
            .Where(_ => _.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (kmlEntries.Count == 0)
        {
            throw new GeoConvertException("KMZ archive contains no .kml entry.");
        }

        if (kmlEntries.Count == 1)
        {
            using var entryStream = kmlEntries[0].Open();
            return Kml.Read(entryStream, progress);
        }

        var root = new FeatureCollection();
        foreach (var entry in kmlEntries)
        {
            using var entryStream = entry.Open();
            var child = Kml.Read(entryStream, progress);
            // A KML <Document>'s name (if present) wins; otherwise fall back to the archive entry's filename.
            child.Name ??= Path.GetFileNameWithoutExtension(entry.FullName);
            root.Children.Add(child);
        }

        return root;
    }

    public static void Write(
        Stream stream,
        FeatureCollection features,
        CompressionLevel compression = CompressionLevel.Optimal) =>
        Write(stream, features, compression, null);

    internal static void Write(Stream stream, FeatureCollection features, ProgressReporter? progress) =>
        Write(stream, features, CompressionLevel.Optimal, progress);

    internal static void Write(
        Stream stream,
        FeatureCollection features,
        CompressionLevel compression,
        ProgressReporter? progress)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry("doc.kml", compression);
        // A fixed timestamp keeps the archive byte-for-byte reproducible.
        entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var entryStream = entry.Open();
        Kml.Write(entryStream, features, progress);
    }
}
