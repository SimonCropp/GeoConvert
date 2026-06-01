namespace GeoConvert;

/// <summary>
/// A pass-through <see cref="Stream"/> decorator that tallies the bytes read from (or written to) the
/// wrapped stream into a <see cref="ProgressReporter"/>. Every other member forwards straight to the
/// inner stream so codecs that seek (FlatGeobuf skipping its index, Shapefile) behave exactly as if
/// they held the real stream. It does not own the inner stream — the facade that created it owns its
/// lifetime — so disposing the decorator is a no-op.
/// </summary>
sealed class ProgressStream(Stream inner, ProgressReporter reporter) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            reporter.AddBytes(read);
        }

        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        if (count > 0)
        {
            reporter.AddBytes(count);
        }
    }

    public override void Flush() =>
        inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) =>
        inner.Seek(offset, origin);

    public override void SetLength(long value) =>
        inner.SetLength(value);
}
