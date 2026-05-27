public class CliTests
{
    [Test]
    public Task Help() =>
        VerifyConsole(["--help"]);

    [Test]
    public Task List() =>
        VerifyConsole(["--list"]);

    [Test]
    public async Task ConvertsByExtension()
    {
        using var directory = new TempDirectory();

        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Mixed()));
        var output = Path.Combine(directory, "out.kml");

        var code = Runner.Run([input, output], new StringWriter(), new StringWriter());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsTrue();
    }

    [Test]
    public async Task MissingInputReturnsError()
    {
        var error = new StringWriter();
        var code = Runner.Run(["does-not-exist.geojson", "out.kml"], new StringWriter(), error);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(error.ToString()).Contains("not found");
    }

    [Test]
    public async Task RendersPngWithBoundingBox()
    {
        using var directory = new TempDirectory();

        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.png");

        var code = Runner.Run(
            [input, output, "--bbox", "-1,-1,16,16", "--size", "128x128"],
            new StringWriter(),
            new StringWriter());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(output))[..4])
            .IsEquivalentTo(new byte[]
            {
                0x89,
                0x50,
                0x4E,
                0x47
            });
    }

    [Test]
    public async Task NoArgumentsReturnsUsage() =>
        await Assert.That(Runner.Run([], new StringWriter(), new StringWriter())).IsEqualTo(2);

    [Test]
    public async Task ForcedFormatsConvert()
    {
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.data");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Mixed()));
        var output = Path.Combine(directory, "out.data");
        var code = Runner.Run(
            [input, output, "--from", "geojson", "--to", "wkt"],
            new StringWriter(),
            new StringWriter());
        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    [Arguments("plate-carree")]
    [Arguments("PlateCarree")]
    [Arguments("equirectangular")]
    [Arguments("web-mercator")]
    [Arguments("Mercator")]
    [Arguments("WEBMERCATOR")]
    [Arguments("lambert")]
    [Arguments("lambert-conformal-conic")]
    [Arguments("LCC")]
    [Arguments("goode")]
    [Arguments("Homolosine")]
    [Arguments("goode-homolosine")]
    [Arguments("goodes-homolosine")]
    [Arguments("auto")]
    [Arguments("Automatic")]
    public async Task RendersPngWithProjection(string projection)
    {
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.png");

        var code = Runner.Run(
            [input, output, "--projection", projection, "--size", "100"],
            new StringWriter(),
            new StringWriter());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsTrue();
    }

    [Test]
    public async Task RendersPngWithSizeOnly()
    {
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.png");
        var code = Runner.Run([input, output, "--size", "100"], new StringWriter(), new StringWriter());
        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    [Arguments(new[] { "a", "b", "--from", "nope" }, 2)]
    [Arguments(new[] { "a", "b", "--from" }, 2)]
    [Arguments(new[] { "a", "b", "--to" }, 2)]
    [Arguments(new[] { "a", "b", "--bbox" }, 2)]
    [Arguments(new[] { "a", "b", "--bbox", "1,2,3" }, 2)]
    [Arguments(new[] { "a", "b", "--bbox", "a,b,c,d" }, 2)]
    [Arguments(new[] { "a", "b", "--size" }, 2)]
    [Arguments(new[] { "a", "b", "--size", "axb" }, 2)]
    [Arguments(new[] { "a", "b", "--size", "10x0" }, 2)]
    [Arguments(new[] { "a", "b", "--size", "1x2x3" }, 2)]
    [Arguments(new[] { "a", "b", "--projection" }, 2)]
    [Arguments(new[] { "a", "b", "--projection", "moon" }, 2)]
    [Arguments(new[] { "a", "b", "--label" }, 2)]
    [Arguments(new[] { "a", "b", "--label-size" }, 2)]
    [Arguments(new[] { "a", "b", "--label-size", "0" }, 2)]
    [Arguments(new[] { "a", "b", "--label-size", "abc" }, 2)]
    [Arguments(new[] { "a", "b", "--label-color" }, 2)]
    [Arguments(new[] { "a", "b", "--label-color", "red" }, 2)]
    [Arguments(new[] { "a", "b", "--label-color", "#GGG" }, 2)]
    [Arguments(new[] { "a", "b", "--label-color", "#ZZZZZZ" }, 2)]
    [Arguments(new[] { "a", "b", "--label-color", "#FFFFFFZZ" }, 2)]
    [Arguments(new[] { "a", "b", "--label-color", "#FFFFFFF" }, 2)]
    [Arguments(new[] { "a", "b", "--label-color", "FFFFFF" }, 2)]
    [Arguments(new[] { "a", "b", "--label-halo" }, 2)]
    [Arguments(new[] { "a", "b", "--label-halo", "red" }, 2)]
    [Arguments(new[] { "--unknown" }, 2)]
    public async Task InvalidArguments(string[] args, int expected) =>
        await Assert.That(Runner.Run(args, new StringWriter(), new StringWriter())).IsEqualTo(expected);

    [Test]
    public async Task RendersPngWithLabels()
    {
        // End-to-end: --label reads the named property, --label-scale grows the text,
        // --label-color picks the text colour, --label-halo accepts a hex with alpha. Validation
        // that the resulting PNG exists is enough — the label-pixel checks live in LabelTests.
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.png");

        var code = Runner.Run(
            [input, output, "--size", "300", "--label", "name", "--label-size", "18", "--label-color", "#102030", "--label-halo", "#FFFFFFCC"],
            new StringWriter(),
            new StringWriter());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsTrue();
    }

    [Test]
    public async Task RendersPngWithLabelHaloNone()
    {
        // 'none' explicitly suppresses the halo (distinct from leaving the flag off entirely,
        // which inherits the default semi-transparent white). Exercises the labelHaloExplicit=true
        // path with labelHalo=null.
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.png");

        var code = Runner.Run(
            [input, output, "--size", "200", "--label", "name", "--label-halo", "none"],
            new StringWriter(),
            new StringWriter());

        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    public async Task LabelWithMissingPropertyRendersAnyway()
    {
        // The --label callback returns null for features missing the requested key, so they
        // simply don't get labelled — the render shouldn't error.
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        // Sample.Polygons features carry "name" but not "missing".
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Polygons()));
        var output = Path.Combine(directory, "out.png");

        var code = Runner.Run(
            [input, output, "--size", "100", "--label", "missing"],
            new StringWriter(),
            new StringWriter());

        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    public async Task BadConversionReturnsError()
    {
        using var directory = new TempDirectory();
        // A shapefile holds a single geometry category, so mixed geometry cannot be written.
        var input = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Mixed()));
        var output = Path.Combine(directory, "out.shp");
        var code = Runner.Run([input, output], new StringWriter(), new StringWriter());
        await Assert.That(code).IsEqualTo(1);
    }

    static Task VerifyConsole(string[] args)
    {
        var output = new StringWriter();
        Runner.Run(args, output, new StringWriter());
        return Verify(output.ToString());
    }
}
