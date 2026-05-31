namespace GeoConvert.Web.Services;

/// <summary>Browser-facing metadata for a <see cref="GeoFormat"/>: how to label, name and serve it.</summary>
public record FormatInfo(
    GeoFormat Format,
    string DisplayName,
    string Extension,
    string ContentType,
    bool CanRead,
    bool CanWrite);
