public class PngTests
{
    static readonly byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Test]
    public async Task Renders_the_requested_size()
    {
        var png = MapRenderer.RenderPng(Sample.Polygons(), new()
        {
            Width = 200,
            Height = 150
        });

        await Assert.That(png[..8]).IsEquivalentTo(signature);
        var (width, height, _) = Decode(png);
        await Assert.That(width).IsEqualTo(200);
        await Assert.That(height).IsEqualTo(150);
    }

    [Test]
    public async Task Draws_geometry_over_the_background()
    {
        var png = MapRenderer.RenderPng(Sample.Polygons(), new()
        {
            Width = 200,
            Height = 150
        });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Clips_to_the_bounding_box()
    {
        // A bounding box far away from the data should leave the canvas empty.
        var options = new RenderOptions
        {
            Bounds = new Envelope(1000, 1000, 1001, 1001),
            Width = 64,
            Height = 64,
        };

        var (_, _, pixels) = Decode(MapRenderer.RenderPng(Sample.Polygons(), options));
        await Assert.That(NonBackgroundCount(pixels)).IsEqualTo(0);
    }

    [Test]
    public async Task Derives_height_from_aspect_ratio_when_unset()
    {
        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 100, 50),
            Width = 200,
        };

        var (width, height, _) = Decode(MapRenderer.RenderPng(Sample.Polygons(), options));
        await Assert.That(width).IsEqualTo(200);
        await Assert.That(height).IsEqualTo(100);
    }

    [Test]
    public async Task Empty_collection_throws()
    {
        var threw = false;
        try
        {
            MapRenderer.RenderPng(new FeatureCollection());
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Renders_all_geometry_types()
    {
        var features = new FeatureCollection
        {
            new Feature(new Point(1, 1)),
            new Feature(new MultiPoint([new(2, 2), new(3, 3)])),
            new Feature(new LineString([new(0, 0), new(4, 4)])),
            new Feature(new MultiLineString([new([new(0, 4), new(4, 0)])])),
            new Feature(new Polygon([[new(0, 0), new(4, 0), new(4, 4), new(0, 0)]])),
            new Feature(new MultiPolygon([new([[new(1, 1), new(2, 1), new(2, 2), new(1, 1)]])])),
            new Feature(new GeometryCollection([new Point(2, 3)])),
        };

        var png = MapRenderer.RenderPng(
            features,
            new()
        {
            Width = 128,
            Height = 128
        });
        await Assert.That(png[..8]).IsEquivalentTo(signature);
    }

    [Test]
    public async Task Fills_polygon_with_opaque_color()
    {
        // An opaque fill takes the fast span-fill path; the interior should be exactly the fill color.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
        };
        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 64,
            Height = 64,
            Fill = new(200, 50, 50),
        };

        var (width, _, pixels) = Decode(MapRenderer.RenderPng(features, options));
        var center = (32 * width + 32) * 4;
        await Assert.That(pixels[center]).IsEqualTo((byte)200);
        await Assert.That(pixels[center + 1]).IsEqualTo((byte)50);
        await Assert.That(pixels[center + 2]).IsEqualTo((byte)50);
    }

    [Test]
    public async Task Strokes_with_translucent_color_blends_against_background()
    {
        // Blend's translucent branch is reached when a stroke (per-pixel disc fill) uses a non-opaque
        // colour. Without a test that exercises this, the FillPolygon path keeps it dead.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(0, 0), new(10, 10)])),
        };
        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 32,
            Height = 32,
            Background = new(255, 255, 255),
            Stroke = new(255, 0, 0, 128),
            StrokeWidth = 4,
        };

        var (_, _, pixels) = Decode(MapRenderer.RenderPng(features, options));
        // A blended pixel is neither pure-white background nor pure-red stroke: green/blue should
        // have lifted toward white as alpha=128 was composited over the background.
        var blended = 0;
        for (var p = 0; p + 4 <= pixels.Length; p += 4)
        {
            var r = pixels[p];
            var g = pixels[p + 1];
            var b = pixels[p + 2];
            // Background is (255,255,255); fully-opaque stroke would write (255,0,0). A blend lands
            // between, with green/blue strictly above 0 but below 255.
            if (r > 200 && g is > 0 and < 255 && b is > 0 and < 255)
            {
                blended++;
            }
        }

        await Assert.That(blended).IsGreaterThan(0);
    }

    [Test]
    public Task Render_snapshot()
    {
        var features = new FeatureCollection
        {
            new Feature(new Polygon(
            [
                [new(0, 0), new(10, 0), new(10, 8), new(0, 8), new(0, 0)],
                [new(2, 2), new(5, 2), new(5, 5), new(2, 5), new(2, 2)],
            ])),
            new Feature(new LineString([new(1, 1), new(9, 7), new(1, 7), new(9, 1)])),
            new Feature(new MultiPoint([new(3, 6), new(7, 3), new(5, 5)])),
        };

        // Pinned to PlateCarree — same rationale as Render_RealMap: Auto would pick Lambert at this
        // bounds size, but this snapshot is the regression for the linear-lon/lat layout.
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-1, -1, 11, 9),
                Width = 300,
                Height = 220,
                Projection = MapProjection.PlateCarree,
            });

        return Verify(png, "png");
    }

    [Test]
    public Task Render_RealMap()
    {
        // Pinned to PlateCarree so this stays the explicit regression for the linear-lon/lat layout
        // even though Auto would pick Lambert for Australia (lonSpan ≈ 41°, latSpan ≈ 34° — both well
        // under the AutoLatitudeSpanLimit / AutoLongitudeSpanLimit cutoffs). The Lambert render of the
        // same dataset is covered by Render_snapshot_lambert.
        var features = GeoConverter.Read(ProjectFiles.australian_suburbs_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Width = 3000,
                Projection = MapProjection.PlateCarree,
            });

        return Verify(png, "png");
    }

    [Test]
    public async Task WebMercator_stretches_high_latitudes_relative_to_plate_carree()
    {
        // Same bounds, derived height: in Web Mercator a 0–80° lat strip projects to roughly 14× the
        // longitudinal width, vs 8× under plate carrée. So Mercator must produce a taller image.
        var features = new FeatureCollection
        {
            new Feature(new Point(5, 40))
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(0, 0, 10, 80),
            Width = 100,
            Padding = 0,
            Projection = projection,
        };

        var (_, plateHeight, _) = Decode(MapRenderer.RenderPng(features, Build(MapProjection.PlateCarree)));
        var (_, mercatorHeight, _) = Decode(MapRenderer.RenderPng(features, Build(MapProjection.WebMercator)));

        await Assert.That(plateHeight).IsEqualTo(800);
        await Assert.That(mercatorHeight).IsGreaterThan(plateHeight);
    }

    [Test]
    public async Task WebMercator_clamps_polar_latitudes()
    {
        // ln(tan) at lat 90° is +∞. Anything past the ±85.0511° cutoff should clamp to the limit, so two
        // points well past the cutoff (one slightly, one extreme) must render identically. Without the
        // clamp this throws or NaNs through the rasterizer.
        var near = new FeatureCollection
        {
            new Feature(new Point(0, 86))
        };
        var far = new FeatureCollection
        {
            new Feature(new Point(0, 89.9))
        };

        static RenderOptions Build() => new()
        {
            // Bounds stop at exactly the cutoff so neither bound is itself clamped — only the data is.
            Bounds = new Envelope(-10, -85.05112877980659, 10, 85.05112877980659),
            Width = 64,
            Height = 64,
            Projection = MapProjection.WebMercator,
        };

        var (_, _, nearPixels) = Decode(MapRenderer.RenderPng(near, Build()));
        var (_, _, farPixels) = Decode(MapRenderer.RenderPng(far, Build()));

        await Assert.That(NonBackgroundCount(nearPixels)).IsGreaterThan(0);
        await Assert.That(farPixels).IsEquivalentTo(nearPixels);
    }

    [Test]
    public async Task Lambert_differs_from_plate_carree_at_country_scale()
    {
        // LCC's whole point is that meridians fan in toward the cone's apex, so a feature on one side
        // of the central meridian doesn't project to the same X as it would under linear lon/lat.
        // Render a single off-centre point in each projection and confirm the pixel column moves —
        // without this, a future refactor could silently fall back to PlateCarree and pass everything.
        static FeatureCollection Build() => [new Feature(new Point(-120, 30))];

        static RenderOptions Options(MapProjection projection) => new()
        {
            Bounds = new Envelope(-125, 25, -65, 50),
            Width = 600,
            Height = 400,
            Padding = 0,
            Projection = projection,
            PointRadius = 1,
        };

        var (_, _, platePixels) = Decode(MapRenderer.RenderPng(Build(), Options(MapProjection.PlateCarree)));
        var (_, _, lambertPixels) = Decode(MapRenderer.RenderPng(Build(), Options(MapProjection.Lambert)));

        // The point renders as a small disc; locate its centroid column in each image.
        await Assert.That(NonBackgroundCentroidX(platePixels, 600))
            .IsNotEqualTo(NonBackgroundCentroidX(lambertPixels, 600));
    }

    [Test]
    public async Task Auto_picks_lambert_for_regional_bounds()
    {
        // A US-shaped bbox (lonSpan 60°, latSpan 25°) sits well under the Auto cutoffs (90°/60°), so
        // Auto must route to Lambert. Comparing pixel-equality against an explicit Lambert render is
        // the tightest check — if Auto starts picking anything else, this fails immediately.
        var features = new FeatureCollection
        {
            new Feature(new Point(-100, 40)),
            new Feature(new Point(-80, 30)),
            new Feature(new Point(-120, 45)),
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(-125, 25, -65, 50),
            Width = 200,
            Height = 150,
            Padding = 0,
            Projection = projection,
        };

        var auto = MapRenderer.RenderPng(features, Build(MapProjection.Auto));
        var lambert = MapRenderer.RenderPng(features, Build(MapProjection.Lambert));
        await Assert.That(auto).IsEquivalentTo(lambert);
    }

    [Test]
    public async Task Auto_picks_plate_carree_when_latitude_span_is_too_large()
    {
        // Africa-shaped bbox: latSpan 73° crosses the AutoLatitudeSpanLimit (60°) so the cone would
        // distort too much; Auto must drop back to PlateCarree.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0)),
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(-20, -35, 55, 38),
            Width = 200,
            Height = 200,
            Padding = 0,
            Projection = projection,
        };

        var auto = MapRenderer.RenderPng(features, Build(MapProjection.Auto));
        var plate = MapRenderer.RenderPng(features, Build(MapProjection.PlateCarree));
        await Assert.That(auto).IsEquivalentTo(plate);
    }

    [Test]
    public async Task Auto_picks_plate_carree_when_longitude_span_is_continental()
    {
        // A continental longitude span (≥ AutoLongitudeSpanLimit = 90° but < the world cutoff of
        // 180°) routes to PlateCarree regardless of latitude — Lambert can't sensibly draw a
        // hemisphere, and the bounds aren't wide enough to justify the equal-area Homolosine.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0)),
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(-60, -10, 60, 10),
            Width = 200,
            Height = 200,
            Padding = 0,
            Projection = projection,
        };

        var auto = MapRenderer.RenderPng(features, Build(MapProjection.Auto));
        var plate = MapRenderer.RenderPng(features, Build(MapProjection.PlateCarree));
        await Assert.That(auto).IsEquivalentTo(plate);
    }

    [Test]
    public async Task Auto_picks_goode_for_world_longitude_span()
    {
        // A full-world longitude span (≥ AutoWorldLongitudeSpan = 180°) is what "world map" means
        // for the renderer's purposes, so Auto picks Goode's Homolosine — equal-area, so areas at
        // high latitudes don't look wildly inflated the way they would under PlateCarree.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0)),
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(-180, -60, 180, 60),
            Width = 200,
            Height = 100,
            Padding = 0,
            Projection = projection,
        };

        var auto = MapRenderer.RenderPng(features, Build(MapProjection.Auto));
        var goode = MapRenderer.RenderPng(features, Build(MapProjection.Goode));
        await Assert.That(auto).IsEquivalentTo(goode);
    }

    [Test]
    public async Task Auto_picks_goode_for_world_latitude_span()
    {
        // A pole-to-pole latitude span (≥ AutoWorldLatitudeSpan = 90°) also reads as a world map —
        // an Antarctica-to-Greenland strip is the canonical case — so Auto routes there to Goode
        // too, not just on the longitude axis.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0)),
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(-30, -85, 30, 85),
            Width = 100,
            Height = 200,
            Padding = 0,
            Projection = projection,
        };

        var auto = MapRenderer.RenderPng(features, Build(MapProjection.Auto));
        var goode = MapRenderer.RenderPng(features, Build(MapProjection.Goode));
        await Assert.That(auto).IsEquivalentTo(goode);
    }

    [Test]
    public async Task Lambert_handles_zero_height_latitude_span()
    {
        // φ₁ and φ₂ collapse to the same parallel when the data has no latitudinal extent (a single
        // east-west line of points). The LCC formulas hit a 0/0 in that case, so the parameter
        // calculation falls back to n = sin(φ₁) for a one-parallel cone — without this branch the
        // projection would emit NaN pixels.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(-100, 40), new(-80, 40), new(-60, 40)])),
        };

        var png = MapRenderer.RenderPng(features, new()
        {
            Width = 200,
            Height = 100,
            Projection = MapProjection.Lambert,
        });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Lambert_falls_back_to_plate_carree_when_cone_degenerates()
    {
        // Equator-symmetric bounds collapse the LCC cone into a cylinder (n → 0, ρ → ∞). Rather than
        // throwing or rendering NaN-filled pixels, the projection silently falls back to PlateCarree;
        // assert that by rendering identical bounds in both projections and comparing the output.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(-10, -10), new(10, -10), new(10, 10), new(-10, 10), new(-10, -10)]])),
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(-20, -20, 20, 20),
            Width = 200,
            Height = 200,
            Padding = 0,
            Projection = projection,
        };

        var lambert = MapRenderer.RenderPng(features, Build(MapProjection.Lambert));
        var plate = MapRenderer.RenderPng(features, Build(MapProjection.PlateCarree));
        await Assert.That(lambert).IsEquivalentTo(plate);
    }

    [Test]
    public Task Render_snapshot_lambert()
    {
        var features = GeoConverter.Read(ProjectFiles.australian_suburbs_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Width = 3000,
                Projection = MapProjection.Lambert,
            });

        return Verify(png, "png");
    }

    [Test]
    public async Task Goode_sinusoidal_band_keeps_y_linear_in_latitude()
    {
        // Inside the ±40°44'11.8" band Goode is the sinusoidal projection: y = φ exactly. So three
        // points at equally-spaced latitudes inside the band must render at equally-spaced pixel
        // rows. If a refactor accidentally routed the band through the Mollweide branch (where
        // y = √2 sin(θ(φ)) bends sub-linearly in φ) the centre row would drift off the midpoint by
        // several pixels at this image size and the assertion would fire.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, -30)),
            new Feature(new Point(0, 0)),
            new Feature(new Point(0, 30)),
        };

        var options = new RenderOptions
        {
            Bounds = new Envelope(-5, -40, 5, 40),
            Width = 40,
            Height = 600,
            Padding = 0,
            Projection = MapProjection.Goode,
            PointRadius = 1,
        };

        var (_, _, pixels) = Decode(MapRenderer.RenderPng(features, options));

        // Three small discs → three painted row ranges. Take the first painted row in each range
        // as the disc's top; under sinusoidal the middle disc sits exactly between the outer two.
        var rows = PaintedRowGroups(pixels, 40);
        await Assert.That(rows.Count).IsEqualTo(3);
        var midpoint = (rows[0] + rows[2]) / 2.0;
        await Assert.That(Math.Abs(rows[1] - midpoint)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Goode_high_latitude_uses_mollweide_cap()
    {
        // Outside the transition band Goode switches to the Mollweide cap; meridians taper toward
        // the pole. A point well off the central meridian at high latitude should project to a
        // smaller |x| than the same point would under plate carrée (where x doesn't depend on
        // latitude at all). Pulling the point inward of the right bound — lon=160, not 180 — keeps
        // the PlateCarree render comfortably away from the canvas edge so the centroid comparison
        // is meaningful for both projections.
        var features = new FeatureCollection
        {
            new Feature(new Point(160, 70))
        };

        static RenderOptions Build(MapProjection projection) => new()
        {
            Bounds = new Envelope(-180, 0, 180, 80),
            Width = 400,
            Height = 200,
            Padding = 0,
            Projection = projection,
            PointRadius = 1,
        };

        var (_, _, goodePixels) = Decode(MapRenderer.RenderPng(features, Build(MapProjection.Goode)));
        var (_, _, platePixels) = Decode(MapRenderer.RenderPng(features, Build(MapProjection.PlateCarree)));

        var goodeX = NonBackgroundCentroidX(goodePixels, 400);
        var plateX = NonBackgroundCentroidX(platePixels, 400);
        await Assert.That(goodeX).IsGreaterThanOrEqualTo(0);
        await Assert.That(plateX).IsGreaterThanOrEqualTo(0);
        await Assert.That(goodeX).IsLessThan(plateX);
    }

    [Test]
    public async Task Goode_renders_southern_hemisphere_above_transition()
    {
        // The Mollweide branch's y shift is sign-flipped per hemisphere (subtract for north, add for
        // south) — without the southern branch a high-southern-latitude point would either NaN
        // through the rasterizer or land in the wrong row. Render a single Antarctic point and
        // confirm something painted.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, -75))
        };

        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-180, -90, 180, 90),
                Width = 200,
                Height = 100,
                Padding = 0,
                Projection = MapProjection.Goode,
            });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Goode_clamps_polar_latitudes()
    {
        // Mollweide's θ-solver hits a singular derivative at the pole (cos(θ) → 0), so the
        // projection clamps latitude just shy of ±90°. Two points well past any reasonable cutoff
        // (one exactly at the pole, one just inside) must both render without NaNs — and within the
        // clamp's tolerance they project to indistinguishable pixels.
        var atPole = new FeatureCollection
        {
            new Feature(new Point(0, 90))
        };
        var nearPole = new FeatureCollection
        {
            new Feature(new Point(0, 89.999))
        };

        static RenderOptions Build() => new()
        {
            Bounds = new Envelope(-180, -90, 180, 90),
            Width = 200,
            Height = 100,
            Padding = 0,
            Projection = MapProjection.Goode,
            PointRadius = 2,
        };

        var (_, _, atPolePixels) = Decode(MapRenderer.RenderPng(atPole, Build()));
        var (_, _, nearPolePixels) = Decode(MapRenderer.RenderPng(nearPole, Build()));

        await Assert.That(NonBackgroundCount(atPolePixels)).IsGreaterThan(0);
        await Assert.That(atPolePixels).IsEquivalentTo(nearPolePixels);
    }

    [Test]
    public async Task Goode_splits_linestring_at_lobe_boundary()
    {
        // A polyline crossing the lon=-40° boundary in the north must be split into two strokes,
        // each entirely within one lobe. The middle vertex sits in lobe 1 and the outer two in
        // lobe 2, so PrepareLine sees both directions of boundary crossing (lobe2→lobe1 and back).
        // Rendered with a translucent stroke and a wide width so the painted pixels span columns;
        // a single un-split stroke would draw a horizontal line across the inter-lobe gap and
        // leave painted pixels in the centre band, which the gap-pixel count would catch.
        var features = new FeatureCollection
        {
            // Four vertices: -25 and -30 are both in lobe 2 (same-lobe walk), -50 is in lobe 1
            // (boundary crossing), -20 returns to lobe 2 (boundary crossing back). Covers both
            // branches of the boundary-meridian ternary plus the same-lobe append.
            new Feature(new LineString([new(-25, 50), new(-30, 50), new(-50, 50), new(-20, 50)])),
        };

        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-180, -90, 180, 90),
                Width = 1200,
                Projection = MapProjection.Goode,
            });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Goode_splits_linestring_at_equator()
    {
        // A polyline crossing the equator hops between hemispheres, so PrepareLine must split it
        // at lat=0 — InterpolateToBoundary's hemisphere branch picks up the crossing point.
        // Without the split, the line would still render (since the equator transition is smooth
        // in projected x at a fixed lon), but the lobe assignment for stroking would be wrong;
        // this test just confirms the split path is reached for the coverage gate.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(0, 30), new(0, -30)])),
        };

        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-180, -90, 180, 90),
                Width = 800,
                Projection = MapProjection.Goode,
            });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Goode_handles_single_vertex_linestring()
    {
        // SubdividePath bails out at positions.Count < 2 (degenerate input) — exercise that path
        // through a 1-vertex LineString. The renderer should produce a sensible empty image
        // without throwing.
        var features = new FeatureCollection
        {
            new Feature(new LineString([new(0, 0)])),
        };

        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-180, -90, 180, 90),
                Width = 200,
                Projection = MapProjection.Goode,
            });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsEqualTo(0);
    }

    [Test]
    public async Task Goode_finds_nearest_lobe_for_out_of_range_longitude()
    {
        // Malformed input with lon outside [-180, 180] would miss every lobe's lon range; the
        // FindLobe fallback picks the lobe in the right hemisphere whose central meridian is
        // closest, so the projection still produces a finite pixel. Render a point at lon=200°N
        // and confirm it doesn't throw and paints something inside the canvas.
        var features = new FeatureCollection
        {
            new Feature(new Point(200, 50)),
        };

        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-180, -90, 180, 90),
                Width = 400,
                Padding = 0,
                Projection = MapProjection.Goode,
                PointRadius = 3,
            });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Goode_clips_polygon_outside_lobe_to_empty()
    {
        // A small polygon located entirely outside a given lobe's bounds → ClipRing returns a
        // ring with fewer than 3 vertices (often 0) and PreparePolygon skips that lobe. With six
        // lobes only one or two will have content for a small antarctic-region polygon; the rest
        // exercise the ClipHalfPlane "ring entirely outside" path (subsequent passes get an empty
        // ring and short-circuit).
        var features = new FeatureCollection
        {
            // Small triangle inside south lobe 3 (lon ∈ [-20, 80], lat ∈ [-90, 0]).
            new Feature(new Polygon([[new(20, -30), new(40, -30), new(30, -10), new(20, -30)]])),
        };

        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-180, -90, 180, 90),
                Width = 800,
                Projection = MapProjection.Goode,
            });

        var (_, _, pixels) = Decode(png);
        await Assert.That(NonBackgroundCount(pixels)).IsGreaterThan(0);
    }

    [Test]
    public async Task Ocean_fills_envelope_under_features_for_non_goode()
    {
        // RenderOptions.Ocean works for any projection — for rectangular projections it just
        // paints a second background covering the input bounds. Useful when the bounds are
        // smaller than the canvas (padding) so the painted region marks the data extent.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2)]])),
        };

        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 100,
            Height = 100,
            Padding = 10,
            Projection = MapProjection.PlateCarree,
            Background = new(255, 255, 255),
            Ocean = new(100, 150, 200),
            Fill = new(200, 200, 50),
        };

        var (width, _, pixels) = Decode(MapRenderer.RenderPng(features, options));

        // A pixel just inside the bounds rectangle but outside the polygon → painted with the
        // ocean colour. A corner of the canvas (in the padding) → still background white.
        var insideBoundsOutsidePolygon = (width / 8 * width + width / 8) * 4;
        await Assert.That(pixels[insideBoundsOutsidePolygon]).IsEqualTo((byte)100);
        await Assert.That(pixels[insideBoundsOutsidePolygon + 1]).IsEqualTo((byte)150);
        await Assert.That(pixels[insideBoundsOutsidePolygon + 2]).IsEqualTo((byte)200);

        // Top-left pixel sits in the padding margin → never reached by either the bounds rect or
        // the polygon, so it stays the background colour.
        await Assert.That(pixels[0]).IsEqualTo((byte)255);
        await Assert.That(pixels[1]).IsEqualTo((byte)255);
        await Assert.That(pixels[2]).IsEqualTo((byte)255);
    }

    [Test]
    public async Task Ocean_skips_lobes_outside_partial_bounds_in_goode()
    {
        // A north-only render (bounds lat ∈ [0, 90]) leaves the four southern lobes entirely
        // outside the input extent. ClampLobeToBounds returns null for those, so they don't get
        // an ocean ring — without that guard, the southern lobes would still paint outside the
        // requested bounds and bleed into the canvas. Confirm the bottom rows of the canvas (which
        // sit *below* the bounds rect in pixel space) stay the background colour.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 45)),
        };

        var options = new RenderOptions
        {
            Bounds = new Envelope(-180, 0, 180, 90),
            Width = 200,
            Height = 100,
            Padding = 0,
            Projection = MapProjection.Goode,
            Background = new(255, 255, 255),
            Ocean = new(100, 150, 200),
        };

        var (width, height, pixels) = Decode(MapRenderer.RenderPng(features, options));

        // The Y-axis flip in the renderer puts MaxY at the top, MinY at the bottom — so the
        // bottom edge of the canvas corresponds to lat=0. Any blue ocean below that edge would
        // mean a south lobe leaked in. Sample the bottom row's center column.
        var bottomCenter = ((height - 1) * width + width / 2) * 4;
        await Assert.That(pixels[bottomCenter + 2] == 255).IsTrue(); // blue channel is 255 only for background, not the ocean colour
    }

    [Test]
    public Task Render_snapshot_goode_world()
    {
        // The full-world Homolosine view — interrupted into 2N/4S lobes. Locked in as a snapshot
        // so a regression in the sinusoidal-to-Mollweide seam, the y-shift, the Newton solver, or
        // the lobe clipping shows up immediately. Pole-to-pole, full longitude — exercises both
        // projection branches and every lobe. The ocean fill paints each lobe under the
        // continents so the lobe outlines (and the inter-lobe gaps) are visible.
        var features = GeoConverter.Read(ProjectFiles.world_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-180, -90, 180, 90),
                Projection = MapProjection.Goode,
                Ocean = new(200, 220, 240),
            });

        return Verify(png, "png");
    }

    [Test]
    public Task Render_snapshot_web_mercator()
    {
        var features = GeoConverter.Read(ProjectFiles.australian_suburbs_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Width = 3000,
                Projection = MapProjection.WebMercator
            });

        return Verify(png, "png");
    }

    [Test]
    public Task Render_snapshot_web_mercator_world()
    {
        // The full-world Mercator view: bounds at the ±180°/±85.0511° cutoff make the projected world a
        // 1:1 square, matching every tiled-map provider's origin. Locking this in as a snapshot so the
        // shape of a "world map" output is regression-checked alongside the dataset-scoped renders.
        var features = GeoConverter.Read(ProjectFiles.world_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = MapRenderer.WebMercatorWorldBounds,
                Projection = MapProjection.WebMercator,
            });

        return Verify(png, "png");
    }

    [Test]
    public async Task WebMercatorWorldBounds_is_square_when_projected()
    {
        // Sanity-check the published constant: under Web Mercator the longitude range (360°) and the
        // projected latitude range must come out equal — that's the point of the ±85.0511° cutoff.
        var bounds = MapRenderer.WebMercatorWorldBounds;
        await Assert.That(bounds.MinX).IsEqualTo(-180);
        await Assert.That(bounds.MaxX).IsEqualTo(180);

        // Render a single point so we can compare derived width/height: with Padding=0 they should match.
        var features = new FeatureCollection
        {
            new Feature(new Point(0, 0))
        };
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = bounds,
                Width = 256,
                Padding = 0,
                Projection = MapProjection.WebMercator,
            });
        var (width, height, _) = Decode(png);
        await Assert.That(height).IsEqualTo(width);
    }

    [Test]
    public async Task Rejects_non_positive_width()
    {
        var threw = false;
        try
        {
            MapRenderer.RenderPng(Sample.Polygons(), new()
            {
                Width = 0
            });
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Compression_level_affects_output_size()
    {
        // A large render with a repetitive background gives deflate something meaningful to chew on.
        var features = Sample.Polygons();

        static RenderOptions Build(CompressionLevel level) => new()
        {
            Width = 512,
            Height = 512,
            Compression = level,
        };

        var none = MapRenderer.RenderPng(features, Build(CompressionLevel.NoCompression));
        var smallest = MapRenderer.RenderPng(features, Build(CompressionLevel.SmallestSize));

        // Skipping compression entirely should be strictly larger than the smallest-size deflate output.
        await Assert.That(smallest.Length).IsLessThan(none.Length);

        // Both still decode to the same image.
        var (widthA, heightA, _) = Decode(none);
        var (widthB, heightB, _) = Decode(smallest);
        await Assert.That(widthA).IsEqualTo(widthB);
        await Assert.That(heightA).IsEqualTo(heightB);
    }

    [Test]
    public async Task Per_layer_style_overrides_default_colors()
    {
        // Two layers stacked. The root holds a green polygon under a default fill; a child layer holds
        // an offset red polygon. The LayerStyle callback returns the per-layer overrides by name, and
        // each layer's interior should come out in its own color — confirming the override path on all
        // four LayerStyle properties is wired through.
        var root = new FeatureCollection
        {
            Name = "background",
            Features =
            {
                new(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
            },
        };
        var top = new FeatureCollection
        {
            Name = "overlay",
            Features =
            {
                new(new Polygon([[new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2)]])),
            },
        };
        root.Children.Add(top);

        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 64,
            Height = 64,
            Padding = 0,
            // Pinned to PlateCarree so the polygons come out as proper pixel-aligned squares: Auto would
            // pick Lambert at this bounds size and curve the parallels, which is the wrong layout for a
            // styling/z-order regression test.
            Projection = MapProjection.PlateCarree,
            // Defaults set to colors the layers will overwrite — if the override path failed, the
            // background polygon's centre would come out in this fill (blue), not green.
            Fill = new(50, 50, 200),
            Stroke = new(0, 0, 0),
            StrokeWidth = 1,
            PointRadius = 1,
            LayerStyle = layer => layer.Name switch
            {
                "background" => new()
                {
                    Fill = new(20, 200, 20),
                    Stroke = new(20, 200, 20),
                    StrokeWidth = 2,
                    PointRadius = 2,
                },
                "overlay" => new()
                {
                    Fill = new(220, 30, 30),
                    Stroke = new(220, 30, 30),
                    StrokeWidth = 2,
                    PointRadius = 2,
                },
                _ => null,
            },
        };

        var png = MapRenderer.RenderPng(root, options);
        var (width, _, pixels) = Decode(png);

        // Sample a pixel inside the outer polygon but outside the overlay (top-left corner area).
        var outer = (width / 16 * width + width / 16) * 4;
        await Assert.That(pixels[outer]).IsEqualTo((byte)20);
        await Assert.That(pixels[outer + 1]).IsEqualTo((byte)200);
        await Assert.That(pixels[outer + 2]).IsEqualTo((byte)20);

        // Sample the centre: it sits inside both polygons, but the overlay paints after its parent so
        // it should win (red, not green).
        var centre = (width / 2 * width + width / 2) * 4;
        await Assert.That(pixels[centre]).IsEqualTo((byte)220);
        await Assert.That(pixels[centre + 1]).IsEqualTo((byte)30);
        await Assert.That(pixels[centre + 2]).IsEqualTo((byte)30);

        await Verify(png, "png");
    }

    [Test]
    public async Task Layer_style_null_properties_inherit_defaults()
    {
        // A LayerStyle with all properties left null must fall through to the RenderOptions defaults
        // — confirms the null-coalescing right-side path when the callback returns a non-null style
        // whose individual overrides are absent.
        var features = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
        };
        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 32,
            Height = 32,
            Padding = 0,
            // Pinned to PlateCarree for the same reason as Per_layer_style_overrides_default_colors:
            // Auto would pick Lambert here and curve the polygon edges.
            Projection = MapProjection.PlateCarree,
            Fill = new(180, 60, 30),
            LayerStyle = _ => new(),
        };

        var png = MapRenderer.RenderPng(features, options);
        var (width, _, pixels) = Decode(png);
        var centre = (width / 2 * width + width / 2) * 4;
        await Assert.That(pixels[centre]).IsEqualTo((byte)180);
        await Assert.That(pixels[centre + 1]).IsEqualTo((byte)60);
        await Assert.That(pixels[centre + 2]).IsEqualTo((byte)30);

        await Verify(png, "png");
    }

    [Test]
    public async Task Multiple_collections_render_in_order_with_per_collection_style()
    {
        // Two independent FeatureCollections passed as a list — first under, second on top. Each
        // collection is a top-level layer for the LayerStyle callback, so this is the natural way to
        // stack a basemap under an overlay without having to wedge them into a single tree.
        var lower = new FeatureCollection
        {
            Name = "lower",
            Features =
            {
                new(new Polygon([[new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]])),
            },
        };
        var upper = new FeatureCollection
        {
            Name = "upper",
            Features =
            {
                new(new Polygon([[new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2)]])),
            },
        };

        var options = new RenderOptions
        {
            Bounds = new Envelope(0, 0, 10, 10),
            Width = 64,
            Height = 64,
            Padding = 0,
            Projection = MapProjection.PlateCarree,
            LayerStyle = layer => layer.Name switch
            {
                "lower" => new() { Fill = new(20, 200, 20), Stroke = new(20, 200, 20), StrokeWidth = 2 },
                "upper" => new() { Fill = new(220, 30, 30), Stroke = new(220, 30, 30), StrokeWidth = 2 },
                _ => null,
            },
        };

        var png = MapRenderer.RenderPng([lower, upper], options);
        var (width, _, pixels) = Decode(png);

        // Outside the upper polygon → lower's green wins.
        var outer = (width / 16 * width + width / 16) * 4;
        await Assert.That(pixels[outer]).IsEqualTo((byte)20);
        await Assert.That(pixels[outer + 1]).IsEqualTo((byte)200);
        await Assert.That(pixels[outer + 2]).IsEqualTo((byte)20);

        // Inside both → upper (last in the list) wins on top.
        var centre = (width / 2 * width + width / 2) * 4;
        await Assert.That(pixels[centre]).IsEqualTo((byte)220);
        await Assert.That(pixels[centre + 1]).IsEqualTo((byte)30);
        await Assert.That(pixels[centre + 2]).IsEqualTo((byte)30);

        await Verify(png, "png");
    }

    [Test]
    public async Task Multiple_collections_default_bounds_to_union()
    {
        // No Bounds set: the rendered extent must cover every input collection, not just the first.
        // Two disjoint single-point features near opposite sides of a shared region — if union bounds
        // weren't applied, the east point would clip out (only the west collection's bounds would
        // drive the projection) and the rightmost non-bg column would stay close to 0.
        var westPoint = new FeatureCollection
        {
            Features =
            {
                new(new Point(-40, 0))
            },
        };
        var eastPoint = new FeatureCollection
        {
            Features =
            {
                new(new Point(40, 0))
            },
        };

        var options = new RenderOptions
        {
            Width = 200,
            Height = 40,
            Padding = 4,
            Projection = MapProjection.PlateCarree,
            PointRadius = 3,
        };

        var (width, _, pixels) = Decode(MapRenderer.RenderPng([westPoint, eastPoint], options));

        var rightmost = -1;
        for (var p = 0; p + 4 <= pixels.Length; p += 4)
        {
            if (pixels[p] == 255 && pixels[p + 1] == 255 && pixels[p + 2] == 255)
            {
                continue;
            }

            var column = p / 4 % width;
            if (column > rightmost)
            {
                rightmost = column;
            }
        }

        // If only the west collection's bounds drove the projection, the east point would project
        // off-canvas and the rightmost painted column would sit far left of centre.
        await Assert.That(rightmost).IsGreaterThan(width / 2);
    }

    [Test]
    public async Task Empty_collection_list_throws()
    {
        // An empty list of layers is the multi-FC analogue of an empty single FC: nothing to render
        // and no bounds to fall back on, so the validator must throw rather than emit an empty PNG.
        FeatureCollection[] empty = [];
        var threw = false;
        try
        {
            MapRenderer.RenderPng(empty);
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Path_overload_leaves_no_file_when_render_throws()
    {
        // Regression: previously the path overload opened the destination file before validation ran,
        // so an empty-collection throw left a 0-byte PNG on disk indistinguishable from a real render.
        using var path = new TempFile();
        var threw = false;
        try
        {
            MapRenderer.RenderPng(new FeatureCollection(), path);
        }
        catch (GeoConvertException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    static int NonBackgroundCount(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 ||
                pixels[i + 1] != 255 ||
                pixels[i + 2] != 255)
            {
                count++;
            }
        }

        return count;
    }

    static List<int> PaintedRowGroups(byte[] pixels, int width)
    {
        // First painted row of each contiguous painted-row group. Walking row by row, a row is
        // "painted" if it contains any non-background pixel; consecutive painted rows belong to the
        // same group (one disc). Returns the top row of each group in image order.
        var groups = new List<int>();
        var height = pixels.Length / 4 / width;
        var inGroup = false;
        for (var row = 0; row < height; row++)
        {
            var rowHasPaint = false;
            for (var column = 0; column < width; column++)
            {
                var offset = (row * width + column) * 4;
                if (pixels[offset] != 255 ||
                    pixels[offset + 1] != 255 ||
                    pixels[offset + 2] != 255)
                {
                    rowHasPaint = true;
                    break;
                }
            }

            if (rowHasPaint && !inGroup)
            {
                groups.Add(row);
            }

            inGroup = rowHasPaint;
        }

        return groups;
    }

    static double NonBackgroundCentroidX(byte[] pixels, int width)
    {
        // Average X of every non-background pixel — a stable summary of where a small drawn feature
        // lands, even when it spans a few columns (point disc), without depending on exact stroke
        // bookkeeping.
        double sum = 0;
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255)
            {
                continue;
            }

            sum += i / 4 % width;
            count++;
        }

        return count == 0 ? -1 : sum / count;
    }

    // A small decoder for GeoConvert's own PNG output (8-bit RGBA, filter 0 rows).
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
}
