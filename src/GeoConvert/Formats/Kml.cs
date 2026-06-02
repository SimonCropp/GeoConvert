namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://www.ogc.org/standard/kml/">KML 2.2</see>. Each
/// <c>&lt;Placemark&gt;</c> maps to a feature; <c>&lt;name&gt;</c>/<c>&lt;description&gt;</c> and
/// <c>&lt;ExtendedData&gt;</c>/<c>&lt;Data&gt;</c> map to properties. <c>&lt;Folder&gt;</c>s map to
/// nested <see cref="FeatureCollection.Children"/> (a folder's <c>&lt;name&gt;</c> becomes
/// <see cref="FeatureCollection.Name"/>, <c>&lt;description&gt;</c> becomes a "description" property),
/// preserving hierarchy across round-trips. <c>&lt;Document&gt;</c> is treated as the root layer:
/// its name/description populate the root. KML coordinates are lon,lat[,alt] (no measure). Reading
/// streams with <see cref="XmlReader"/> and is namespace-tolerant.
/// </summary>
public static class Kml
{
    const string ns = "http://www.opengis.net/kml/2.2";

    public static FeatureCollection Read(Stream stream) =>
        Read(stream, null);

    internal static FeatureCollection Read(Stream stream, ProgressReporter? progress)
    {
        try
        {
            using var reader = Xml.CreateReader(stream);
            var features = new FeatureCollection();
            reader.MoveToContent();
            // The first <Document> we encounter populates the root; nested Documents become child layers.
            ScanContainer(reader, features, isRoot: true, progress);
            return features;
        }
        catch (GeoConvertException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GeoConvertException($"Invalid KML data: {exception.Message}");
        }
    }

    // Reads the children of an element (<kml>, <Document>, or <Folder>), populating `target` with
    // its placemarks, folders, name and description.
    static void ScanContainer(XmlReader reader, FeatureCollection target, bool isRoot, ProgressReporter? progress) =>
        Xml.ReadChildren(
            reader,
            () =>
            {
                switch (reader.LocalName)
                {
                    case "Placemark":
                        target.Add(ReadPlacemark(reader));
                        progress?.Feature();
                        break;
                    case "Folder":
                        target.Children.Add(ReadContainer(reader, progress));
                        break;
                    case "Document":
                        if (isRoot)
                        {
                            // First Document under <kml> is the root layer itself, not a child.
                            ScanContainer(reader, target, isRoot: false, progress);
                        }
                        else
                        {
                            target.Children.Add(ReadContainer(reader, progress));
                        }

                        break;
                    case "name":
                        target.Name = reader.ReadElementContentAsString();
                        break;
                    case "description":
                        target.Properties["description"] = reader.ReadElementContentAsString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            });

    static FeatureCollection ReadContainer(XmlReader reader, ProgressReporter? progress)
    {
        var features = new FeatureCollection();
        ScanContainer(reader, features, isRoot: false, progress);
        return features;
    }

    static Feature ReadPlacemark(XmlReader reader)
    {
        var feature = new Feature();
        Xml.ReadChildren(
            reader,
            () =>
            {
                switch (reader.LocalName)
                {
                    case "name":
                        feature.Properties["name"] = reader.ReadElementContentAsString();
                        break;
                    case "description":
                        feature.Properties["description"] = reader.ReadElementContentAsString();
                        break;
                    case "ExtendedData":
                        ReadExtendedData(reader, feature);
                        break;
                    case "Point":
                    case "LineString":
                    case "LinearRing":
                    case "Polygon":
                    case "MultiGeometry":
                        var geometry = ReadGeometry(reader);
                        // first geometry wins
                        feature.Geometry ??= geometry;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            });

        return feature;
    }

    static void ReadExtendedData(XmlReader reader, Feature feature) =>
        Xml.ReadChildren(reader, () =>
        {
            if (reader.LocalName != "Data")
            {
                reader.Skip();
                return;
            }

            var key = reader.GetAttribute("name");
            string? value = null;
            Xml.ReadChildren(reader, () =>
            {
                if (reader.LocalName == "value")
                {
                    value = reader.ReadElementContentAsString();
                }
                else
                {
                    reader.Skip();
                }
            });

            if (key != null)
            {
                feature.Properties[key] = value;
            }
        });

    static Geometry ReadGeometry(XmlReader reader) =>
        reader.LocalName switch
        {
            "Point" => ReadPoint(reader),
            "LineString" or "LinearRing" => new LineString(ReadCoordinatesElement(reader)),
            "Polygon" => ReadPolygon(reader),
            _ => ReadMultiGeometry(reader), // MultiGeometry
        };

    static Point ReadPoint(XmlReader reader)
    {
        var positions = ReadCoordinatesElement(reader);
        return new(positions.Count > 0 ? positions[0] : new(double.NaN, double.NaN));
    }

    static Polygon ReadPolygon(XmlReader reader)
    {
        var rings = new List<IReadOnlyList<Position>>();
        Xml.ReadChildren(reader, () =>
        {
            if (reader.LocalName is "outerBoundaryIs" or "innerBoundaryIs")
            {
                rings.Add(ReadBoundary(reader));
            }
            else
            {
                reader.Skip();
            }
        });

        return new(rings);
    }

    static List<Position> ReadBoundary(XmlReader reader)
    {
        var positions = new List<Position>();
        Xml.ReadChildren(reader, () =>
        {
            if (reader.LocalName == "LinearRing")
            {
                positions = ReadCoordinatesElement(reader);
            }
            else
            {
                reader.Skip();
            }
        });

        return positions;
    }

    static Geometry ReadMultiGeometry(XmlReader reader)
    {
        var children = new List<Geometry>();
        Xml.ReadChildren(reader, () =>
        {
            switch (reader.LocalName)
            {
                case "Point":
                case "LineString":
                case "LinearRing":
                case "Polygon":
                case "MultiGeometry":
                    children.Add(ReadGeometry(reader));
                    break;
                default:
                    reader.Skip();
                    break;
            }
        });

        if (children.Count > 0 && children.All(_ => _ is Point))
        {
            return new MultiPoint([.. children.Cast<Point>().Select(_ => _.Coordinate)]);
        }

        if (children.Count > 0 && children.All(_ => _ is LineString))
        {
            return new MultiLineString([.. children.Cast<LineString>()]);
        }

        if (children.Count > 0 && children.All(_ => _ is Polygon))
        {
            return new MultiPolygon([.. children.Cast<Polygon>()]);
        }

        return new GeometryCollection(children);
    }

    static List<Position> ReadCoordinatesElement(XmlReader reader)
    {
        var positions = new List<Position>();
        Xml.ReadChildren(reader, () =>
        {
            if (reader.LocalName == "coordinates")
            {
                ParseCoordinates(reader.ReadElementContentAsString(), positions);
            }
            else
            {
                reader.Skip();
            }
        });

        return positions;
    }

    static void ParseCoordinates(string text, List<Position> positions)
    {
        foreach (var tuple in text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = tuple.Split(',');
            if (parts.Length < 2)
            {
                throw new GeoConvertException(
                    $"KML coordinate tuple needs at least lon,lat: '{tuple}'.");
            }

            var x = ParseOrdinate(parts[0], tuple);
            var y = ParseOrdinate(parts[1], tuple);
            double? z = parts.Length > 2 ? ParseOrdinate(parts[2], tuple) : null;
            positions.Add(new(x, y, z));
        }
    }

    static double ParseOrdinate(string text, string tuple)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new GeoConvertException($"KML coordinate is not a valid number: '{text}' in '{tuple}'.");
        }

        return value;
    }

    public static void Write(Stream stream, FeatureCollection collection) =>
        Write(stream, collection, null);

    internal static void Write(Stream stream, FeatureCollection collection, ProgressReporter? progress)
    {
        using var writer = Xml.CreateWriter(stream);
        writer.WriteStartDocument();
        writer.WriteStartElement("kml", ns);
        writer.WriteStartElement("Document", ns);
        WriteContainerBody(writer, collection, progress);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    static void WriteContainerBody(XmlWriter writer, FeatureCollection collection, ProgressReporter? progress)
    {
        if (collection.Name is { } name)
        {
            writer.WriteElementString("name", ns, name);
        }

        if (collection.Properties.TryGetValue("description", out var description) && description != null)
        {
            writer.WriteElementString("description", ns, Scalars.Format(description));
        }

        foreach (var feature in collection.Features)
        {
            WritePlacemark(writer, feature);
            progress?.Feature();
        }

        foreach (var child in collection.Children)
        {
            writer.WriteStartElement("Folder", ns);
            WriteContainerBody(writer, child, progress);
            writer.WriteEndElement();
        }
    }

    static void WritePlacemark(XmlWriter writer, Feature feature)
    {
        writer.WriteStartElement("Placemark", ns);

        var hasExtras = false;
        foreach (var property in feature.Properties)
        {
            switch (property.Key)
            {
                case "name":
                    writer.WriteElementString("name", ns, Scalars.Format(property.Value));
                    break;
                case "description":
                    writer.WriteElementString("description", ns, Scalars.Format(property.Value));
                    break;
                default:
                    hasExtras = true;
                    break;
            }
        }

        if (hasExtras)
        {
            writer.WriteStartElement("ExtendedData", ns);
            foreach (var property in feature.Properties)
            {
                if (property.Key is "name" or "description")
                {
                    continue;
                }

                writer.WriteStartElement("Data", ns);
                writer.WriteAttributeString("name", property.Key);
                writer.WriteElementString("value", ns, Scalars.Format(property.Value));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        if (feature.Geometry is { } geometry)
        {
            WriteGeometry(writer, geometry);
        }

        writer.WriteEndElement();
    }

    static void WriteGeometry(XmlWriter writer, Geometry geometry)
    {
        switch (geometry)
        {
            case Point point:
                WriteCoordinatesElement(writer, "Point", [point.Coordinate]);
                break;
            case LineString line:
                WriteCoordinatesElement(writer, "LineString", line.Positions);
                break;
            case Polygon polygon:
                WritePolygon(writer, polygon);
                break;
            case MultiPoint multiPoint:
                writer.WriteStartElement("MultiGeometry", ns);
                foreach (var position in multiPoint.Positions)
                {
                    WriteCoordinatesElement(writer, "Point", [position]);
                }

                writer.WriteEndElement();
                break;
            case MultiLineString multiLine:
                writer.WriteStartElement("MultiGeometry", ns);
                foreach (var line in multiLine.LineStrings)
                {
                    WriteGeometry(writer, line);
                }

                writer.WriteEndElement();
                break;
            case MultiPolygon multiPolygon:
                writer.WriteStartElement("MultiGeometry", ns);
                foreach (var polygon in multiPolygon.Polygons)
                {
                    WriteGeometry(writer, polygon);
                }

                writer.WriteEndElement();
                break;
            case GeometryCollection collection:
                writer.WriteStartElement("MultiGeometry", ns);
                foreach (var child in collection.Geometries)
                {
                    WriteGeometry(writer, child);
                }

                writer.WriteEndElement();
                break;
            default:
                throw new GeoConvertException($"Cannot write {geometry.Type} as KML.");
        }
    }

    static void WritePolygon(XmlWriter writer, Polygon polygon)
    {
        writer.WriteStartElement("Polygon", ns);
        if (polygon.ExteriorRing is { } exterior)
        {
            writer.WriteStartElement("outerBoundaryIs", ns);
            WriteCoordinatesElement(writer, "LinearRing", exterior);
            writer.WriteEndElement();
        }

        foreach (var hole in polygon.InteriorRings)
        {
            writer.WriteStartElement("innerBoundaryIs", ns);
            WriteCoordinatesElement(writer, "LinearRing", hole);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    static void WriteCoordinatesElement(XmlWriter writer, string element, IReadOnlyList<Position> positions)
    {
        writer.WriteStartElement(element, ns);

        var builder = new StringBuilder();
        for (var i = 0; i < positions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            var position = positions[i];
            builder.Append(position.X.ToString("R", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(position.Y.ToString("R", CultureInfo.InvariantCulture));

            if (position.Z is not { } z)
            {
                continue;
            }

            builder.Append(',');
            builder.Append(z.ToString("R", CultureInfo.InvariantCulture));
        }

        writer.WriteElementString("coordinates", ns, builder.ToString());
        writer.WriteEndElement();
    }
}
