using System.IO.Compression;

namespace GeoConvert;

/// <summary>
/// Reads and writes KMZ — a ZIP archive wrapping a single KML document. On read the first
/// <c>.kml</c> entry is parsed; on write the KML is stored as <c>doc.kml</c>.
/// </summary>
public static class Kmz
{
    public static FeatureCollection Read(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(
                        _ => _.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase)) ??
                    throw new GeoConvertException("KMZ archive contains no .kml entry.");
        using var entryStream = entry.Open();
        return Kml.Read(entryStream);
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry("doc.kml", CompressionLevel.Optimal);
        // A fixed timestamp keeps the archive byte-for-byte reproducible.
        entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var entryStream = entry.Open();
        Kml.Write(entryStream, collection);
    }
}
