using System.Net;

namespace GeoConvert.Web.Services;

/// <summary>
/// Builds a pre-filled GitHub "new issue" URL when an unexpected exception escapes a conversion, so a
/// user can report it in a single click. Modelled on DiffEngineTray's <c>IssueLauncher</c>, adapted for
/// the browser: there is no message box or <c>Process.Start</c>, so instead of opening the URL itself it
/// hands the URL back to be surfaced as a link the user can choose to follow (the web-native equivalent
/// of DiffEngineTray's <c>LinkLauncher</c>).
/// </summary>
public static class IssueLauncher
{
    const string NewIssue = "https://github.com/Papyrine/GeoConvert/issues/new";

    /// <summary>
    /// Produces a `…/issues/new?title=…&amp;body=…` URL describing <paramref name="exception"/>.
    /// <paramref name="action"/> is the user-facing operation that failed (it becomes the issue title);
    /// <paramref name="environment"/> is an optional block of pre-formatted Markdown bullet lines
    /// (e.g. app version, user agent) included verbatim in the body.
    /// </summary>
    public static string ForException(string action, Exception exception, string? environment = null)
    {
        var title = WebUtility.UrlEncode($"{action}: {exception.GetType().Name}");

        var lines = new List<string>
        {
            "An unexpected error occurred in the GeoConvert web app.",
            "",
            $"* Action: {action}",
        };

        if (!string.IsNullOrWhiteSpace(environment))
        {
            lines.Add(environment);
        }

        lines.Add("* Exception:");
        lines.Add("```");
        lines.Add(exception.ToString());
        lines.Add("```");

        var body = WebUtility.UrlEncode(string.Join("\n", lines));

        return $"{NewIssue}?title={title}&body={body}";
    }
}
