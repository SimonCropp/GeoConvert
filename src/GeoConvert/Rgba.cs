namespace GeoConvert;

/// <summary>A straight (non-premultiplied) 8-bit-per-channel RGBA color.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba White { get; } = new(255, 255, 255);

    public static Rgba Black { get; } = new(0, 0, 0);

    public static Rgba Transparent { get; } = new(0, 0, 0, 0);
}
