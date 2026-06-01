public class IssueLauncherTests
{
    [Test]
    public async Task ForException_BuildsPrefilledIssueUrl()
    {
        // A constructed (never thrown) exception has a null stack trace, so ToString() is deterministic.
        var exception = new InvalidOperationException("boom");

        var url = IssueLauncher.ForException("Could not read the map", exception, "* GeoConvert version: 1.2.3");

        await Assert.That(url).StartsWith("https://github.com/Papyrine/GeoConvert/issues/new?title=");

        var decoded = WebUtility.UrlDecode(url);
        await Assert.That(decoded).Contains("Could not read the map: InvalidOperationException");
        await Assert.That(decoded).Contains("* Action: Could not read the map");
        await Assert.That(decoded).Contains("* GeoConvert version: 1.2.3");
        await Assert.That(decoded).Contains("InvalidOperationException: boom");
    }

    [Test]
    public async Task ForException_WithoutEnvironment_OmitsEnvironmentBlock()
    {
        var url = IssueLauncher.ForException("Boom", new("oops"));

        var decoded = WebUtility.UrlDecode(url);
        await Assert.That(decoded).Contains("* Action: Boom");
        await Assert.That(decoded).DoesNotContain("* GeoConvert version:");
    }
}
