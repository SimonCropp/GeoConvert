namespace GeoConvert;

/// <summary>Thrown when input cannot be parsed, or a conversion is not supported.</summary>
public sealed class GeoConvertException(string message) : Exception(message);
