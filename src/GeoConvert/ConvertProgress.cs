namespace GeoConvert;

/// <summary>Which half of a conversion a <see cref="ConvertProgress"/> report describes.</summary>
public enum ProgressPhase
{
    /// <summary>Decoding an input source into a <see cref="FeatureCollection"/>.</summary>
    Reading,

    /// <summary>Encoding a <see cref="FeatureCollection"/> to an output sink.</summary>
    Writing,
}

/// <summary>
/// A single progress notification emitted while reading, writing or converting. Pass an
/// <see cref="IProgress{T}"/> of this type to the <see cref="GeoConverter"/> read/write/convert methods
/// (or to <see cref="RenderOptions.Progress"/>) to receive these as work proceeds.
/// <para>
/// Both a feature count and a byte count are carried in every report so a caller can use whichever is
/// meaningful for the operation. <see cref="FeatureTotal"/> is known up front when writing (the source
/// collection's <see cref="FeatureCollection.Count"/>) but not when reading (the total isn't known until
/// the source is fully parsed), so it is null on the read phase. <see cref="ByteTotal"/> is known when
/// reading from a seekable stream (its length) but not when writing (the encoded size isn't known until
/// the write completes), so it is null on the write phase. Either total can also be null when the
/// underlying source/sink can't report a size (e.g. a non-seekable stream).
/// </para>
/// </summary>
public readonly struct ConvertProgress(
    ProgressPhase phase,
    long features,
    long? featureTotal,
    long bytes,
    long? byteTotal)
{
    /// <summary>Whether this report describes the read or the write half of the work.</summary>
    public ProgressPhase Phase { get; } = phase;

    /// <summary>Features read (this phase) so far, or written so far.</summary>
    public long Features { get; } = features;

    /// <summary>Total features expected, or null when the total isn't known yet (the read phase).</summary>
    public long? FeatureTotal { get; } = featureTotal;

    /// <summary>Bytes consumed from the source (read) or written to the sink (write) so far.</summary>
    public long Bytes { get; } = bytes;

    /// <summary>Total bytes expected, or null when the total isn't known (the write phase, or a non-seekable source).</summary>
    public long? ByteTotal { get; } = byteTotal;

    /// <summary>
    /// A completion fraction in [0, 1] when one can be derived, else null. Prefers the feature ratio
    /// when <see cref="FeatureTotal"/> is known (it tracks the work the caller cares about most), and
    /// falls back to the byte ratio when only <see cref="ByteTotal"/> is known. Returns null when
    /// neither total is available, so an indeterminate operation reports an honest "unknown" rather than
    /// a fabricated number.
    /// </summary>
    public double? Fraction
    {
        get
        {
            if (FeatureTotal is > 0)
            {
                return Math.Clamp((double) Features / FeatureTotal.Value, 0, 1);
            }

            if (ByteTotal is > 0)
            {
                return Math.Clamp((double) Bytes / ByteTotal.Value, 0, 1);
            }

            return null;
        }
    }
}
