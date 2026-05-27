namespace GeoConvert;

/// <summary>
/// Compression codec used for the data pages of a GeoParquet file. The reader additionally accepts
/// Zstd-encoded input (on .NET 11+), but Zstd is not exposed on the writer because the BCL only
/// supplies decoder access via <c>ZstandardStream</c> in stream form on the supported targets.
/// </summary>
public enum ParquetCompression
{
    /// <summary>Snappy (default). Fast with modest ratios; the GeoParquet ecosystem's most common choice.</summary>
    Snappy,

    /// <summary>Pages stored without compression.</summary>
    Uncompressed,

    /// <summary>GZIP. Slower writes, smaller output; respects the supplied <see cref="System.IO.Compression.CompressionLevel"/>.</summary>
    Gzip,
}
