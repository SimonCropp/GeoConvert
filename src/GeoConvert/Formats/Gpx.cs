namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://www.topografix.com/gpx.asp">GPX 1.1</see>. Waypoints
/// (<c>wpt</c>) map to points, routes (<c>rte</c>) and single-segment tracks (<c>trk</c>) to line
/// strings, and multi-segment tracks to multi line strings. On read, each category becomes a named
/// child layer (<c>"waypoints"</c>, <c>"routes"</c>, <c>"tracks"</c>) so the wpt/rte/trk distinction
/// — which type alone can't carry — round-trips. On write, those same layer names route features back
/// to their original element; a flat input is dispatched purely by geometry type (LineString → trk).
/// GPX has no native area type, so writing a polygon emits a track with one segment per ring
/// (exterior then holes), a multi polygon flattens every ring of every member into a single track,
/// and a geometry collection writes each member geometry in turn. Coordinates are lon/lat with
/// optional elevation (Z). Reading streams with <see cref="XmlReader"/>.
/// </summary>
public static class Gpx
{
    const string ns = "http://www.topografix.com/GPX/1/1";

    public static FeatureCollection Read(Stream stream)
    {
        using var reader = Xml.CreateReader(stream);
        var collection = new FeatureCollection();
        var waypoints = new FeatureCollection { Name = "waypoints" };
        var routes = new FeatureCollection { Name = "routes" };
        var tracks = new FeatureCollection { Name = "tracks" };

        reader.MoveToContent();
        Xml.ReadChildren(reader, () =>
        {
            switch (reader.LocalName)
            {
                case "wpt":
                    waypoints.Add(ReadWaypointFeature(reader));
                    break;
                case "rte":
                    routes.Add(ReadRoute(reader));
                    break;
                case "trk":
                    tracks.Add(ReadTrack(reader));
                    break;
                default:
                    reader.Skip();
                    break;
            }
        });

        // Only attach the buckets that have content, so a single-category document stays uncluttered.
        if (waypoints.Features.Count > 0)
        {
            collection.Children.Add(waypoints);
        }

        if (routes.Features.Count > 0)
        {
            collection.Children.Add(routes);
        }

        if (tracks.Features.Count > 0)
        {
            collection.Children.Add(tracks);
        }

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

        // GPX's element ordering is wpt → rte → trk; emit category children in that order, and any
        // root-level features or non-category children fall back to type-based dispatch (no category hint).
        var hasCategoryChild = collection.Children.Any(IsCategoryLayer);
        if (hasCategoryChild)
        {
            foreach (var feature in collection.Features)
            {
                WriteFeature(writer, feature, category: null);
            }

            WriteCategoryChildren(writer, collection, "waypoints");
            WriteCategoryChildren(writer, collection, "routes");
            WriteCategoryChildren(writer, collection, "tracks");

            foreach (var child in collection.Children)
            {
                if (IsCategoryLayer(child))
                {
                    continue;
                }

                foreach (var feature in child)
                {
                    WriteFeature(writer, feature, category: null);
                }
            }
        }
        else
        {
            foreach (var feature in collection)
            {
                WriteFeature(writer, feature, category: null);
            }
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    static bool IsCategoryLayer(FeatureCollection layer) =>
        layer.Name is "waypoints" or "routes" or "tracks";

    static void WriteCategoryChildren(XmlWriter writer, FeatureCollection root, string category)
    {
        foreach (var child in root.Children)
        {
            if (child.Name != category)
            {
                continue;
            }

            foreach (var feature in child)
            {
                WriteFeature(writer, feature, category);
            }
        }
    }

    static void WriteFeature(XmlWriter writer, Feature feature, string? category) =>
        WriteGeometry(writer, feature.Geometry, feature, category);

    // Writes one geometry, carrying the owning feature so metadata (name/desc) is attached. A geometry
    // collection recurses, writing each member with the same feature metadata. The optional category
    // — set by category-named child layers on the way in — flips LineStrings between rte and trk.
    static void WriteGeometry(XmlWriter writer, Geometry? geometry, Feature feature, string? category)
    {
        switch (geometry)
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
            case LineString line when category == "routes":
                WriteRoute(writer, feature, line.Positions);
                break;
            case LineString line:
                WriteTrack(writer, feature, [line.Positions]);
                break;
            case MultiLineString multiLine:
                WriteTrack(writer, feature, multiLine.LineStrings.Select(_ => _.Positions));
                break;
            case Polygon polygon:
                WriteTrack(writer, feature, polygon.Rings);
                break;
            case MultiPolygon multiPolygon:
                WriteTrack(writer, feature, multiPolygon.Polygons.SelectMany(_ => _.Rings));
                break;
            case GeometryCollection collection:
                foreach (var member in collection.Geometries)
                {
                    WriteGeometry(writer, member, feature, category);
                }

                break;
            default:
                throw new GeoConvertException($"GPX cannot represent {geometry.Type}.");
        }
    }

    static void WriteRoute(XmlWriter writer, Feature feature, IReadOnlyList<Position> positions)
    {
        writer.WriteStartElement("rte", ns);
        WriteMetadata(writer, feature);
        foreach (var position in positions)
        {
            WriteWaypoint(writer, "rtept", position, null);
        }

        writer.WriteEndElement();
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
