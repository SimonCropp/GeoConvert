public class FormatSelectorTests : BunitTestContext
{
    [Test]
    public async Task Render_ListsEveryFormatAsOption()
    {
        var cut = Render<FormatSelector>(_ => _
            .Add(_ => _.Label, "Convert to")
            .Add(_ => _.Formats, ConversionService.WritableFormats)
            .Add(_ => _.Selected, GeoFormat.Kml));

        var options = cut.FindAll("option");

        await Assert.That(options.Count).IsEqualTo(ConversionService.WritableFormats.Count);
        await Assert.That(options.Any(_ => _.TextContent.Contains("PNG image"))).IsTrue();
    }

    [Test]
    public async Task Render_UsesLabel()
    {
        var cut = Render<FormatSelector>(_ => _
            .Add(_ => _.Label, "Convert to")
            .Add(_ => _.Formats, ConversionService.WritableFormats)
            .Add(_ => _.Selected, GeoFormat.Kml));

        await Assert.That(cut.Find("label").TextContent).IsEqualTo("Convert to");
    }

    [Test]
    public async Task Change_RaisesSelectedChanged()
    {
        GeoFormat? selected = null;
        var cut = Render<FormatSelector>(_ => _
            .Add(_ => _.Label, "Convert to")
            .Add(_ => _.Formats, ConversionService.WritableFormats)
            .Add(_ => _.Selected, GeoFormat.Kml)
            .Add(_ => _.SelectedChanged, (GeoFormat format) => selected = format));

        cut.Find("select").Change(nameof(GeoFormat.Gpx));

        await Assert.That(selected).IsEqualTo(GeoFormat.Gpx);
    }
}
