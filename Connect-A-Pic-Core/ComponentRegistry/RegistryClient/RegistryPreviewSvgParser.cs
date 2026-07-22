using System.Globalization;
using System.Xml.Linq;

namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>
/// Parses the registry's geometry preview SVGs (background rect + polygons,
/// see <see cref="RegistryPreviewSvg"/>) with plain XML tooling. Tolerant by
/// design: it never throws — any malformed document yields null so callers
/// fall back to the placeholder pictogram without error spam.
/// </summary>
public static class RegistryPreviewSvgParser
{
    private const int ViewBoxValueCount = 4;
    private const double DefaultFillOpacity = 1.0;

    /// <summary>
    /// Parses SVG text into a preview document. Returns null when the text is
    /// not an SVG, has no usable viewBox, or contains no valid polygons.
    /// </summary>
    public static RegistryPreviewSvg? TryParse(string? svgText)
    {
        if (string.IsNullOrWhiteSpace(svgText))
            return null;
        try
        {
            return Parse(XDocument.Parse(svgText).Root);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static RegistryPreviewSvg? Parse(XElement? root)
    {
        if (root is null || root.Name.LocalName != "svg")
            return null;
        if (TryParseViewBox(root.Attribute("viewBox")?.Value) is not { } viewBox)
            return null;

        var polygons = root.Elements()
            .Where(e => e.Name.LocalName == "polygon")
            .Select(TryParsePolygon)
            .OfType<RegistryPreviewPolygon>()
            .ToList();
        if (polygons.Count == 0)
            return null;

        var background = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "rect")?.Attribute("fill")?.Value;
        return new RegistryPreviewSvg(
            viewBox.MinX, viewBox.MinY, viewBox.Width, viewBox.Height, background, polygons);
    }

    private static (double MinX, double MinY, double Width, double Height)? TryParseViewBox(
        string? viewBox)
    {
        var values = SplitNumbers(viewBox);
        if (values is null || values.Count != ViewBoxValueCount)
            return null;
        if (values[2] <= 0 || values[3] <= 0)
            return null;
        return (values[0], values[1], values[2], values[3]);
    }

    private static RegistryPreviewPolygon? TryParsePolygon(XElement polygon)
    {
        var values = SplitNumbers(polygon.Attribute("points")?.Value);
        if (values is null || values.Count < 6 || values.Count % 2 != 0)
            return null; // Fewer than 3 vertices is not a drawable polygon.

        var points = new List<(double X, double Y)>(values.Count / 2);
        for (var i = 0; i < values.Count; i += 2)
            points.Add((values[i], values[i + 1]));

        var fill = polygon.Attribute("fill")?.Value;
        if (string.IsNullOrWhiteSpace(fill) || fill == "none")
            return null;
        return new RegistryPreviewPolygon(points, fill, ParseOpacity(polygon));
    }

    private static double ParseOpacity(XElement polygon)
    {
        var raw = polygon.Attribute("fill-opacity")?.Value;
        if (raw is not null
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
            return Math.Clamp(opacity, 0.0, 1.0);
        return DefaultFillOpacity;
    }

    /// <summary>
    /// Splits a whitespace/comma separated coordinate list into doubles
    /// (invariant culture). Returns null when any token is not a number.
    /// </summary>
    private static List<double>? SplitNumbers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var tokens = raw.Split(new[] { ' ', '\t', '\n', '\r', ',' },
            StringSplitOptions.RemoveEmptyEntries);
        var values = new List<double>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;
            values.Add(value);
        }
        return values;
    }
}
