namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://www.topografix.com/gpx.asp">GPX 1.1</see>. Waypoints
/// (<c>wpt</c>) map to points, routes (<c>rte</c>) and single-segment tracks (<c>trk</c>) to line
/// strings, and multi-segment tracks to multi line strings. GPX cannot represent polygons or geometry
/// collections, so writing those throws. Coordinates are lon/lat with optional elevation (Z). Reading
/// streams with <see cref="XmlReader"/>.
/// </summary>
public static class Gpx
{
    const string ns = "http://www.topografix.com/GPX/1/1";

    public static FeatureCollection Read(Stream stream)
    {
        using var reader = Xml.CreateReader(stream);
        var collection = new FeatureCollection();
        reader.MoveToContent();
        Xml.ReadChildren(reader, () =>
        {
            switch (reader.LocalName)
            {
                case "wpt":
                    collection.Add(ReadWaypointFeature(reader));
                    break;
                case "rte":
                    collection.Add(ReadRoute(reader));
                    break;
                case "trk":
                    collection.Add(ReadTrack(reader));
                    break;
                default:
                    reader.Skip();
                    break;
            }
        });

        return collection;
    }

    static Feature ReadWaypointFeature(XmlReader reader)
    {
        var feature = new Feature();
        feature.Geometry = new Point(ReadWaypoint(reader, feature));
        return feature;
    }

    static Feature ReadRoute(XmlReader reader)
    {
        var feature = new Feature();
        var positions = new List<Position>();
        Xml.ReadChildren(reader, () =>
        {
            switch (reader.LocalName)
            {
                case "rtept":
                    positions.Add(ReadWaypoint(reader, null));
                    break;
                case "name":
                    feature.Properties["name"] = reader.ReadElementContentAsString();
                    break;
                case "desc":
                    feature.Properties["description"] = reader.ReadElementContentAsString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        });

        feature.Geometry = new LineString(positions);
        return feature;
    }

    static Feature ReadTrack(XmlReader reader)
    {
        var feature = new Feature();
        var segments = new List<LineString>();
        Xml.ReadChildren(reader, () =>
        {
            switch (reader.LocalName)
            {
                case "trkseg":
                    segments.Add(ReadSegment(reader));
                    break;
                case "name":
                    feature.Properties["name"] = reader.ReadElementContentAsString();
                    break;
                case "desc":
                    feature.Properties["description"] = reader.ReadElementContentAsString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        });

        feature.Geometry = segments.Count == 1 ? segments[0] : new MultiLineString(segments);
        return feature;
    }

    static LineString ReadSegment(XmlReader reader)
    {
        var positions = new List<Position>();
        Xml.ReadChildren(reader, () =>
        {
            if (reader.LocalName == "trkpt")
            {
                positions.Add(ReadWaypoint(reader, null));
            }
            else
            {
                reader.Skip();
            }
        });

        return new(positions);
    }

    // Reads lat/lon (and optional ele) from a wpt/rtept/trkpt; populates name/desc into metadata when given.
    static Position ReadWaypoint(XmlReader reader, Feature? metadata)
    {
        var lat = double.Parse(reader.GetAttribute("lat")!, CultureInfo.InvariantCulture);
        var lon = double.Parse(reader.GetAttribute("lon")!, CultureInfo.InvariantCulture);
        double? ele = null;

        Xml.ReadChildren(reader, () =>
        {
            switch (reader.LocalName)
            {
                case "ele":
                    ele = double.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture);
                    break;
                case "name" when metadata != null:
                    metadata.Properties["name"] = reader.ReadElementContentAsString();
                    break;
                case "desc" when metadata != null:
                    metadata.Properties["description"] = reader.ReadElementContentAsString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        });

        return new(lon, lat, ele);
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        using var writer = Xml.CreateWriter(stream);
        writer.WriteStartDocument();
        writer.WriteStartElement("gpx", ns);
        writer.WriteAttributeString("version", "1.1");
        writer.WriteAttributeString("creator", "GeoConvert");
        foreach (var feature in collection)
        {
            WriteFeature(writer, feature);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    static void WriteFeature(XmlWriter writer, Feature feature)
    {
        switch (feature.Geometry)
        {
            case null:
                break;
            case Point point:
                WriteWaypoint(writer, "wpt", point.Coordinate, feature);
                break;
            case MultiPoint multiPoint:
                foreach (var position in multiPoint.Positions)
                {
                    WriteWaypoint(writer, "wpt", position, feature);
                }

                break;
            case LineString line:
                WriteTrack(writer, feature, [line.Positions]);
                break;
            case MultiLineString multiLine:
                WriteTrack(writer, feature, multiLine.LineStrings.Select(_ => _.Positions));
                break;
            default:
                throw new GeoConvertException($"GPX cannot represent {feature.Geometry.Type}.");
        }
    }

    static void WriteTrack(XmlWriter writer, Feature feature, IEnumerable<IReadOnlyList<Position>> segments)
    {
        writer.WriteStartElement("trk", ns);
        WriteMetadata(writer, feature);
        foreach (var segment in segments)
        {
            writer.WriteStartElement("trkseg", ns);
            foreach (var position in segment)
            {
                WriteWaypoint(writer, "trkpt", position, null);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    static void WriteWaypoint(XmlWriter writer, string element, Position position, Feature? feature)
    {
        writer.WriteStartElement(element, ns);
        writer.WriteAttributeString("lat", position.Y.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteAttributeString("lon", position.X.ToString("R", CultureInfo.InvariantCulture));
        if (position.Z is { } z)
        {
            writer.WriteElementString("ele", ns, z.ToString("R", CultureInfo.InvariantCulture));
        }

        if (feature != null)
        {
            WriteMetadata(writer, feature);
        }

        writer.WriteEndElement();
    }

    static void WriteMetadata(XmlWriter writer, Feature feature)
    {
        if (feature.Properties.TryGetValue("name", out var name) && name != null)
        {
            writer.WriteElementString("name", ns, Scalars.Format(name));
        }

        if (feature.Properties.TryGetValue("description", out var description) && description != null)
        {
            writer.WriteElementString("desc", ns, Scalars.Format(description));
        }
    }
}
