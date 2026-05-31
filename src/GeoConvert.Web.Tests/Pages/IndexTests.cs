using System.Text.RegularExpressions;

public class IndexTests : BunitTestContext
{
    public IndexTests() =>
        JSInterop.Mode = JSRuntimeMode.Loose;

    [Test]
    public Task LayoutStructure()
    {
        var cut = Render<GeoConvert.Web.Pages.Index>();

        return Verify(cut.Markup)
            // InputFile stamps a fresh element-reference GUID each render; pin it so the snapshot is stable.
            .ScrubLinesWithReplace(_ =>
                Regex.Replace(_, "blazor:elementReference=\"[^\"]*\"", "blazor:elementReference=\"scrubbed\""));
    }

    [Test]
    public async Task InitialRender_ShowsUploadPrompt()
    {
        var cut = Render<GeoConvert.Web.Pages.Index>();

        await Assert.That(cut.Find(".file-drop-text strong").TextContent).IsEqualTo("Choose a map file");
        await Assert.That(cut.FindAll(".convert-panel")).IsEmpty();
    }
}
