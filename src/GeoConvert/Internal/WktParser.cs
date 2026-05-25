/// <summary>Recursive-descent parser for a single WKT geometry.</summary>
sealed class WktParser(string text)
{
    int pos;

    public Geometry ParseGeometry()
    {
        var geometry = ParseTagged();
        SkipWhitespace();
        if (pos == text.Length)
        {
            return geometry;
        }

        throw Error("unexpected trailing characters");
    }

    Geometry ParseTagged()
    {
        var keyword = ReadWord();
        var hasZ = false;
        var hasM = false;
        var dimension = PeekWord();
        if (dimension is "Z" or "M" or "ZM" or "MZ")
        {
            ReadWord();
            hasZ = dimension.Contains('Z');
            hasM = dimension.Contains('M');
        }

        if (PeekWord() == "EMPTY")
        {
            ReadWord();
            return Empty(keyword);
        }

        return keyword switch
        {
            "POINT" => new Point(ReadParenthesizedCoordinate(hasZ, hasM)),
            "LINESTRING" => new LineString(ReadCoordinateList(hasZ, hasM)),
            "POLYGON" => new Polygon(ReadRingList(hasZ, hasM)),
            "MULTIPOINT" => ReadMultiPoint(hasZ, hasM),
            "MULTILINESTRING" => ReadMultiLineString(hasZ, hasM),
            "MULTIPOLYGON" => ReadMultiPolygon(hasZ, hasM),
            "GEOMETRYCOLLECTION" => ReadGeometryCollection(),
            _ => throw Error($"unknown geometry keyword '{keyword}'"),
        };
    }

    static Geometry Empty(string keyword) =>
        keyword switch
        {
            "POINT" => new Point(new(double.NaN, double.NaN)),
            "LINESTRING" => new LineString([]),
            "POLYGON" => new Polygon([]),
            "MULTIPOINT" => new MultiPoint([]),
            "MULTILINESTRING" => new MultiLineString([]),
            "MULTIPOLYGON" => new MultiPolygon([]),
            "GEOMETRYCOLLECTION" => new GeometryCollection([]),
            _ => throw new GeoConvertException($"unknown geometry keyword '{keyword}'"),
        };

    MultiPoint ReadMultiPoint(bool hasZ, bool hasM)
    {
        Expect('(');
        var positions = new List<Position>();
        do
        {
            SkipWhitespace();
            if (Peek() == '(')
            {
                positions.Add(ReadParenthesizedCoordinate(hasZ, hasM));
            }
            else
            {
                positions.Add(ReadCoordinate(hasZ, hasM));
            }
        }
        while (TryConsumeComma());

        Expect(')');
        return new(positions);
    }

    MultiLineString ReadMultiLineString(bool hasZ, bool hasM)
    {
        Expect('(');
        var lines = new List<LineString>();
        do
        {
            lines.Add(new(ReadCoordinateList(hasZ, hasM)));
        }
        while (TryConsumeComma());

        Expect(')');
        return new(lines);
    }

    MultiPolygon ReadMultiPolygon(bool hasZ, bool hasM)
    {
        Expect('(');
        var polygons = new List<Polygon>();
        do
        {
            polygons.Add(new(ReadRingList(hasZ, hasM)));
        }
        while (TryConsumeComma());

        Expect(')');
        return new(polygons);
    }

    GeometryCollection ReadGeometryCollection()
    {
        Expect('(');
        var geometries = new List<Geometry>();
        do
        {
            geometries.Add(ParseTagged());
        }
        while (TryConsumeComma());

        Expect(')');
        return new(geometries);
    }

    List<IReadOnlyList<Position>> ReadRingList(bool hasZ, bool hasM)
    {
        Expect('(');
        var rings = new List<IReadOnlyList<Position>>();
        do
        {
            rings.Add(ReadCoordinateList(hasZ, hasM));
        }
        while (TryConsumeComma());

        Expect(')');
        return rings;
    }

    List<Position> ReadCoordinateList(bool hasZ, bool hasM)
    {
        Expect('(');
        var positions = new List<Position>();
        do
        {
            positions.Add(ReadCoordinate(hasZ, hasM));
        }
        while (TryConsumeComma());

        Expect(')');
        return positions;
    }

    Position ReadParenthesizedCoordinate(bool hasZ, bool hasM)
    {
        Expect('(');
        var position = ReadCoordinate(hasZ, hasM);
        Expect(')');
        return position;
    }

    Position ReadCoordinate(bool hasZ, bool hasM)
    {
        var x = ReadNumber();
        var y = ReadNumber();
        double? z = null;
        double? m = null;
        if (hasZ && hasM)
        {
            z = ReadNumber();
            m = ReadNumber();
        }
        else if (hasZ)
        {
            z = ReadNumber();
        }
        else if (hasM)
        {
            m = ReadNumber();
        }
        else
        {
            // No explicit dimension tag; greedily absorb a third (Z) and fourth (M) ordinate.
            if (PeekIsNumber())
            {
                z = ReadNumber();
                if (PeekIsNumber())
                {
                    m = ReadNumber();
                }
            }
        }

        return new(x, y, z, m);
    }

    double ReadNumber()
    {
        SkipWhitespace();
        var start = pos;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsDigit(c) ||
                c is '+' or '-' or '.' or 'e' or 'E')
            {
                pos++;
            }
            else
            {
                break;
            }
        }

        if (pos == start)
        {
            throw Error("expected a number");
        }

        var token = text.AsSpan(start, pos - start);
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw Error($"invalid number '{token}'");
        }

        return value;
    }

    bool PeekIsNumber()
    {
        SkipWhitespace();
        if (pos >= text.Length)
        {
            return false;
        }

        var c = text[pos];
        return char.IsDigit(c) ||
               c is '+' or '-' or '.';
    }

    string ReadWord()
    {
        SkipWhitespace();
        var start = pos;
        while (pos < text.Length &&
               char.IsLetter(text[pos]))
        {
            pos++;
        }

        if (pos == start)
        {
            throw Error("expected a keyword");
        }

        return text.AsSpan(start, pos - start).ToString().ToUpperInvariant();
    }

    string? PeekWord()
    {
        var saved = pos;
        SkipWhitespace();
        var start = pos;
        while (pos < text.Length && char.IsLetter(text[pos]))
        {
            pos++;
        }

        var word = pos == start ? null : text.AsSpan(start, pos - start).ToString().ToUpperInvariant();
        pos = saved;
        return word;
    }

    char Peek()
    {
        SkipWhitespace();
        return pos < text.Length ? text[pos] : '\0';
    }

    void Expect(char expected)
    {
        SkipWhitespace();
        if (pos >= text.Length || text[pos] != expected)
        {
            throw Error($"expected '{expected}'");
        }

        pos++;
    }

    bool TryConsumeComma()
    {
        SkipWhitespace();
        if (pos < text.Length && text[pos] == ',')
        {
            pos++;
            return true;
        }

        return false;
    }

    void SkipWhitespace()
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }
    }

    GeoConvertException Error(string message) =>
        new($"Invalid WKT at position {pos}: {message}.");
}
