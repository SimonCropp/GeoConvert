public class ConversionProgressTests : BunitTestContext
{
    [Test]
    public async Task DeterminateReport_RendersProportionalFill()
    {
        var cut = Render<ConversionProgress>(_ => _
            .Add(_ => _.Label, "Writing GeoJSON…")
            .Add(_ => _.Report, new ConvertProgress(ProgressPhase.Writing, 5, 10, 0, null)));

        var fill = cut.Find(".progress-fill");

        await Assert.That(fill.GetAttribute("style")).Contains("width:50%");
        await Assert.That(fill.ClassList).DoesNotContain("progress-fill-indeterminate");
        await Assert.That(cut.Find(".progress-count").TextContent).Contains("5 features");
    }

    [Test]
    public async Task NoFraction_RendersIndeterminateBar()
    {
        var cut = Render<ConversionProgress>(_ => _
            .Add(_ => _.Label, "Reading map…")
            .Add(_ => _.Report, null));

        await Assert.That(cut.Find(".progress-fill").ClassList).Contains("progress-fill-indeterminate");
        await Assert.That(cut.Find(".progress-label span").TextContent).IsEqualTo("Reading map…");
        await Assert.That(cut.FindAll(".progress-count")).IsEmpty();
    }

    [Test]
    public async Task SingleFeature_UsesSingularLabel()
    {
        var cut = Render<ConversionProgress>(_ => _
            .Add(_ => _.Label, "Writing…")
            .Add(_ => _.Report, new ConvertProgress(ProgressPhase.Writing, 1, 1, 0, null)));

        await Assert.That(cut.Find(".progress-count").TextContent).Contains("1 feature");
    }

    [Test]
    public async Task ReadingReport_ShowsBytesAsRatioOfTotal()
    {
        var cut = Render<ConversionProgress>(_ => _
            .Add(_ => _.Label, "Reading map…")
            // 512 of 2048 bytes read; no feature total during a read.
            .Add(_ => _.Report, new ConvertProgress(ProgressPhase.Reading, 0, null, 512, 2048)));

        var count = cut.Find(".progress-count").TextContent;

        await Assert.That(count).Contains("512 B / 2.0 KB");
        await Assert.That(cut.Find(".progress-fill").GetAttribute("style")).Contains("width:25%");
    }

    [Test]
    public async Task WritingReport_ShowsFeaturesAndBytesTogether()
    {
        var cut = Render<ConversionProgress>(_ => _
            .Add(_ => _.Label, "Writing GeoJSON…")
            // FeatureTotal known on write, byte total not — so bytes show without a "/".
            .Add(_ => _.Report, new ConvertProgress(ProgressPhase.Writing, 3, 6, 1536, null)));

        var count = cut.Find(".progress-count").TextContent;

        await Assert.That(count).Contains("3 features");
        await Assert.That(count).Contains("1.5 KB");
        await Assert.That(count).DoesNotContain("/");
    }

    [Test]
    public async Task Indeterminate_ForcesAnimatedBarEvenWhenFractionIsComplete()
    {
        var cut = Render<ConversionProgress>(_ => _
            .Add(_ => _.Label, "Reading map…")
            // Byte fraction is 100% (buffered read), but Indeterminate must still animate.
            .Add(_ => _.Report, new ConvertProgress(ProgressPhase.Reading, 5, null, 2048, 2048))
            .Add(_ => _.Indeterminate, true));

        await Assert.That(cut.Find(".progress-fill").ClassList).Contains("progress-fill-indeterminate");
        // The detail line still reports what's been processed.
        await Assert.That(cut.Find(".progress-count").TextContent).Contains("5 features");
    }

    [Test]
    public async Task LargeByteCount_FormatsAsMegabytes()
    {
        var cut = Render<ConversionProgress>(_ => _
            .Add(_ => _.Label, "Reading map…")
            .Add(_ => _.Report, new ConvertProgress(ProgressPhase.Reading, 0, null, 3 * 1024 * 1024, null)));

        await Assert.That(cut.Find(".progress-count").TextContent).Contains("3.0 MB");
    }
}
