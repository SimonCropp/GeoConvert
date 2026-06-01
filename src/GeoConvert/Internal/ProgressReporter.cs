namespace GeoConvert;

/// <summary>
/// Bundles the running feature and byte counters plus the phase totals behind a single
/// <see cref="IProgress{T}"/> sink. The facade, the codecs and <see cref="ProgressStream"/> each nudge
/// one shared instance — features via <see cref="Feature"/>, bytes via <see cref="AddBytes"/> — and
/// every nudge emits a combined <see cref="ConvertProgress"/>. Internal: callers only ever see the
/// <see cref="IProgress{T}"/> they passed in.
/// </summary>
sealed class ProgressReporter
{
    readonly IProgress<ConvertProgress> sink;
    readonly ProgressPhase phase;
    readonly long? featureTotal;
    readonly long? byteTotal;
    long features;
    long bytes;

    public ProgressReporter(IProgress<ConvertProgress> sink, ProgressPhase phase, long? featureTotal, long? byteTotal)
    {
        this.sink = sink;
        this.phase = phase;
        this.featureTotal = featureTotal;
        this.byteTotal = byteTotal;
        // Emit an initial baseline so a caller can render a 0% state before any data flows.
        Report();
    }

    public void Feature()
    {
        features++;
        Report();
    }

    public void AddBytes(long count)
    {
        bytes += count;
        Report();
    }

    void Report() =>
        sink.Report(new(phase, features, featureTotal, bytes, byteTotal));
}
