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
            MapRenderer.RenderPng(new());
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

        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Bounds = new Envelope(-1, -1, 11, 9),
                Width = 300,
                Height = 220
            });

        return Verify(new MemoryStream(png), "png");
    }

    [Test]
    public Task Render_RealMap()
    {
        var features = GeoConverter.Read(ProjectFiles.australian_suburbs_geojson);
        var png = MapRenderer.RenderPng(
            features,
            new()
            {
                Width = 3000
            });

        return Verify(new MemoryStream(png), "png");
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
        static FeatureCollection Build() => new()
        {
            // Far west corner of a US-shaped bounds: under Lambert this point swings inward toward the
            // central meridian, so it should land further right than under linear PlateCarree.
            new Feature(new Point(-120, 30))
        };

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

        return Verify(new MemoryStream(png), "png");
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

        return Verify(new MemoryStream(png), "png");
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
                Width = 1200,
                Projection = MapProjection.WebMercator,
            });

        return Verify(new MemoryStream(png), "png");
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
    public async Task Path_overload_leaves_no_file_when_render_throws()
    {
        // Regression: previously the path overload opened the destination file before validation ran,
        // so an empty-collection throw left a 0-byte PNG on disk indistinguishable from a real render.
        using var path = new TempFile();
        var threw = false;
        try
        {
            MapRenderer.RenderPng(new(), path);
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
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
            {
                count++;
            }
        }

        return count;
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

            sum += (i / 4) % width;
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
