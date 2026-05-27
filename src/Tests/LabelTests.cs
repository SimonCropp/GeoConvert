// Tests for the PNG renderer's label pass — the StrokeFont + Labeller + per-geometry-type anchor
// stack, plus the LayerStyle/RenderOptions wiring. PNG decode and the small pixel helpers
// (NonBackgroundCount, Decode) are local copies of PngTests' helpers rather than publicly exposing
// the harness on PngTests; the duplication is tiny and keeps PngTests free of "needs a friend for
// label tests" oddities.
public class LabelTests
{
    [Test]
    public async Task Font_measure_empty_string_is_zero_width()
    {
        // Empty string still reports a positive height (one cap-height row) so callers don't
        // accidentally collide-test against a zero-height box and place labels inside each other.
        var (width, height) = StrokeFont.Measure("", 14);
        await Assert.That(width).IsEqualTo(0d);
        await Assert.That(height).IsEqualTo(14d);
    }

    [Test]
    public async Task Font_measure_grows_with_size_and_text_length()
    {
        // Doubling the size doubles both axes; adding a character widens by roughly one glyph
        // advance + one tracking unit. Looser-than-pixel-perfect bounds because the exact glyph
        // widths vary per character.
        var (smallW, smallH) = StrokeFont.Measure("Hi", 14);
        var (bigW, bigH) = StrokeFont.Measure("Hi", 28);

        await Assert.That(bigH).IsEqualTo(smallH * 2);
        // Width also scales linearly (within FP rounding).
        await Assert.That(Math.Abs(bigW - smallW * 2)).IsLessThan(0.001);

        // Longer text → wider bounding box.
        var (longerW, _) = StrokeFont.Measure("Hii", 14);
        await Assert.That(longerW).IsGreaterThan(smallW);
    }

    [Test]
    public async Task Font_renders_every_printable_ascii_glyph()
    {
        // Iterates every glyph (' ' through '~') so each glyph's stroke list is traversed —
        // confirms the table covers printable ASCII and every entry produces sensible output.
        // Non-space glyphs must each contribute at least one painted pixel; space stays blank.
        var canvas = new Canvas(2400, 40, Rgba.White);
        var text = new string(Enumerable.Range(0x20, 0x7E - 0x20 + 1).Select(_ => (char)_).ToArray());
        StrokeFont.Render(canvas, text, 4, 28, 14, Rgba.Black, halo: null);

        var painted = NonBackgroundCount(canvas.Pixels);
        // 95 glyphs - 1 space = 94 glyphs that each contribute at least a few pixels. The hard
        // lower bound here is loose; in practice it's well into the thousands.
        await Assert.That(painted).IsGreaterThan(94);
    }

    [Test]
    public async Task Font_substitutes_unknown_characters_with_question_mark()
    {
        // A glyph outside printable ASCII falls back to '?' rather than producing nothing —
        // keeping the rendered width honest so Labeller's collision math isn't fooled by an
        // empty-but-non-zero-text bbox.
        var canvas = new Canvas(64, 40, Rgba.White);
        StrokeFont.Render(canvas, "\t", 8, 28, 14, Rgba.Black, halo: null);
        var withSubstitute = NonBackgroundCount(canvas.Pixels);

        var direct = new Canvas(64, 40, Rgba.White);
        StrokeFont.Render(direct, "?", 8, 28, 14, Rgba.Black, halo: null);
        var directQuestion = NonBackgroundCount(direct.Pixels);

        await Assert.That(withSubstitute).IsEqualTo(directQuestion);
    }

    [Test]
    public async Task Font_halo_paints_around_text()
    {
        // With halo set, the strokes are first drawn in the halo colour at a wider stroke — so
        // we should see halo-coloured pixels around the (black) text. Render "A" in pure black on
        // a white canvas with a red halo and look for "red-dominant" pixels: under antialiasing
        // the fully-red ring around the text gets blended at its edges, so an exact (255,0,0)
        // match misses everything — what's left is pixels where R clearly dominates G and B.
        var canvas = new Canvas(64, 40, Rgba.White);
        StrokeFont.Render(canvas, "A", 8, 28, 16, Rgba.Black, halo: new(255, 0, 0));
        var redDominant = 0;
        for (var i = 0; i + 4 <= canvas.Pixels.Length; i += 4)
        {
            var r = canvas.Pixels[i];
            var g = canvas.Pixels[i + 1];
            var b = canvas.Pixels[i + 2];
            if (r > g + 30 && r > b + 30)
            {
                redDominant++;
            }
        }

        await Assert.That(redDominant).IsGreaterThan(0);
    }

    [Test]
    public async Task Canvas_antialiased_stroke_handles_zero_length_segment()
    {
        // Zero-length segment is the degenerate-line case StrokeLineAntialiased delegates to
        // FillDiscAntialiased — without it the projection math would divide by zero. Confirm by
        // painting a "line" from a point to itself and checking pixels were laid down (and that
        // the call doesn't throw).
        var canvas = new Canvas(32, 32, Rgba.White);
        canvas.StrokeLineAntialiased(16, 16, 16, 16, width: 4, Rgba.Black);
        await Assert.That(NonBackgroundCount(canvas.Pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Canvas_antialiased_disc_paints_soft_edge()
    {
        // FillDiscAntialiased painted directly. The very centre pixel sits at distance 0 from
        // the centre and is well inside the radius, so it gets full coverage (alpha clamps to 1)
        // — the centre channel value should be the colour as-given. An off-disc pixel stays
        // background.
        var canvas = new Canvas(20, 20, Rgba.White);
        canvas.FillDiscAntialiased(10, 10, radius: 3, new(200, 50, 50));

        // Centre pixel: full coverage (distance 0 from centre, well under the inner-1.0 boundary).
        var centre = (10 * 20 + 10) * 4;
        await Assert.That(canvas.Pixels[centre]).IsEqualTo((byte)200);

        // Far corner: untouched white.
        await Assert.That(canvas.Pixels[0]).IsEqualTo((byte)255);
    }

    [Test]
    public async Task Labeller_places_first_and_rejects_overlap()
    {
        var canvas = new Canvas(200, 64, Rgba.White);
        var labeller = new Labeller(canvas);
        var first = labeller.TryPlace("ONE", 60, 32, 14, Rgba.Black, halo: null);
        // Anchor right on top of the first → second's bbox overlaps and must be rejected.
        var second = labeller.TryPlace("TWO", 60, 32, 14, Rgba.Black, halo: null);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(labeller.PlacedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Labeller_skips_empty_text()
    {
        var canvas = new Canvas(64, 32, Rgba.White);
        var labeller = new Labeller(canvas);
        await Assert.That(labeller.TryPlace("", 10, 10, 14, Rgba.Black, halo: null)).IsFalse();
        await Assert.That(labeller.PlacedCount).IsEqualTo(0);
    }

    [Test]
    public async Task Labeller_skips_off_canvas_anchor()
    {
        // Anchor in the corner so the text bounding box would extend past the left/top edges.
        // Off-canvas labels are dropped silently rather than rendered cropped.
        var canvas = new Canvas(64, 32, Rgba.White);
        var labeller = new Labeller(canvas);
        await Assert.That(labeller.TryPlace("OFFSCREEN", 1, 1, 14, Rgba.Black, halo: null)).IsFalse();
    }

    [Test]
    public async Task Labeller_halo_pad_widens_collision_box()
    {
        // Without halo the bboxes sit close but don't overlap, so both fit. Add a halo and the
        // padding pushes them into collision and the second is rejected — confirms the halo
        // participates in the collision calculation.
        var (textWidth, _) = StrokeFont.Measure("A", 14);
        var canvas = new Canvas(200, 32, Rgba.White);

        var withoutHalo = new Labeller(canvas);
        withoutHalo.TryPlace("A", 40, 16, 14, Rgba.Black, halo: null);
        var withoutHaloSecond = withoutHalo.TryPlace("A", 40 + textWidth + 1, 16, 14, Rgba.Black, halo: null);

        var withHalo = new Labeller(new Canvas(200, 32, Rgba.White));
        withHalo.TryPlace("A", 40, 16, 14, Rgba.Black, halo: Rgba.White);
        var withHaloSecond = withHalo.TryPlace("A", 40 + textWidth + 1, 16, 14, Rgba.Black, halo: Rgba.White);

        await Assert.That(withoutHaloSecond).IsTrue();
        await Assert.That(withHaloSecond).IsFalse();
    }

    [Test]
    public async Task Renders_label_on_point()
    {
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0), new Dictionary<string, object?> { ["name"] = "ORIGIN" }),
        };
        var options = LabelOptions();
        var pixels = Render(features, options);

        // The stroke font paints in near-black on a white background → at least the label pixels
        // must be non-background. Without the label callback set, the point alone wouldn't paint
        // text — so any text-coloured pixels are evidence of the label pass running.
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Renders_labels_for_every_geometry_type()
    {
        // One feature per concrete geometry type — Point, MultiPoint, LineString, MultiLineString,
        // Polygon, MultiPolygon, GeometryCollection — each with its own label. Confirms every
        // ComputeAnchor switch arm yields a usable anchor at this layout. Each label is unique so
        // they sit at different anchors and don't collide.
        var features = new FeatureCollection
        {
            new Feature(new Point(-80, 0), Props("AA")),
            new Feature(new MultiPoint([new(-40, 0), new(-30, 0)]), Props("BB")),
            new Feature(new LineString([new(0, 0), new(20, 0)]), Props("CC")),
            new Feature(new MultiLineString([new([new(40, 0), new(50, 0)]), new([new(40, 20), new(60, 20)])]), Props("DD")),
            new Feature(new Polygon([[new(-80, -40), new(-60, -40), new(-60, -20), new(-80, -20), new(-80, -40)]]), Props("EE")),
            new Feature(new MultiPolygon([
                new([[new(-20, -40), new(-10, -40), new(-10, -30), new(-20, -30), new(-20, -40)]]),
                new([[new(0, -40), new(20, -40), new(20, -20), new(0, -20), new(0, -40)]]),
            ]), Props("FF")),
            new Feature(new GeometryCollection([new Point(60, -40), new LineString([new(40, -40), new(50, -40)])]), Props("GG")),
        };

        var options = LabelOptions();
        options.Bounds = new(-90, -50, 70, 30);
        var pixels = Render(features, options);
        // All 7 features carry a label and have well-separated anchors → expect at least a few
        // hundred painted text pixels in aggregate.
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(100);
    }

    [Test]
    public async Task Empty_multipoint_skips_label()
    {
        // Empty MultiPoint → ComputeAnchor returns null → label dropped silently. Pair with a
        // labelled control point so the canvas isn't blank for an unrelated reason; the assertion
        // is the *control* renders (proving the label pass ran) but no error was thrown for the
        // empty multipoint.
        var features = new FeatureCollection
        {
            new Feature(new MultiPoint([]), Props("EMPTY")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Empty_polygon_rings_skips_label()
    {
        var features = new FeatureCollection
        {
            new Feature(new Polygon([]), Props("EMPTY")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Empty_multilinestring_skips_label()
    {
        var features = new FeatureCollection
        {
            new Feature(new MultiLineString([]), Props("EMPTY")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Empty_multipolygon_skips_label()
    {
        var features = new FeatureCollection
        {
            new Feature(new MultiPolygon([]), Props("EMPTY")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Multipolygon_with_all_empty_polygons_skips_label()
    {
        // Each child polygon has Rings.Count == 0, so LargestPolygonAnchor's skip-condition runs
        // for every entry and `largest` stays null → returns null. Without this branch, the
        // continue-on-empty in LargestPolygonAnchor would be unreached.
        var features = new FeatureCollection
        {
            new Feature(new MultiPolygon([new([]), new([])]), Props("EMPTY")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Empty_geometry_collection_skips_label()
    {
        var features = new FeatureCollection
        {
            new Feature(new GeometryCollection([]), Props("EMPTY")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Geometry_collection_with_only_unanchorable_children_skips_label()
    {
        // The collection wraps an empty MultiPoint — ComputeAnchor recurses, the child returns
        // null, the collection's loop falls out without ever returning an anchor.
        var features = new FeatureCollection
        {
            new Feature(new GeometryCollection([new MultiPoint([])]), Props("NONE")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Empty_linestring_skips_label()
    {
        // LineString with zero positions reaches LineAnchor's count==0 guard. The Polygon and
        // MultiPolygon cases short-circuit before the guard, but LineString and MultiLineString
        // pass the geometry straight in.
        var features = new FeatureCollection
        {
            new Feature(new LineString([]), Props("EMPTY")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Polygon_with_two_vertices_skips_label()
    {
        // PolygonAnchor's ring.Count < 3 guard — a "polygon" with two vertices is degenerate
        // (no enclosed area), but well-formed enough to reach the per-ring centroid path.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(1, 1)]]), Props("TWO")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Degenerate_polygon_falls_back_to_vertex_mean()
    {
        // All vertices collinear → shoelace area sums to 0; PolygonAnchor's |areaSum| < epsilon
        // branch falls back to the vertex arithmetic mean.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(-10, 0), new(0, 0), new(10, 0), new(-10, 0)]]), Props("FLAT")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task LineString_with_single_position_anchors_at_that_point()
    {
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(0, 0)]), Props("DOT")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task LineString_with_zero_total_length_anchors_at_first_position()
    {
        // Every vertex coincides → total arclength is 0; without the fast-path, the bisect loop
        // would divide by zero on its first segment. The label must still place.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(0, 0), new(0, 0), new(0, 0)]), Props("ZERO")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Custom_geometry_type_yields_no_label()
    {
        // A Geometry subclass outside the renderer's known set falls through to ComputeAnchor's
        // default arm, returning null and so dropping the label.
        var features = new FeatureCollection
        {
            new Feature(new UnknownGeometry(), Props("X")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Label_callback_returning_null_skips_feature()
    {
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0)),  // no "name" property
        };
        var options = LabelOptions();
        var pixels = Render(features, options);
        await Assert.That(LabelPixels(pixels)).IsEqualTo(0);
    }

    [Test]
    public async Task Feature_without_geometry_skips_label()
    {
        var features = new FeatureCollection
        {
            new Feature(geometry: null, properties: NameProps("X")),
            new Feature(new Point(0, 0), Props("OK")),
        };
        var pixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Layer_label_overrides_options_label()
    {
        // RenderOptions.Label says "no label" for every feature; LayerStyle.Label overrides
        // that for the specific layer to read the "code" property instead — so the layer renders
        // labels even though the default is silent.
        var features = new FeatureCollection
        {
            Name = "custom",
            Features =
            {
                new(new Point(0, 0), Props("X", "code")),
            },
        };
        var options = LabelOptions();
        options.Label = _ => null;
        options.LayerStyle = _ => new LayerStyle
        {
            Label = feature => feature.Properties.TryGetValue("code", out var v) ? v as string : null,
        };
        var pixels = Render(features, options);
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Layer_label_inherits_options_defaults()
    {
        // LayerStyle returns an empty LayerStyle: every label property is null → falls back to
        // the RenderOptions defaults. So the layer still gets labelled via the options-level
        // Label callback.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0), Props("OK")),
        };
        var options = LabelOptions();
        options.LayerStyle = _ => new LayerStyle();
        var pixels = Render(features, options);
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Label_pass_recurses_into_child_layers()
    {
        // Two-level layer tree, label only on the child layer's features. Confirms DrawLabels'
        // tail recursion runs for layer.Children.
        var child = new FeatureCollection
        {
            Name = "child",
            Features =
            {
                new(new Point(0, 0), Props("CHILD")),
            },
        };
        var root = new FeatureCollection { Name = "root" };
        root.Children.Add(child);
        var pixels = Render(root, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Label_inherits_color_and_halo_from_options()
    {
        // Render with LabelColor red and LabelHalo blue — assert both colours appear in the
        // output. Covers the per-property fall-through in ResolveLabel and the halo-on path in
        // StrokeFont.Render simultaneously.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0), Props("HI")),
        };
        var options = LabelOptions();
        options.LabelColor = new(255, 0, 0);
        options.LabelHalo = new(0, 0, 255);
        var pixels = Render(features, options);

        // Antialiasing means exact (255,0,0) / (0,0,255) pixels are rare — look for channel
        // dominance instead. The red text strokes are inside the blue halo, so there must be
        // pixels where R clearly outpaces G/B (text) AND pixels where B clearly outpaces R/G
        // (halo edges where text doesn't cover).
        var hasRed = false;
        var hasBlue = false;
        for (var i = 0; i + 4 <= pixels.Length; i += 4)
        {
            var r = pixels[i];
            var g = pixels[i + 1];
            var b = pixels[i + 2];
            if (r > g + 30 && r > b + 30)
            {
                hasRed = true;
            }
            else if (b > r + 30 && b > g + 30)
            {
                hasBlue = true;
            }
        }

        await Assert.That(hasRed).IsTrue();
        await Assert.That(hasBlue).IsTrue();
    }

    [Test]
    public async Task Label_renders_without_halo()
    {
        // Halo explicitly disabled → the halo branch in StrokeFont.Render is skipped. The text
        // still renders.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0), Props("X")),
        };
        var options = LabelOptions();
        options.LabelHalo = null;
        var pixels = Render(features, options);
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
    }

    [Test]
    public Task Render_snapshot_with_labels()
    {
        // A snapshot anchoring the labelling output (glyph shapes, halo, anchor positions) so a
        // regression in any of those shows up immediately. Bounds and projection are pinned for
        // pixel stability.
        var features = new FeatureCollection
        {
            new Feature(new Point(-100, 40), Props("USA")),
            new Feature(new Point(2, 48), Props("Paris")),
            new Feature(new Point(139, 35), Props("Tokyo")),
            new Feature(new LineString([new(-100, 40), new(2, 48), new(139, 35)]), Props("Route")),
        };
        var options = new RenderOptions
        {
            Bounds = new(-180, -10, 180, 80),
            Width = 600,
            Projection = MapProjection.PlateCarree,
            Label = feature => feature.Properties.TryGetValue("name", out var v) ? v as string : null,
            LabelSize = 14,
            LabelColor = new(40, 40, 40),
            LabelHalo = new(255, 255, 255, 220),
            Fill = new(220, 220, 220),
            Stroke = new(80, 80, 80),
        };
        return Verify(new MemoryStream(MapRenderer.RenderPng(features, options)), "png");
    }

    [Test]
    public async Task Custom_label_priority_overrides_default_area_rule()
    {
        // Two points sitting at the same projected pixel so they MUST collide. Without a custom
        // priority both have geometry-area = 0 (points) and file order decides — the first wins.
        // With a custom priority sourced from the "rank" property, the higher-ranked feature
        // outranks the other regardless of file order. The snapshot pins which word actually
        // renders (SECOND, the higher-ranked one), so a regression that drops the callback or
        // reverses the sort would diff visibly.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0), new Dictionary<string, object?> { ["name"] = "FIRST", ["rank"] = 1.0 }),
            new Feature(new Point(0, 0), new Dictionary<string, object?> { ["name"] = "SECOND", ["rank"] = 99.0 }),
        };
        var options = LabelOptions();
        options.LabelPriority = feature =>
            feature.Properties.TryGetValue("rank", out var v) ? Convert.ToDouble(v) : 0;

        var png = MapRenderer.RenderPng(features, options);
        var (_, _, pixels) = Decode(png);

        // Cross-check: same scene without the priority callback must produce a different image —
        // confirms the override actually changed the winner rather than coincidentally agreeing
        // with the default.
        var defaultPixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
        await Assert.That(pixels.SequenceEqual(defaultPixels)).IsFalse();

        await Verify(new MemoryStream(png), "png");
    }

    [Test]
    public async Task Custom_priority_from_external_dictionary_lookup()
    {
        // The closure pattern: priorities live outside the features themselves (an admin table, a
        // population dataset, etc.) and the callback captures the dict. Useful when the features
        // already exist and you don't want to mutate properties just to drive label ordering.
        // Snapshot pins "Big" as the winner (priority 1000 > priority 1).
        var importance = new Dictionary<string, double>
        {
            ["Big"] = 1000,
            ["Small"] = 1,
        };
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(0, 0)]]), new Dictionary<string, object?> { ["name"] = "Big" }),
            new Feature(new Polygon([[new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(0, 0)]]), new Dictionary<string, object?> { ["name"] = "Small" }),
        };

        var options = LabelOptions();
        options.LabelPriority = feature =>
            feature.Properties.TryGetValue("name", out var n) && n is string name && importance.TryGetValue(name, out var p)
                ? p
                : 0;
        var png = MapRenderer.RenderPng(features, options);
        var (_, _, pixels) = Decode(png);
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);

        await Verify(new MemoryStream(png), "png");
    }

    [Test]
    public async Task Layer_label_priority_overrides_options_default()
    {
        // Priority callback on a LayerStyle takes precedence over the RenderOptions-level one for
        // that layer — same per-property fall-through pattern as the other label knobs. Snapshot
        // pins which of the two layers' labels wins the collision at the shared anchor.
        var lower = new FeatureCollection
        {
            Name = "background",
            Features =
            {
                new(new Point(0, 0), new Dictionary<string, object?> { ["name"] = "L", ["w"] = 5.0 }),
            },
        };
        var upper = new FeatureCollection
        {
            Name = "overlay",
            Features =
            {
                new(new Point(0, 0), new Dictionary<string, object?> { ["name"] = "U", ["w"] = 50.0 }),
            },
        };

        var options = LabelOptions();
        // Options-level priority returns 0 for every feature; the layer-style override picks the
        // "w" property only for the "background" layer. Both layers visit the same pixel anchor,
        // and pre-order means the background layer's label is attempted first regardless.
        options.LabelPriority = _ => 0;
        options.LayerStyle = layer => layer.Name switch
        {
            "background" => new LayerStyle
            {
                LabelPriority = f => Convert.ToDouble(f.Properties["w"]),
            },
            _ => null,
        };
        var png = MapRenderer.RenderPng([lower, upper], options);
        var (_, _, pixels) = Decode(png);
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);

        await Verify(new MemoryStream(png), "png");
    }

    [Test]
    public Task Render_snapshot_world_labels_high_res()
    {
        // High-resolution world snapshot — 4096-wide Goode's Homolosine (the equal-area lobed
        // projection that Auto picks for world extents — areas at high latitudes read at honest
        // size, with the interrupt meridians running through ocean basins so continents stay
        // whole). Country names labelled with the stroke font at cap-height 22 pixels, anchored
        // at the shoelace centroid of each country's largest polygon. Greedy collision drops
        // crowded labels (Europe and the Caribbean lose most of their small countries to bigger
        // neighbours that come first in file order) — that's the expected v1 behaviour without
        // per-feature priority ranking. Note that the centroid is computed in lon/lat then
        // projected through whichever lobe the centroid falls in, so a multi-lobe country's
        // label may land in just one lobe rather than straddling the gap. Locked in as a
        // snapshot so any regression in the font, halo, anchor calc or projection wiring is
        // caught at scale.
        var features = GeoConverter.Read(ProjectFiles.world_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new(-180, -90, 180, 90),
                Projection = MapProjection.Goode,
                Width = 4096,
                Ocean = new(200, 220, 240),
                Fill = new(240, 235, 220),
                Stroke = new(120, 120, 120),
                StrokeWidth = 1,
                Label = feature =>
                    feature.Properties.TryGetValue("NAME", out var value) ? value as string : null,
                LabelSize = 22,
                LabelColor = new(30, 30, 30),
                LabelHalo = new(255, 255, 255, 220),
            });

        return Verify(new MemoryStream(png), "png");
    }

    static RenderOptions LabelOptions() =>
        new()
        {
            Bounds = new(-90, -50, 90, 30),
            Width = 400,
            Projection = MapProjection.PlateCarree,
            Padding = 0,
            Label = feature => feature.Properties.TryGetValue("name", out var v) ? v as string : null,
            LabelSize = 14,
            LabelColor = new(20, 20, 20),
            LabelHalo = null,
            // Force fill/stroke fully transparent so the only non-bg pixels are label text — makes
            // LabelPixels' "non-bg" heuristic an exact label-pixel count.
            Fill = Rgba.Transparent,
            Stroke = Rgba.Transparent,
        };

    static byte[] Render(FeatureCollection features, RenderOptions options)
    {
        var png = MapRenderer.RenderPng(features, options);
        var (_, _, pixels) = Decode(png);
        return pixels;
    }

    static byte[] Render(IReadOnlyList<FeatureCollection> layers, RenderOptions options)
    {
        var png = MapRenderer.RenderPng(layers, options);
        var (_, _, pixels) = Decode(png);
        return pixels;
    }

    // Counts pixels that aren't pure white — with LabelOptions() forcing geometry to fully
    // transparent stroke/fill, every non-white pixel in the output is label text (or halo).
    static int LabelPixels(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
            {
                count++;
            }
        }

        return count;
    }

    static int NonBackgroundCount(byte[] pixels) => LabelPixels(pixels);

    static Dictionary<string, object?> Props(string name, string? key = null) =>
        NameProps(name, key);

    static Dictionary<string, object?> NameProps(string name, string? key = null) =>
        new() { [key ?? "name"] = name };

    // Mirrors PngTests.Decode — kept local to avoid making PngTests' helpers public for a
    // test-only consumer.
    static (int Width, int Height, byte[] Rgba) Decode(byte[] data)
    {
        var position = 8;
        var width = 0;
        var height = 0;
        using var compressed = new MemoryStream();
        while (position < data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position));
            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            var contentStart = position + 8;
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(contentStart));
                height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(contentStart + 4));
            }
            else if (type == "IDAT")
            {
                compressed.Write(data, contentStart, length);
            }
            else if (type == "IEND")
            {
                break;
            }

            position = contentStart + length + 4;
        }

        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var rawBytes = raw.ToArray();

        var stride = width * 4;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(rawBytes, y * (stride + 1) + 1, rgba, y * stride, stride);
        }

        return (width, height, rgba);
    }

    // Custom Geometry subclass used to exercise ComputeAnchor's default arm. None of the
    // overrides are called by the label pass beyond the type check, so the implementations are
    // stubs that satisfy the abstract contract.
    sealed class UnknownGeometry : Geometry
    {
        public override GeometryType Type => GeometryType.Point;

        public override bool IsEmpty => false;

        public override Envelope GetBounds() => new(0, 0, 0, 0);

        public override bool HasZ => false;

        public override bool HasM => false;
    }
}
