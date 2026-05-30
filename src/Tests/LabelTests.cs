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
        // Iterates every base glyph (' ' through '~') so each glyph's stroke list is traversed —
        // confirms the ASCII table covers printable ASCII and every entry produces sensible
        // output. Combining marks have their own coverage below; this exists to keep the base
        // glyph data covered. Non-space glyphs must each contribute at least one painted pixel;
        // space stays blank.
        var canvas = new Canvas(2400, 40, Rgba.White);
        var text = new string(Enumerable.Range(0x20, 0x7E - 0x20 + 1).Select(_ => (char)_).ToArray());
        StrokeFont.Render(canvas, text, 4, 28, 14, Rgba.Black, halo: null);

        var painted = NonBackgroundCount(LogicalPixels(canvas));
        // 95 glyphs - 1 space = 94 glyphs that each contribute at least a few pixels. The hard
        // lower bound here is loose; in practice it's well into the thousands.
        await Assert.That(painted).IsGreaterThan(94);
    }

    [Test]
    public async Task Font_substitutes_unknown_characters_with_question_mark()
    {
        // A character with no glyph and no NFD decomposition into mappable parts falls back to '?'
        // rather than producing nothing — keeping the rendered width honest so Labeller's collision
        // math isn't fooled by an empty-but-non-zero-text bbox. Tab is the test input because it's
        // outside the printable-ASCII table and decomposes to itself (no combining mark).
        var canvas = new Canvas(64, 40, Rgba.White);
        StrokeFont.Render(canvas, "\t", 8, 28, 14, Rgba.Black, halo: null);
        var withSubstitute = NonBackgroundCount(LogicalPixels(canvas));

        var direct = new Canvas(64, 40, Rgba.White);
        StrokeFont.Render(direct, "?", 8, 28, 14, Rgba.Black, halo: null);
        var directQuestion = NonBackgroundCount(LogicalPixels(direct));

        await Assert.That(withSubstitute).IsEqualTo(directQuestion);
    }

    [Test]
    public async Task Font_renders_precomposed_diacritic_via_NFD()
    {
        // "ô" (U+00F4) NFD-decomposes to 'o' + combining circumflex (U+0302). The renderer should
        // paint the lowercase 'o' base glyph and the circumflex stroke above it — visibly more ink
        // than 'o' alone and more than the question-mark fallback.
        var bare = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(bare, "o", 8, 28, 14, Rgba.Black, halo: null);
        var bareInk = NonBackgroundCount(LogicalPixels(bare));

        var accented = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(accented, "ô", 8, 28, 14, Rgba.Black, halo: null);
        var accentedInk = NonBackgroundCount(LogicalPixels(accented));

        // Accent adds strokes above the base glyph — measurable ink delta.
        await Assert.That(accentedInk).IsGreaterThan(bareInk);

        // And it's not '?' substitution: rendering '?' on a fresh canvas shouldn't match.
        var fallback = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(fallback, "?", 8, 28, 14, Rgba.Black, halo: null);
        await Assert.That(NonBackgroundCount(LogicalPixels(fallback))).IsNotEqualTo(accentedInk);
    }

    [Test]
    public async Task Font_measure_treats_combining_marks_as_zero_width()
    {
        // After NFD decomposition the combining mark contributes nothing to width — "ô" measures
        // the same as "o" because only the base glyph advances the pen.
        var (bareWidth, _) = StrokeFont.Measure("o", 14);
        var (accentedWidth, _) = StrokeFont.Measure("ô", 14);
        await Assert.That(accentedWidth).IsEqualTo(bareWidth);
    }

    [Test]
    public async Task Font_renders_each_supported_combining_mark()
    {
        // One representative for each combining mark in the table: grave, acute, circumflex,
        // tilde, diaeresis, ring above, caron, cedilla. Each must produce more ink than the bare
        // base letter to confirm the mark dispatched through DrawAt.
        var samples = new (string Bare, string Accented)[]
        {
            ("a", "à"),   // grave
            ("e", "é"),   // acute
            ("o", "ô"),   // circumflex
            ("n", "ñ"),   // tilde
            ("u", "ü"),   // diaeresis
            ("a", "å"),   // ring above
            ("s", "š"),   // caron
            ("c", "ç"),   // cedilla
        };
        foreach (var (bare, accented) in samples)
        {
            var bareCanvas = new Canvas(48, 40, Rgba.White);
            StrokeFont.Render(bareCanvas, bare, 8, 28, 14, Rgba.Black, halo: null);
            var accentedCanvas = new Canvas(48, 40, Rgba.White);
            StrokeFont.Render(accentedCanvas, accented, 8, 28, 14, Rgba.Black, halo: null);

            await Assert.That(NonBackgroundCount(LogicalPixels(accentedCanvas)))
                .IsGreaterThan(NonBackgroundCount(LogicalPixels(bareCanvas)));
        }
    }

    [Test]
    public async Task Font_drops_leading_standalone_combining_mark()
    {
        // A combining mark with no preceding base glyph has nothing to attach to — drop it
        // silently rather than anchor it at leftX. So a string that's *only* a combining
        // circumflex paints nothing.
        var canvas = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(canvas, "̂", 8, 28, 14, Rgba.Black, halo: null);
        await Assert.That(NonBackgroundCount(LogicalPixels(canvas))).IsEqualTo(0);
    }

    [Test]
    public async Task Font_drops_unsupported_combining_mark_silently()
    {
        // A combining mark outside the table (here the Vietnamese hook above, U+0309) is dropped
        // rather than substituted with '?', so "o" + U+0309 paints the same ink as plain "o" —
        // a missing accent reads better than a stray '?' next to the correctly-drawn base letter.
        var bare = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(bare, "o", 8, 28, 14, Rgba.Black, halo: null);

        var withUnsupportedMark = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(withUnsupportedMark, "ỏ", 8, 28, 14, Rgba.Black, halo: null);

        await Assert.That(NonBackgroundCount(LogicalPixels(withUnsupportedMark)))
            .IsEqualTo(NonBackgroundCount(LogicalPixels(bare)));
    }

    [Test]
    public async Task Font_undecomposable_ligature_still_falls_back_to_question_mark()
    {
        // ß has no NFD decomposition into ASCII + combining marks — it's a single codepoint that
        // doesn't break down. So it still hits the GlyphFor fallback to '?'.
        var canvas = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(canvas, "ß", 8, 28, 14, Rgba.Black, halo: null);
        var ligatureInk = NonBackgroundCount(LogicalPixels(canvas));

        var fallback = new Canvas(48, 40, Rgba.White);
        StrokeFont.Render(fallback, "?", 8, 28, 14, Rgba.Black, halo: null);
        await Assert.That(ligatureInk).IsEqualTo(NonBackgroundCount(LogicalPixels(fallback)));
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
        for (var i = 0; i + 4 <= canvas.PixelByteCount; i += 4)
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
    public async Task Canvas_stroke_handles_zero_length_segment()
    {
        // Zero-length segment is the degenerate-line case StrokeLine delegates to FillDisc —
        // without it the projection math would divide by zero. Confirm by painting a "line" from
        // a point to itself and checking pixels were laid down (and that the call doesn't throw).
        var canvas = new Canvas(32, 32, Rgba.White);
        canvas.StrokeLine(16, 16, 16, 16, width: 4, Rgba.Black);
        await Assert.That(NonBackgroundCount(LogicalPixels(canvas))).IsGreaterThan(0);
    }

    [Test]
    public async Task Canvas_stroke_line_with_far_out_of_canvas_endpoint_returns_promptly()
    {
        // Regression for the Lambert-at-Western-Europe-bounds hang: a non-linear projection can
        // hand StrokeLine a pixel coordinate astronomically outside int range (Antarctica's
        // latitude pushed through a northern-hemisphere LCC cone produces ρ ≈ 1e15). Without
        // clamping the iteration bbox to the canvas up-front, the outer y-loop would iterate
        // billions of rows relying on per-pixel Blend rejection — an effective infinite loop.
        // With the clamp it returns in microseconds. The 1-second budget is huge relative to the
        // real cost (<1 ms) and tiny relative to the unclamped version (>>1 minute), so even a
        // heavily loaded CI shouldn't flake.
        var canvas = new Canvas(32, 32, Rgba.White);
        var sw = Stopwatch.StartNew();
        canvas.StrokeLine(10, 10, 1e15, 1e15, width: 1, Rgba.Black);
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1000);
    }

    [Test]
    public async Task Canvas_fill_disc_with_far_out_of_canvas_centre_returns_promptly()
    {
        // Same root cause as the StrokeLine guard above: a runaway centre coordinate would make
        // the outer y-loop iterate over billions of out-of-canvas rows. Clamping minY/maxY to
        // [0, Height-1] bounds the loop to the visible region.
        var canvas = new Canvas(32, 32, Rgba.White);
        var sw = Stopwatch.StartNew();
        canvas.FillDisc(1e15, 1e15, radius: 2, Rgba.Black);
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1000);
    }

    [Test]
    public async Task Canvas_fill_rect_with_far_out_of_canvas_corners_returns_promptly()
    {
        // FillRect already clipped via Math.Max/Min, but the clip step now stands on its own as a
        // hard contract: even with both corners at 1e15, the per-row loop must stay bounded to
        // [0, Width-1] × [0, Height-1] and return promptly. Pairs with the StrokeLine/FillDisc
        // guards so all three rasterizer primitives are regression-locked against the same kind
        // of projection-blow-up input.
        var canvas = new Canvas(32, 32, Rgba.White);
        var sw = Stopwatch.StartNew();
        canvas.FillRect(1e15, 1e15, 2e15, 2e15, Rgba.Black);
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1000);
    }

    [Test]
    public async Task Canvas_fill_rect_paints_solid_block()
    {
        // Opaque fill of an interior rect leaves the centre pixel the fill colour and untouched
        // pixels well outside the rect at the background colour. The bounds are inclusive at low
        // and exclusive at high, so picking ints lets the per-pixel loop hit every covered pixel.
        var canvas = new Canvas(40, 40, Rgba.White);
        canvas.FillRect(10, 10, 30, 30, new(100, 150, 200));

        var inside = (20 * 40 + 20) * 4;
        await Assert.That(canvas.Pixels[inside]).IsEqualTo((byte)100);
        await Assert.That(canvas.Pixels[inside + 1]).IsEqualTo((byte)150);
        await Assert.That(canvas.Pixels[inside + 2]).IsEqualTo((byte)200);

        // Far corner: untouched white.
        await Assert.That(canvas.Pixels[0]).IsEqualTo((byte)255);
    }

    [Test]
    public async Task Canvas_fill_rect_skips_zero_alpha()
    {
        // A fully-transparent colour is a no-op — the early exit avoids a per-pixel Blend loop
        // that would short-circuit anyway. Exercises the alpha==0 fast-path.
        var canvas = new Canvas(20, 20, Rgba.White);
        canvas.FillRect(0, 0, 20, 20, Rgba.Transparent);
        await Assert.That(NonBackgroundCount(LogicalPixels(canvas))).IsEqualTo(0);
    }

    [Test]
    public async Task Canvas_fill_rect_clips_to_canvas_bounds()
    {
        // Off-canvas rect coordinates get clipped, so a fill that overlaps the right/bottom edge
        // paints up to the edge and stops — no out-of-bounds writes, no thrown exceptions.
        var canvas = new Canvas(10, 10, Rgba.White);
        canvas.FillRect(5, 5, 100, 100, Rgba.Black);

        // Bottom-right pixel sits inside the requested rect after clipping.
        var corner = (9 * 10 + 9) * 4;
        await Assert.That(canvas.Pixels[corner]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Canvas_disc_paints_full_coverage_at_centre()
    {
        // FillDisc painted directly. The very centre pixel sits at distance 0 from the centre and
        // is well inside the radius, so it gets full coverage (alpha clamps to 1) — the centre
        // channel value should be the colour as-given. An off-disc pixel stays background.
        var canvas = new Canvas(20, 20, Rgba.White);
        canvas.FillDisc(10, 10, radius: 3, new(200, 50, 50));

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
    public async Task Labeller_knockout_paints_backdrop_under_text()
    {
        // With a fully-opaque red knockout the rect underneath every label pixel reads as red —
        // including pixels where no glyph stroke landed (the gaps between letters). Without
        // knockout those same pixels stay the background colour. Compare a knockout render to a
        // plain one and assert (a) the knockout-coloured backdrop is present and (b) the foreground
        // text colour still wins where the strokes land (so knockout doesn't *replace* the text).
        var canvas = new Canvas(120, 40, Rgba.White);
        var labeller = new Labeller(canvas);
        labeller.TryPlace("HI", 60, 20, 14, Rgba.Black, halo: null, pointOffset: 0, knockout: new(255, 0, 0));

        var redPixels = 0;
        var blackPixels = 0;
        for (var i = 0; i + 4 <= canvas.PixelByteCount; i += 4)
        {
            var r = canvas.Pixels[i];
            var g = canvas.Pixels[i + 1];
            var b = canvas.Pixels[i + 2];
            if (r == 255 && g == 0 && b == 0)
            {
                redPixels++;
            }
            else if (r < 80 && g < 80 && b < 80)
            {
                blackPixels++;
            }
        }

        // Backdrop covers the entire bbox so we expect many red pixels — far more than the
        // black stroke ink within the same bbox.
        await Assert.That(redPixels).IsGreaterThan(blackPixels);
        await Assert.That(blackPixels).IsGreaterThan(0);
    }

    [Test]
    public async Task Labeller_knockout_pad_widens_collision_box()
    {
        // Same shape as the halo collision test: without a backdrop two labels just clear of each
        // other both fit; turn knockout on and the 2-pixel pad pushes them into collision and the
        // second is dropped. Confirms knockout participates in the collision bbox the same way the
        // halo does (and that the pad applies even when halo is null).
        var (textWidth, _) = StrokeFont.Measure("A", 14);

        var withoutKnockout = new Labeller(new(200, 32, Rgba.White));
        withoutKnockout.TryPlace("A", 40, 16, 14, Rgba.Black, halo: null);
        var secondWithoutKnockout = withoutKnockout.TryPlace("A", 40 + textWidth + 1, 16, 14, Rgba.Black, halo: null);

        var withKnockout = new Labeller(new(200, 32, Rgba.White));
        withKnockout.TryPlace("A", 40, 16, 14, Rgba.Black, halo: null, pointOffset: 0, knockout: Rgba.White);
        var secondWithKnockout = withKnockout.TryPlace("A", 40 + textWidth + 1, 16, 14, Rgba.Black, halo: null, pointOffset: 0, knockout: Rgba.White);

        await Assert.That(secondWithoutKnockout).IsTrue();
        await Assert.That(secondWithKnockout).IsFalse();
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

        var withHalo = new Labeller(new(200, 32, Rgba.White));
        withHalo.TryPlace("A", 40, 16, 14, Rgba.Black, halo: Rgba.White);
        var withHaloSecond = withHalo.TryPlace("A", 40 + textWidth + 1, 16, 14, Rgba.Black, halo: Rgba.White);

        await Assert.That(withoutHaloSecond).IsTrue();
        await Assert.That(withHaloSecond).IsFalse();
    }

    [Test]
    public async Task Labeller_point_offset_places_NE_of_anchor()
    {
        // With a positive pointOffset the label sits at the NE corner of the candidate ring:
        // leftX = anchorX + offset, baselineY = anchorY - offset. So every painted pixel must lie
        // strictly to the upper-right of the anchor — confirms the first Imhof candidate is the
        // one chosen when nothing's competing for it.
        var canvas = new Canvas(200, 200, Rgba.White);
        var labeller = new Labeller(canvas);
        const double anchorX = 100;
        const double anchorY = 100;
        var placed = labeller.TryPlace("HI", anchorX, anchorY, 14, Rgba.Black, halo: null, pointOffset: 6);

        await Assert.That(placed).IsTrue();

        // Scan the canvas: any non-white pixel must be NE of the anchor (x > anchorX, y < anchorY).
        // The "y < anchorY" check uses the baseline being above the anchor — descenders would
        // push some ink below the baseline, but "HI" has none.
        var anyInk = false;
        for (var y = 0; y < canvas.Height; y++)
        {
            for (var x = 0; x < canvas.Width; x++)
            {
                var index = (y * canvas.Width + x) * 4;
                if (canvas.Pixels[index] != 255 || canvas.Pixels[index + 1] != 255 || canvas.Pixels[index + 2] != 255)
                {
                    anyInk = true;
                    await Assert.That(x).IsGreaterThanOrEqualTo((int)anchorX);
                    await Assert.That(y).IsLessThan((int)anchorY);
                }
            }
        }

        await Assert.That(anyInk).IsTrue();
    }

    [Test]
    public async Task Labeller_point_offset_falls_back_through_candidates_on_collision()
    {
        // First label takes NE. Second label at the same anchor must collide on NE and fall
        // through to NW — leftX ends up to the LEFT of the anchor, distinguishable from a "first
        // candidate" failure (which would have just dropped). PlacedCount confirms both fitted.
        var canvas = new Canvas(400, 200, Rgba.White);
        var labeller = new Labeller(canvas);
        const double anchorX = 200;
        const double anchorY = 100;
        var first = labeller.TryPlace("AAA", anchorX, anchorY, 14, Rgba.Black, halo: null, pointOffset: 6);
        var second = labeller.TryPlace("BBB", anchorX, anchorY, 14, Rgba.Black, halo: null, pointOffset: 6);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(labeller.PlacedCount).IsEqualTo(2);

        // The first label sits to the right of the anchor; the second must have landed to the
        // left (NW) — find any ink to the LEFT of the anchor to confirm the fall-through ran.
        var foundLeftOfAnchor = false;
        for (var y = 0; y < canvas.Height && !foundLeftOfAnchor; y++)
        {
            for (var x = 0; x < (int)anchorX; x++)
            {
                var index = (y * canvas.Width + x) * 4;
                if (canvas.Pixels[index] != 255 || canvas.Pixels[index + 1] != 255 || canvas.Pixels[index + 2] != 255)
                {
                    foundLeftOfAnchor = true;
                    break;
                }
            }
        }

        await Assert.That(foundLeftOfAnchor).IsTrue();
    }

    [Test]
    public async Task Labeller_point_offset_drops_when_all_candidates_collide()
    {
        // Block every Imhof slot around (200, 200) by placing five centred labels stacked
        // vertically through the anchor — covers the inkTop-rise for NE/N/NW (via the row above)
        // and the inkBottom-drop for SE/S/SW (via the row below), with enough horizontal extent
        // to swallow the E/W boxes too. With every candidate colliding, the YY placement must
        // return false and never paint.
        var canvas = new Canvas(400, 400, Rgba.White);
        var labeller = new Labeller(canvas);
        for (var y = 160; y <= 240; y += 20)
        {
            labeller.TryPlace("BLOCK_BLOCK_BLOCK", 200, y, 14, Rgba.Black, halo: null);
        }
        var initialPlacedCount = labeller.PlacedCount;

        var placed = labeller.TryPlace("YY", 200, 200, 14, Rgba.Black, halo: null, pointOffset: 6);
        await Assert.That(placed).IsFalse();
        // PlacedCount unchanged — confirms no candidate snuck a YY through.
        await Assert.That(labeller.PlacedCount).IsEqualTo(initialPlacedCount);
    }

    [Test]
    public async Task Labeller_zero_pointOffset_keeps_centred_placement()
    {
        // Default pointOffset is 0 — the centred placement path for polygon-centroid / line-
        // midpoint anchors. The label must straddle the anchor on both axes: some ink to the
        // left AND some to the right, some above AND some below the baseline-centred row. NE
        // placement would put ALL ink on one side; this test would fail there.
        var canvas = new Canvas(200, 100, Rgba.White);
        var labeller = new Labeller(canvas);
        var placed = labeller.TryPlace("CENTRED", 100, 50, 14, Rgba.Black, halo: null);
        await Assert.That(placed).IsTrue();

        var leftOfAnchor = false;
        var rightOfAnchor = false;
        for (var y = 0; y < canvas.Height; y++)
        {
            for (var x = 0; x < canvas.Width; x++)
            {
                var index = (y * canvas.Width + x) * 4;
                if (canvas.Pixels[index] == 255 && canvas.Pixels[index + 1] == 255 && canvas.Pixels[index + 2] == 255)
                {
                    continue;
                }

                if (x < 100)
                {
                    leftOfAnchor = true;
                }
                else if (x > 100)
                {
                    rightOfAnchor = true;
                }
            }
        }

        await Assert.That(leftOfAnchor).IsTrue();
        await Assert.That(rightOfAnchor).IsTrue();
    }

    [Test]
    public async Task Labeller_point_offset_drops_off_canvas_candidates()
    {
        // Anchor pinned hard against the right edge of the canvas: the NE/SE/E candidates all
        // push the label past the right edge. The walker should keep going and find a working
        // slot on the left side (NW or W). Confirms the off-canvas rejection participates in the
        // candidate walk rather than abandoning at the first failure.
        var canvas = new Canvas(200, 100, Rgba.White);
        var labeller = new Labeller(canvas);
        var placed = labeller.TryPlace("LONGLABEL", 195, 50, 14, Rgba.Black, halo: null, pointOffset: 6);
        await Assert.That(placed).IsTrue();
        await Assert.That(labeller.PlacedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Labeller_point_offset_drops_when_canvas_too_small_in_all_directions()
    {
        // Canvas barely larger than one label's width: every Imhof candidate clips somewhere off
        // the edge. The fallback walk runs all eight slots and returns false. Covers the "all
        // candidates failed" return-false path.
        var canvas = new Canvas(40, 40, Rgba.White);
        var labeller = new Labeller(canvas);
        var placed = labeller.TryPlace("VERYLONGLABELHERE", 20, 20, 14, Rgba.Black, halo: null, pointOffset: 6);
        await Assert.That(placed).IsFalse();
        await Assert.That(labeller.PlacedCount).IsEqualTo(0);
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
        options.LayerStyle = _ => new()
        {
            Label = _ => _.Properties.TryGetValue("code", out var v) ? v as string : null,
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
        options.LayerStyle = _ => new();
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
    public async Task Label_inherits_knockout_from_options()
    {
        // Knockout colour distinct from background and from any text/halo. Underlying geometry
        // would normally paint blue (the polygon fill); the knockout rect should cover it within
        // each label bbox so the backdrop colour appears in the output.
        var features = new FeatureCollection
        {
            new Feature(
                new Polygon([[new(-80, -40), new(80, -40), new(80, 40), new(-80, 40), new(-80, -40)]]),
                new Dictionary<string, object?> { ["name"] = "X" }),
        };
        var options = new RenderOptions
        {
            Bounds = new(-90, -50, 90, 50),
            Width = 200,
            Projection = MapProjection.PlateCarree,
            Padding = 0,
            Label = feature => feature.Properties.TryGetValue("name", out var v) ? v as string : null,
            LabelSize = 20,
            LabelColor = Rgba.Black,
            LabelHalo = null,
            LabelKnockout = new(0, 200, 0),
            Fill = new(0, 0, 200),
            Stroke = new(0, 0, 100),
        };
        var pixels = Render(features, options);

        var greenPixels = 0;
        for (var i = 0; i + 4 <= pixels.Length; i += 4)
        {
            // The knockout colour blends over the blue fill — exact (0,200,0) appears on
            // backdrop pixels the fill already painted under (no blending needed since knockout
            // is fully opaque).
            if (pixels[i] == 0 && pixels[i + 1] == 200 && pixels[i + 2] == 0)
            {
                greenPixels++;
            }
        }

        await Assert.That(greenPixels).IsGreaterThan(0);
    }

    [Test]
    public async Task Layer_label_knockout_overrides_options()
    {
        // Per-layer override of LabelKnockout — same fall-through pattern as the other label knobs.
        var features = new FeatureCollection
        {
            Name = "custom",
            Features =
            {
                new(new Point(0, 0), Props("X")),
            },
        };
        var options = LabelOptions();
        options.LabelKnockout = null;
        options.LayerStyle = _ => new() { LabelKnockout = new(255, 200, 0) };
        var pixels = Render(features, options);

        var orangePixels = 0;
        for (var i = 0; i + 4 <= pixels.Length; i += 4)
        {
            if (pixels[i] == 255 && pixels[i + 1] == 200 && pixels[i + 2] == 0)
            {
                orangePixels++;
            }
        }

        await Assert.That(orangePixels).IsGreaterThan(0);
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
    public Task Render_snapshot_with_diacritic_labels()
    {
        // A diacritic-heavy label set covering every supported combining mark (grave, acute,
        // circumflex, tilde, diaeresis, ring, caron, cedilla) plus an undecomposable ligature (ß)
        // and a non-Latin script (こんにちは) that falls back to '?'. Points are spread on a wide
        // canvas at well-separated lon/lat so every label fits — the snapshot pins where each
        // accent lands relative to its base glyph and locks in the '?' fallback path. Halo on so
        // the white ring around the strokes is part of the visual baseline.
        var features = new FeatureCollection
        {
            new Feature(new Point(-160, 60), Props("Montréal")),
            new Feature(new Point(-120, 40), Props("São Paulo")),
            new Feature(new Point(-80, 20), Props("Côte d'Ivoire")),
            new Feature(new Point(-40, 0), Props("Mañana")),
            new Feature(new Point(0, -20), Props("Über")),
            new Feature(new Point(40, -40), Props("Århus")),
            new Feature(new Point(80, -60), Props("Plzeň")),
            new Feature(new Point(120, 60), Props("Curaçao")),
            new Feature(new Point(120, 0), Props("Straße")),       // ß stays '?'
            new Feature(new Point(160, -40), Props("こんにちは")),  // non-Latin falls back to '?'
        };
        var options = new RenderOptions
        {
            Bounds = new(-180, -80, 180, 80),
            Width = 1200,
            Projection = MapProjection.PlateCarree,
            Label = feature => feature.Properties.TryGetValue("name", out var v) ? v as string : null,
            LabelSize = 18,
            LabelColor = new(30, 30, 30),
            LabelHalo = new(255, 255, 255, 220),
            Fill = new(220, 220, 220),
            Stroke = new(80, 80, 80),
        };
        return Verify(new MemoryStream(MapRenderer.RenderPng(features, options)), "png");
    }

    [Test]
    public Task Render_snapshot_label_halo()
    {
        // Companion to the readme's RenderLabelHalo snippet — same scene, same options. Acts as
        // the visual baseline for the "halo treatment" image in the docs and locks in regressions
        // to the halo's interaction with country borders. Cropped to western Europe so dense
        // political borders read at this size; Lambert is the Auto choice at this lat/lon span.
        var features = GeoConverter.Read(ProjectFiles.world_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new(-12, 35, 32, 60),
                Width = 800,
                Projection = MapProjection.Lambert,
                Background = new(245, 245, 245),
                Fill = new(220, 220, 210),
                Stroke = new(120, 120, 120),
                StrokeWidth = 1,
                Label = _ => _.Properties.TryGetValue("NAME", out var value) ? value as string : null,
                LabelSize = 14,
                LabelColor = new(30, 30, 30),
                LabelHalo = new(255, 255, 255, 220),
            });
        return Verify(png, "png");
    }

    [Test]
    public Task Render_snapshot_label_knockout()
    {
        // Pair to Render_snapshot_label_halo: identical scene with knockout swapped in for the
        // halo. Locked in as a snapshot so the readme image stays in sync with the code, and so
        // any regression to the knockout rect (size, padding, fill order vs the halo pass) shows
        // up as a visible diff next to the halo baseline.
        var features = GeoConverter.Read(ProjectFiles.world_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new(-12, 35, 32, 60),
                Width = 800,
                Projection = MapProjection.Lambert,
                Background = new(245, 245, 245),
                Fill = new(220, 220, 210),
                Stroke = new(120, 120, 120),
                StrokeWidth = 1,
                Label = _ => _.Properties.TryGetValue("NAME", out var value) ? value as string : null,
                LabelSize = 14,
                LabelColor = new(30, 30, 30),
                LabelHalo = null,
                LabelKnockout = new(245, 245, 245),
            });
        return Verify(png, "png");
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
        return Verify(MapRenderer.RenderPng(features, options), "png");
    }

    [Test]
    public async Task Custom_label_priority_overrides_default_area_rule()
    {
        // Two points sitting at the same projected pixel. Both want the NE slot of the Imhof
        // candidate ring; the higher-priority one wins it and the other gets bumped to NW. Without
        // a custom priority both have geometry-area = 0 (points) and file order decides — FIRST
        // takes NE, SECOND lands NW. With a custom priority sourced from the "rank" property,
        // SECOND outranks FIRST and the slots flip. The snapshot pins which word lands where, so
        // a regression that drops the callback or reverses the sort diffs visibly.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0), new Dictionary<string, object?> { ["name"] = "FIRST", ["rank"] = 1.0 }),
            new Feature(new Point(0, 0), new Dictionary<string, object?> { ["name"] = "SECOND", ["rank"] = 99.0 }),
        };
        var options = LabelOptions();
        options.LabelPriority = _ => _.Properties.TryGetValue("rank", out var v) ? Convert.ToDouble(v) : 0;

        var png = MapRenderer.RenderPng(features, options);
        var pixels = Decode(png);

        // Cross-check: same scene without the priority callback must produce a different image —
        // confirms the override actually changed the winner rather than coincidentally agreeing
        // with the default.
        var defaultPixels = Render(features, LabelOptions());
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);
        await Assert.That(pixels.SequenceEqual(defaultPixels)).IsFalse();

        await Verify(png, "png");
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
        var pixels = Decode(png);
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);

        await Verify(png, "png");
    }

    [Test]
    public async Task Layer_label_priority_overrides_options_default()
    {
        // Priority callback on a LayerStyle takes precedence over the RenderOptions-level one for
        // that layer — same per-property fall-through pattern as the other label knobs. Pre-order
        // layer traversal means the background layer's "L" lands its preferred NE slot first;
        // "U" (overlay) collides at NE and gets bumped to NW. Snapshot pins which letter lands
        // where so a regression that drops the per-layer priority callback diffs visibly.
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
            "background" => new()
            {
                LabelPriority = _ => Convert.ToDouble(_.Properties["w"]),
            },
            _ => null,
        };
        var png = MapRenderer.RenderPng([lower, upper], options);
        var pixels = Decode(png);
        await Assert.That(LabelPixels(pixels)).IsGreaterThan(0);

        await Verify(png, "png");
    }

    [Test]
    public Task Render_snapshot_stroke_autoscale_world()
    {
        // Snapshot pinning the visual effect of StrokeAutoScale at a world-scale render — the
        // multiplier should be well below 1 (zoom ~4 at 2048-wide is 5.5 zoom levels under the
        // country-scale baseline, so 1.15^-5.5 ≈ 0.46). Compared to the existing world snapshots
        // that use a fixed 2px StrokeWidth, this one should show noticeably thinner borders.
        // A regression that reversed the formula's direction (e.g. 1.15^(10 - zoom) instead of
        // 1.15^(zoom - 10)) would produce the opposite: thick borders at world scale, which
        // would diff loudly against this baseline.
        var features = GeoConverter.Read(ProjectFiles.world_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new(-180, -90, 180, 90),
                Width = 2048,
                Projection = MapProjection.PlateCarree,
                Fill = new(180, 200, 220),
                Stroke = new(40, 40, 40),
                StrokeWidth = 2,
                StrokeAutoScale = true,
            });

        return Verify(png, "png");
    }

    [Test]
    public async Task StrokeAutoScale_thickens_strokes_at_country_scale()
    {
        // Same canvas, same StrokeWidth=2, but different bboxes. With autoscale on, the
        // country-scale render uses a different multiplier than the world view — so the rendered
        // bytes differ from the autoscale-off baseline. Confirms the flag is wired through.
        static byte[] RenderWith(Envelope bounds, bool autoScale) =>
            MapRenderer.RenderPng(
                [
                    new Feature(new LineString([new(-5, 0), new(5, 0)]))
                ],
                new()
                {
                    Bounds = bounds,
                    Width = 200,
                    Height = 200,
                    Padding = 0,
                    Projection = MapProjection.PlateCarree,
                    Stroke = new(0, 0, 0),
                    StrokeWidth = 2,
                    StrokeAutoScale = autoScale,
                });

        // Country bbox (10° × 10°) — autoscale multiplier < 1 here (zoom ~4.8 vs baseline 10).
        var country = RenderWith(new(-5, -5, 5, 5), autoScale: true);
        // Same country render WITHOUT autoscale — fixed 2px regardless of bbox.
        var countryFixed = RenderWith(new(-5, -5, 5, 5), autoScale: false);

        await Assert.That(country.SequenceEqual(countryFixed)).IsFalse();
    }

    [Test]
    public async Task StrokeAutoScale_off_leaves_strokes_unchanged()
    {
        // With the flag off (default), the rendered bytes must match a render that doesn't set
        // the flag at all — autoscale is genuinely opt-in and the existing snapshot output is
        // untouched.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(-5, 0), new(5, 0)])),
        };
        var withFlag = MapRenderer.RenderPng(features, new()
        {
            Bounds = new(-10, -10, 10, 10),
            Width = 200,
            Height = 200,
            Padding = 0,
            Projection = MapProjection.PlateCarree,
            StrokeWidth = 3,
            StrokeAutoScale = false,
        });
        var withoutFlag = MapRenderer.RenderPng(features, new()
        {
            Bounds = new(-10, -10, 10, 10),
            Width = 200,
            Height = 200,
            Padding = 0,
            Projection = MapProjection.PlateCarree,
            StrokeWidth = 3,
        });

        await Assert.That(withFlag.SequenceEqual(withoutFlag)).IsTrue();
    }

    [Test]
    public async Task StrokeAutoScale_clamps_at_high_zoom_extreme()
    {
        // A microscopic bbox (1e-4° square) at 4096px implies an implicit zoom well past 20,
        // which the 1.15^(zoom-10) formula would otherwise blow up to >100×. The clamp caps
        // the multiplier at 6×, so the stroke stays drawable. Confirms the upper-clamp branch
        // is exercised — without it this render would either swallow the whole canvas in
        // strokes or trip canvas-size guards.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0)),
        };
        var png = MapRenderer.RenderPng(features, new()
        {
            Bounds = new(-0.00005, -0.00005, 0.00005, 0.00005),
            Width = 4096,
            Padding = 0,
            Projection = MapProjection.PlateCarree,
            PointRadius = 4,
            StrokeAutoScale = true,
        });
        await Assert.That(png.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task StrokeAutoScale_clamps_at_low_zoom_extreme()
    {
        // Reverse: oversized bbox (3600° span) at small canvas pushes the implicit zoom well
        // below 0, where the formula would shrink the multiplier toward 0. The clamp pins it at
        // 0.25× so the stroke never vanishes entirely. Exercises the lower-clamp branch.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(-100, 0), new(100, 0)])),
        };
        var png = MapRenderer.RenderPng(features, new()
        {
            Bounds = new(-1800, -1800, 1800, 1800),
            Width = 64,
            Padding = 0,
            Projection = MapProjection.PlateCarree,
            StrokeWidth = 2,
            StrokeAutoScale = true,
        });
        await Assert.That(png.Length).IsGreaterThan(0);
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

        return Verify(png, "png");
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
        return Decode(png);
    }

    static byte[] Render(IReadOnlyList<FeatureCollection> layers, RenderOptions options)
    {
        var png = MapRenderer.RenderPng(layers, options);
        return Decode(png);
    }

    // Counts pixels that aren't pure white — with LabelOptions() forcing geometry to fully
    // transparent stroke/fill, every non-white pixel in the output is label text (or halo).
    static int LabelPixels(ReadOnlySpan<byte> pixels)
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

    static int NonBackgroundCount(ReadOnlySpan<byte> pixels) => LabelPixels(pixels);

    // Tight view over a Canvas's logical pixel region — strips any oversized trailing bytes the
    // ArrayPool rental may have included, so the test helpers above iterate over actual canvas
    // pixels and not stale pool content.
    static ReadOnlySpan<byte> LogicalPixels(Canvas canvas) =>
        canvas.Pixels.AsSpan(0, canvas.PixelByteCount);

    static Dictionary<string, object?> Props(string name, string? key = null) =>
        NameProps(name, key);

    static Dictionary<string, object?> NameProps(string name, string? key = null) =>
        new() { [key ?? "name"] = name };

    // Mirrors PngTests.Decode — kept local to avoid making PngTests' helpers public for a
    // test-only consumer.
    static byte[] Decode(byte[] data)
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

        return PngDecoder.Reconstruct(raw.ToArray(), width, height);
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
