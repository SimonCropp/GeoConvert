namespace GeoConvert.Web.Services;

/// <summary>Build/version facts worth attaching to a bug report.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(GeoConverter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(GeoConverter).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Pre-formatted Markdown bullet lines describing the runtime, for an issue body.</summary>
    public static string Environment(string? userAgent) =>
        string.Join(
            '\n',
            $"* GeoConvert version: {Version}",
            $"* User agent: {userAgent}");
}
