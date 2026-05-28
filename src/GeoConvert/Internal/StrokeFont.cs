/// <summary>
/// A hand-rolled single-stroke sans-serif vector font in the Hershey idiom — each glyph is a list of
/// polyline strokes in a flat coordinate space, rendered through <see cref="Canvas.StrokeLine"/>.
/// Unlike a bitmap font, scaling is smooth: a glyph at size 30 traces the same paths as a glyph at
/// size 10, just larger, so the only artefact is line aliasing on diagonals (the same as any other
/// rendered geometry) rather than visible pixel blocks.
/// <para>
/// Coordinate convention: x grows right, y grows up, baseline at y=0, cap height at y=14, lowercase
/// x-height at y=10, descenders down to y=-4, combining marks reach up to y=17. Per-glyph width is
/// variable (proportional). The renderer scales font units to pixels by <paramref>size</paramref>/14
/// so the `size` parameter reads as cap height in pixels.
/// </para>
/// <para>
/// Coverage is printable ASCII (U+0020–U+007E) for the base glyphs, plus a handful of combining
/// marks (grave, acute, circumflex, tilde, diaeresis, ring above, caron, cedilla). Input is
/// normalised to NFD so precomposed forms — "ô" (U+00F4), "Á" (U+00C1), "ñ" (U+00F1) and the rest
/// of Latin-1 Supplement / Latin Extended-A that decomposes into one of those marks — render as
/// the ASCII base glyph with the mark stroked above (or below, for cedilla). Characters that
/// don't decompose to an ASCII base (ß, æ, ø, þ, Đ, the Greek/Cyrillic blocks, CJK, …) still
/// substitute as '?'.
/// </para>
/// <para>
/// The font is intentionally not historic Hershey — that data is public-domain but reproducing it
/// from memory would be unverifiable. These glyphs are designed from scratch with the same
/// geometric approach (single-stroke, polyline approximation of curves) but lower fidelity on
/// detail. Sufficient for short cartographic labels; not a typographic deliverable.
/// </para>
/// </summary>
static class StrokeFont
{
    public const double CapHeight = 14;

    // The glyph cell extends from y=-4 (deepest descender on g/j/p/q/y) to y=17 (top of combining
    // marks like circumflex / caron sitting one font unit above the cap line). The collision pass
    // uses these so a label's bbox covers every pixel it could paint, not just the cap rectangle —
    // without that, two lines of text at safe-looking cap-to-cap spacing have descenders or
    // accents biting into the next line's caps.
    public const double AscenderTop = 17;
    public const double DescenderBottom = -4;

    // Horizontal gap between glyph cells, in font units. 2 units at default size 14 → ~1.4 pixels;
    // enough to keep adjacent letters from running into each other without spreading text out.
    const double tracking = 2;

    public readonly record struct Glyph(double Width, (double X, double Y)[][] Strokes);

    static readonly Dictionary<char, Glyph> glyphs = BuildGlyphs();

    // Combining marks (Unicode NonSpacingMark, U+0300+). Strokes are defined centred on x=0 in
    // font units — they're positioned at the horizontal centre of the preceding base glyph rather
    // than at the pen position, and don't advance the pen.
    static readonly Dictionary<char, (double X, double Y)[][]> marks = BuildMarks();

    /// <summary>Measures the rendered size of <paramref name="text"/> at the given cap-height
    /// <paramref name="size"/> in pixels.</summary>
    public static (double Width, double Height) Measure(string text, double size)
    {
        // Normalise so precomposed accented characters split into base + combining mark — only the
        // base glyph contributes width.
        var normalized = text.Normalize(NormalizationForm.FormD);
        if (normalized.Length == 0)
        {
            return (0, size);
        }

        var unit = size / CapHeight;
        var widthInFontUnits = 0d;
        var baseCount = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (IsCombiningMark(normalized[i]))
            {
                continue;
            }

            if (baseCount > 0)
            {
                widthInFontUnits += tracking;
            }

            widthInFontUnits += GlyphFor(normalized[i]).Width;
            baseCount++;
        }

        // Glyph cells span y ∈ [-4, 17] but the measured height is the cap height — descenders
        // and accents extend slightly above/below the reported bbox. Labeller uses the bbox for
        // collision; tighter centring on cap height matches what the eye reads as "the text".
        return (widthInFontUnits * unit, size);
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its baseline at <paramref name="baselineY"/> and the
    /// leftmost glyph stroke starting at <paramref name="leftX"/>. When <paramref name="halo"/>
    /// is non-null, the strokes are first drawn in the halo colour at a slightly larger width so
    /// they extend a pixel beyond the foreground stroke on every side.
    /// </summary>
    public static void Render(Canvas canvas, string text, double leftX, double baselineY, double size, Rgba color, Rgba? halo)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var unit = size / CapHeight;
        // Stroke widths scale gently with size: keeps small text crisp (width 1) and grows the
        // weight as text gets bigger so big text doesn't look reedy. Halo is one pixel wider so
        // it rings the foreground stroke uniformly.
        var strokeWidth = Math.Max(1, size / 12);
        var haloWidth = strokeWidth + 2;

        if (halo is { } haloColor)
        {
            DrawStrokes(canvas, normalized, leftX, baselineY, unit, haloWidth, haloColor);
        }

        DrawStrokes(canvas, normalized, leftX, baselineY, unit, strokeWidth, color);
    }

    static void DrawStrokes(Canvas canvas, string text, double leftX, double baselineY, double unit, double strokeWidth, Rgba color)
    {
        var penX = leftX;
        var previousBaseCentreX = 0d;
        var hasPreviousBase = false;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (marks.TryGetValue(character, out var markStrokes))
            {
                if (!hasPreviousBase)
                {
                    // Leading combining mark with no base to attach to — drop silently rather than
                    // anchor it at leftX, which would float in space.
                    continue;
                }

                DrawAt(canvas, markStrokes, previousBaseCentreX, baselineY, unit, strokeWidth, color);
                continue;
            }

            if (IsCombiningMark(character))
            {
                // Combining mark outside our table (some Vietnamese tone marks, stacked accents
                // beyond the basic Latin set). Drop rather than substitute '?' — the base glyph
                // is already on the canvas, so swallowing the accent reads better than a stray
                // '?' beside a correctly-rendered letter.
                continue;
            }

            var glyph = GlyphFor(character);
            if (hasPreviousBase)
            {
                penX += tracking * unit;
            }

            DrawAt(canvas, glyph.Strokes, penX, baselineY, unit, strokeWidth, color);
            previousBaseCentreX = penX + glyph.Width * unit / 2;
            penX += glyph.Width * unit;
            hasPreviousBase = true;
        }
    }

    static void DrawAt(Canvas canvas, (double X, double Y)[][] strokes, double originX, double baselineY, double unit, double strokeWidth, Rgba color)
    {
        foreach (var stroke in strokes)
        {
            for (var p = 0; p + 1 < stroke.Length; p++)
            {
                // Y is flipped: font coords grow up from the baseline, canvas pixels grow down
                // from the top. So baselineY - y * unit lands the y=0 vertex on the baseline
                // row and y=14 vertices `cap*unit` pixels above it.
                var ax = originX + stroke[p].X * unit;
                var ay = baselineY - stroke[p].Y * unit;
                var bx = originX + stroke[p + 1].X * unit;
                var by = baselineY - stroke[p + 1].Y * unit;
                canvas.StrokeLine(ax, ay, bx, by, strokeWidth, color);
            }
        }
    }

    static bool IsCombiningMark(char character) =>
        CharUnicodeInfo.GetUnicodeCategory(character) ==
        UnicodeCategory.NonSpacingMark;

    static Glyph GlyphFor(char character) =>
        glyphs.TryGetValue(character, out var g) ? g : glyphs['?'];

    // Compact stroke-list builder so the glyph table reads as one line per character.
    static Glyph G(double width, params (double X, double Y)[][] strokes) =>
        new(width, strokes);

    static (double X, double Y)[] S(params (double, double)[] points) => points;

    // Combining-mark strokes, defined in font units centred on x=0 and sitting just above the cap
    // line (y ∈ [15, 17]) or, for cedilla, hanging below the baseline (y ∈ [-3, 0]). The renderer
    // translates them to the centre of the preceding base glyph at draw time, so a wide letter
    // like 'M' and a narrow letter like 'i' both get their accent in the right place without
    // per-glyph anchor tables. Marks are written as literal combining characters in source —
    // they visually attach to the closing quote of the char literal but are otherwise valid.
    static Dictionary<char, (double X, double Y)[][]> BuildMarks() =>
        new()
        {
            ['̀'] = [S((-2, 17), (1, 15))],                       // grave
            ['́'] = [S((-1, 15), (2, 17))],                       // acute
            ['̂'] = [S((-2, 15), (0, 17), (2, 15))],              // circumflex
            ['̃'] = [S((-3, 16), (-1, 17), (1, 15), (3, 16))],    // tilde
            ['̈'] = [S((-2, 15), (-2, 16)), S((2, 15), (2, 16))], // diaeresis (two dots)
            ['̊'] = [Ring(0, 16, 1, 1)],                          // ring above
            ['̌'] = [S((-2, 17), (0, 15), (2, 17))],              // caron (hacek)
            ['̧'] = [S((0, 0), (0, -2), (-2, -3))],               // cedilla
        };

    // Approximated 12-gon ring (shared by BuildGlyphs and BuildMarks). Centre at (cx, cy), radii
    // (rx, ry). Returns the 13 vertices of a closed loop (last == first) ready to be passed
    // straight to StrokeLine via a polyline. A 12-segment approximation reads smooth at typical
    // label sizes without bloating the tables.
    static (double X, double Y)[] Ring(double cx, double cy, double rx, double ry)
    {
        const int segments = 12;
        var points = new (double X, double Y)[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var angle = i * 2 * Math.PI / segments;
            points[i] = (cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle));
        }

        return points;
    }

    static Dictionary<char, Glyph> BuildGlyphs()
    {
        // Same as Ring but only a portion of the ellipse — used for C, c, e, etc. that aren't
        // closed. startAngle and endAngle are in turns (1.0 = full revolution) measured clockwise
        // from 3 o'clock (positive y up).
        static (double X, double Y)[] Arc(double cx, double cy, double rx, double ry, double startTurns, double endTurns, int segments = 8)
        {
            var points = new (double X, double Y)[segments + 1];
            for (var i = 0; i <= segments; i++)
            {
                var t = startTurns + (endTurns - startTurns) * i / segments;
                var angle = t * 2 * Math.PI;
                points[i] = (cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle));
            }

            return points;
        }

        return new()
        {
            // Space and punctuation
            [' '] = G(6),
            ['!'] = G(3, S((1.5, 14), (1.5, 4)), S((1.5, 1), (1.5, 0))),
            ['"'] = G(5, S((1, 14), (1, 10)), S((4, 14), (4, 10))),
            ['#'] = G(10, S((3, 16), (1, -2)), S((7, 16), (5, -2)), S((0, 11), (9, 11)), S((0, 5), (9, 5))),
            ['$'] = G(9, S((4, 16), (4, -2)), S((7, 13), (5, 14), (2, 14), (0, 12), (0, 10), (2, 8), (6, 8), (8, 6), (8, 4), (6, 2), (2, 2), (0, 4))),
            ['%'] = G(12, Ring(2, 11, 2, 2.5), Ring(10, 3, 2, 2.5), S((0, 0), (12, 14))),
            ['&'] = G(11, S((10, 0), (3, 14), (2, 13), (2, 10), (8, 4), (8, 1), (7, 0), (4, 0), (1, 3), (1, 6), (10, 14))),
            ['\''] = G(2, S((1, 14), (1, 10))),
            ['('] = G(5, S((4, 16), (1, 12), (1, 2), (4, -2))),
            [')'] = G(5, S((1, 16), (4, 12), (4, 2), (1, -2))),
            ['*'] = G(9, S((4, 12), (4, 4)), S((1, 10), (7, 6)), S((1, 6), (7, 10))),
            ['+'] = G(9, S((4, 12), (4, 2)), S((0, 7), (8, 7))),
            [','] = G(3, S((2, 1), (2, 0), (1, -2))),
            ['-'] = G(7, S((1, 7), (6, 7))),
            ['.'] = G(3, S((1, 1), (2, 1), (2, 0), (1, 0), (1, 1))),
            ['/'] = G(9, S((0, -2), (8, 16))),

            // Digits
            ['0'] = G(9, Ring(4.5, 7, 4, 6.5)),
            ['1'] = G(6, S((1, 12), (3, 14), (3, 0)), S((1, 0), (5, 0))),
            ['2'] = G(9, S((0, 12), (2, 14), (6, 14), (8, 12), (8, 10), (0, 0), (8, 0))),
            ['3'] = G(9, S((0, 14), (8, 14), (3, 8), (6, 8), (8, 6), (8, 2), (6, 0), (2, 0), (0, 2))),
            ['4'] = G(9, S((6, 0), (6, 14), (0, 4), (9, 4))),
            ['5'] = G(9, S((8, 14), (1, 14), (0, 8), (2, 9), (6, 9), (8, 7), (8, 2), (6, 0), (2, 0), (0, 2))),
            ['6'] = G(9, S((8, 12), (6, 14), (3, 14), (1, 12), (0, 8), (0, 2), (2, 0), (6, 0), (8, 2), (8, 5), (6, 7), (2, 7), (0, 5))),
            ['7'] = G(9, S((0, 14), (8, 14), (3, 0))),
            ['8'] = G(9, Ring(4.5, 10.5, 3.5, 3), Ring(4.5, 3.5, 4, 3.5)),
            ['9'] = G(9, S((1, 2), (3, 0), (6, 0), (8, 2), (8, 12), (6, 14), (3, 14), (1, 12), (1, 9), (3, 7), (7, 7), (8, 9))),
            [':'] = G(3, S((1.5, 10), (1.5, 9)), S((1.5, 4), (1.5, 3))),
            [';'] = G(3, S((2, 10), (2, 9)), S((2, 4), (2, 3), (1, -2))),
            ['<'] = G(9, S((8, 12), (0, 7), (8, 2))),
            ['='] = G(9, S((0, 9), (8, 9)), S((0, 5), (8, 5))),
            ['>'] = G(9, S((0, 12), (8, 7), (0, 2))),
            ['?'] = G(8, S((0, 12), (2, 14), (6, 14), (8, 12), (8, 10), (4, 6), (4, 4)), S((4, 1), (4, 0))),
            ['@'] = G(12, Ring(6, 7, 5.5, 6)),

            // Uppercase letters
            ['A'] = G(10, S((0, 0), (5, 14), (10, 0)), S((2, 5), (8, 5))),
            ['B'] = G(10,
                S((0, 0), (0, 14), (7, 14), (9, 12), (9, 9), (7, 7), (0, 7)),
                S((0, 7), (7, 7), (9, 5), (9, 2), (7, 0), (0, 0))),
            // C/G/c: arc opens to the right. Sweeps CCW from 30° (upper-right shoulder, where the
            // opening starts) around through top → left → bottom to 330° (lower-right shoulder).
            // 300° of arc means 12 segments keeps the curve smooth at typical label sizes.
            ['C'] = G(10, Arc(5, 7, 5, 7, 0.083, 0.917, 12)),
            ['D'] = G(10, S((0, 0), (0, 14), (5, 14), (9, 11), (9, 3), (5, 0), (0, 0))),
            ['E'] = G(9, S((8, 14), (0, 14), (0, 0), (8, 0)), S((0, 7), (5, 7))),
            ['F'] = G(9, S((0, 0), (0, 14), (8, 14)), S((0, 7), (5, 7))),
            // G is a C with an inward-pointing tongue from the lower-right opening: up to the
            // middle, then left into the body. (9.33, 3.5) is the arc's end point at 330°
            // (cx + r·cos(330°), cy + r·sin(330°)) = (5 + 4.33, 7 - 3.5).
            ['G'] = G(10, Arc(5, 7, 5, 7, 0.083, 0.917, 12), S((9.33, 3.5), (9.33, 7), (5, 7))),
            ['H'] = G(10, S((0, 0), (0, 14)), S((10, 0), (10, 14)), S((0, 7), (10, 7))),
            ['I'] = G(3, S((1.5, 0), (1.5, 14))),
            ['J'] = G(7, S((6, 14), (6, 3), (4, 0), (2, 0), (0, 3))),
            ['K'] = G(10, S((0, 0), (0, 14)), S((0, 7), (10, 14)), S((0, 7), (10, 0))),
            ['L'] = G(8, S((0, 14), (0, 0), (7, 0))),
            ['M'] = G(12, S((0, 0), (0, 14), (6, 7), (12, 14), (12, 0))),
            ['N'] = G(11, S((0, 0), (0, 14), (10, 0), (10, 14))),
            ['O'] = G(11, Ring(5.5, 7, 5, 7)),
            ['P'] = G(10, S((0, 0), (0, 14), (7, 14), (9, 12), (9, 9), (7, 7), (0, 7))),
            ['Q'] = G(11, Ring(5.5, 7, 5, 7), S((6, 3), (11, -2))),
            ['R'] = G(11, S((0, 0), (0, 14), (7, 14), (9, 12), (9, 9), (7, 7), (0, 7)), S((5, 7), (10, 0))),
            ['S'] = G(9, S((8, 13), (6, 14), (2, 14), (0, 12), (0, 10), (2, 8), (6, 6), (8, 4), (8, 2), (6, 0), (2, 0), (0, 1))),
            ['T'] = G(9, S((0, 14), (8, 14)), S((4, 14), (4, 0))),
            ['U'] = G(10, S((0, 14), (0, 3), (2, 0), (7, 0), (9, 3), (9, 14))),
            ['V'] = G(10, S((0, 14), (5, 0), (10, 14))),
            ['W'] = G(13, S((0, 14), (3, 0), (6.5, 8), (10, 0), (13, 14))),
            ['X'] = G(10, S((0, 14), (10, 0)), S((0, 0), (10, 14))),
            ['Y'] = G(10, S((0, 14), (5, 7), (10, 14)), S((5, 7), (5, 0))),
            ['Z'] = G(9, S((0, 14), (8, 14), (0, 0), (8, 0))),

            ['['] = G(5, S((4, 16), (1, 16), (1, -2), (4, -2))),
            ['\\'] = G(9, S((0, 16), (8, -2))),
            [']'] = G(5, S((1, 16), (4, 16), (4, -2), (1, -2))),
            ['^'] = G(9, S((1, 10), (4, 14), (7, 10))),
            ['_'] = G(9, S((0, -2), (8, -2))),
            ['`'] = G(4, S((1, 14), (3, 12))),

            // Lowercase letters
            ['a'] = G(9, Ring(4, 5, 4, 5), S((8, 10), (8, 0))),
            ['b'] = G(9, S((0, 0), (0, 14)), Ring(4.5, 5, 4.5, 5)),
            ['c'] = G(9, Arc(4.5, 5, 4.5, 5, 0.083, 0.917, 10)),
            ['d'] = G(9, S((9, 0), (9, 14)), Ring(4.5, 5, 4.5, 5)),
            ['e'] = G(9, S((0, 5), (9, 5), (9, 7), (7, 10), (3, 10), (0, 7), (0, 3), (3, 0), (7, 0), (9, 2))),
            ['f'] = G(6, S((6, 13), (4, 14), (2, 12), (2, 0)), S((0, 8), (5, 8))),
            ['g'] = G(9, Ring(4.5, 5, 4.5, 5), S((9, 10), (9, -2), (7, -4), (3, -4), (1, -2))),
            ['h'] = G(9, S((0, 0), (0, 14)), S((0, 7), (3, 10), (6, 10), (8, 8), (8, 0))),
            ['i'] = G(3, S((1.5, 0), (1.5, 10)), S((1.5, 12), (1.5, 13))),
            ['j'] = G(4, S((2, -4), (0, -4), (-1, -2), (2, 0), (2, 10)), S((2, 12), (2, 13))),
            ['k'] = G(8, S((0, 0), (0, 14)), S((0, 4), (6, 10)), S((0, 4), (6, 0))),
            ['l'] = G(3, S((1.5, 0), (1.5, 14))),
            ['m'] = G(13,
                S((0, 0), (0, 10)),
                S((0, 8), (2, 10), (5, 10), (6, 9), (6, 0)),
                S((6, 8), (8, 10), (11, 10), (13, 8), (13, 0))),
            ['n'] = G(9, S((0, 0), (0, 10)), S((0, 7), (3, 10), (6, 10), (8, 8), (8, 0))),
            ['o'] = G(9, Ring(4.5, 5, 4.5, 5)),
            ['p'] = G(9, S((0, -4), (0, 10)), Ring(4.5, 5, 4.5, 5)),
            ['q'] = G(9, S((9, -4), (9, 10)), Ring(4.5, 5, 4.5, 5)),
            ['r'] = G(7, S((0, 0), (0, 10)), S((0, 5), (3, 10), (6, 10))),
            ['s'] = G(8, S((7, 9), (5, 10), (2, 10), (0, 8), (0, 7), (2, 5), (5, 5), (7, 3), (7, 2), (5, 0), (2, 0), (0, 1))),
            ['t'] = G(6, S((3, 14), (3, 2), (5, 0)), S((1, 10), (5, 10))),
            ['u'] = G(9, S((0, 10), (0, 2), (3, 0), (6, 0), (8, 2)), S((8, 10), (8, 0))),
            ['v'] = G(8, S((0, 10), (4, 0), (8, 10))),
            ['w'] = G(11, S((0, 10), (3, 0), (5.5, 6), (8, 0), (11, 10))),
            ['x'] = G(8, S((0, 10), (7, 0)), S((0, 0), (7, 10))),
            ['y'] = G(9, S((0, 10), (4, 0)), S((8, 10), (0, -4))),
            ['z'] = G(8, S((0, 10), (7, 10), (0, 0), (7, 0))),

            ['{'] = G(5, S((4, 16), (2, 14), (2, 9), (1, 7), (2, 5), (2, 0), (4, -2))),
            ['|'] = G(2, S((1, 16), (1, -4))),
            ['}'] = G(5, S((1, 16), (3, 14), (3, 9), (4, 7), (3, 5), (3, 0), (1, -2))),
            ['~'] = G(9, S((0, 8), (2, 10), (5, 8), (7, 10))),
        };
    }
}
