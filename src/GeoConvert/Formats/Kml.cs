using System.Xml;
using System.Xml.Linq;

namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://www.ogc.org/standard/kml/">KML 2.2</see>. Each
/// <c>&lt;Placemark&gt;</c> maps to a feature; <c>&lt;name&gt;</c>/<c>&lt;description&gt;</c> and
/// <c>&lt;ExtendedData&gt;</c>/<c>&lt;Data&gt;</c> map to properties. KML coordinates are lon,lat[,alt]
/// (no measure). Reading is namespace-tolerant.
/// </summary>
public static class Kml
{
    static readonly XNamespace ns = "http://www.opengis.net/kml/2.2";

    public static FeatureCollection Read(Stream stream)
    {
        var document = XDocument.Load(stream);
        var collection = new FeatureCollection();
        foreach (var placemark in document.DescendantsLocal("Placemark"))
        {
            collection.Add(ReadPlacemark(placemark));
        }

        return collection;
    }

    static Feature ReadPlacemark(XElement placemark)
    {
        var feature = new Feature();

        if (placemark.ValueLocal("name") is { } name)
        {
            feature.Properties["name"] = name;
        }

        if (placemark.ValueLocal("description") is { } description)
        {
            feature.Properties["description"] = description;
        }

        if (placemark.ElementLocal("ExtendedData") is { } extended)
        {
            foreach (var data in extended.ElementsLocal("Data"))
            {
                if (data.Attribute("name")?.Value is { } key)
                {
                    feature.Properties[key] = data.ValueLocal("value");
                }
            }
        }

        feature.Geometry = ReadGeometry(placemark);
        return feature;
    }

    static Geometry? ReadGeometry(XContainer container)
    {
        foreach (var element in container.Elements())
        {
            if (ReadGeometryElement(element) is { } geometry)
            {
                return geometry;
            }
        }

        return null;
    }

    static Geometry? ReadGeometryElement(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "Point":
                var points = ParseCoordinates(element);
                return new Point(points.Count > 0 ? points[0] : new(double.NaN, double.NaN));
            case "LineString":
            case "LinearRing":
                return new LineString(ParseCoordinates(element));
            case "Polygon":
                return ReadPolygon(element);
            case "MultiGeometry":
                return ReadMultiGeometry(element);
            default:
                return null;
        }
    }

    static Polygon ReadPolygon(XElement element)
    {
        var rings = new List<IReadOnlyList<Position>>();
        if (element.ElementLocal("outerBoundaryIs")?.ElementLocal("LinearRing") is { } outer)
        {
            rings.Add(ParseCoordinates(outer));
        }

        foreach (var inner in element.ElementsLocal("innerBoundaryIs"))
        {
            if (inner.ElementLocal("LinearRing") is { } ring)
            {
                rings.Add(ParseCoordinates(ring));
            }
        }

        return new(rings);
    }

    static Geometry ReadMultiGeometry(XElement element)
    {
        var children = new List<Geometry>();
        foreach (var child in element.Elements())
        {
            if (ReadGeometryElement(child) is { } geometry)
            {
                children.Add(geometry);
            }
        }

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

    static List<Position> ParseCoordinates(XElement geometryElement)
    {
        var coordinates = geometryElement.ValueLocal("coordinates");
        if (coordinates == null)
        {
            return [];
        }

        var positions = new List<Position>();
        foreach (var tuple in coordinates.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = tuple.Split(',');
            var x = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var y = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double? z = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : null;
            positions.Add(new(x, y, z));
        }

        return positions;
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        var document = BuildDocument(collection);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
        };
        using var writer = XmlWriter.Create(stream, settings);
        document.Save(writer);
    }

    internal static XDocument BuildDocument(FeatureCollection collection)
    {
        var document = new XElement(ns + "Document");
        foreach (var feature in collection)
        {
            document.Add(WritePlacemark(feature));
        }

        return new(
            new("1.0", "UTF-8", null),
            new XElement(ns + "kml", document));
    }

    static XElement WritePlacemark(Feature feature)
    {
        var placemark = new XElement(ns + "Placemark");
        var extras = new List<KeyValuePair<string, object?>>();
        foreach (var property in feature.Properties)
        {
            switch (property.Key)
            {
                case "name":
                    placemark.Add(new XElement(ns + "name", Scalars.Format(property.Value)));
                    break;
                case "description":
                    placemark.Add(new XElement(ns + "description", Scalars.Format(property.Value)));
                    break;
                default:
                    extras.Add(property);
                    break;
            }
        }

        if (extras.Count > 0)
        {
            var extended = new XElement(ns + "ExtendedData");
            foreach (var extra in extras)
            {
                extended.Add(
                    new XElement(
                        ns + "Data",
                        new XAttribute("name", extra.Key),
                        new XElement(ns + "value", Scalars.Format(extra.Value))));
            }

            placemark.Add(extended);
        }

        if (feature.Geometry is { } geometry)
        {
            placemark.Add(WriteGeometry(geometry));
        }

        return placemark;
    }

    static XElement WriteGeometry(Geometry geometry) =>
        geometry switch
        {
            Point point => new(ns + "Point", Coordinates([point.Coordinate])),
            LineString line => new(ns + "LineString", Coordinates(line.Positions)),
            Polygon polygon => WritePolygon(polygon),
            MultiPoint multiPoint => new(
                ns + "MultiGeometry",
                multiPoint.Positions.Select(_ => new XElement(ns + "Point", Coordinates([_])))),
            MultiLineString multiLine => new(
                ns + "MultiGeometry",
                multiLine.LineStrings.Select(WriteGeometry)),
            MultiPolygon multiPolygon => new(
                ns + "MultiGeometry",
                multiPolygon.Polygons.Select(WriteGeometry)),
            GeometryCollection collection => new(
                ns + "MultiGeometry",
                collection.Geometries.Select(WriteGeometry)),
            _ => throw new GeoConvertException($"Cannot write {geometry.Type} as KML."),
        };

    static XElement WritePolygon(Polygon polygon)
    {
        var element = new XElement(ns + "Polygon");
        if (polygon.ExteriorRing is { } exterior)
        {
            element.Add(new XElement(ns + "outerBoundaryIs", new XElement(ns + "LinearRing", Coordinates(exterior))));
        }

        foreach (var hole in polygon.InteriorRings)
        {
            element.Add(new XElement(ns + "innerBoundaryIs", new XElement(ns + "LinearRing", Coordinates(hole))));
        }

        return element;
    }

    static XElement Coordinates(IReadOnlyList<Position> positions)
    {
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
            if (position.Z is { } z)
            {
                builder.Append(',');
                builder.Append(z.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return new(ns + "coordinates", builder.ToString());
    }
}
