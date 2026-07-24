namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>
/// Parsed geometry preview of a registry component. The registry's
/// <c>render_previews.py</c> emits deliberately simple SVGs — a background
/// rect plus one <c>&lt;polygon&gt;</c> per GDS polygon — so this model (and
/// <see cref="RegistryPreviewSvgParser"/>) covers exactly that dialect and
/// keeps Lunima free of a full SVG rendering dependency.
/// </summary>
public class RegistryPreviewSvg
{
    /// <summary>Creates a parsed preview document.</summary>
    public RegistryPreviewSvg(
        double viewBoxMinX, double viewBoxMinY, double viewBoxWidth, double viewBoxHeight,
        string? backgroundColor, IReadOnlyList<RegistryPreviewPolygon> polygons)
    {
        ViewBoxMinX = viewBoxMinX;
        ViewBoxMinY = viewBoxMinY;
        ViewBoxWidth = viewBoxWidth;
        ViewBoxHeight = viewBoxHeight;
        BackgroundColor = backgroundColor;
        Polygons = polygons;
    }

    /// <summary>Left edge of the SVG viewBox (µm space).</summary>
    public double ViewBoxMinX { get; }

    /// <summary>Top edge of the SVG viewBox (µm space, y already screen-down).</summary>
    public double ViewBoxMinY { get; }

    /// <summary>Width of the SVG viewBox.</summary>
    public double ViewBoxWidth { get; }

    /// <summary>Height of the SVG viewBox.</summary>
    public double ViewBoxHeight { get; }

    /// <summary>Background fill as a hex color (e.g. <c>#12161d</c>), or null when absent.</summary>
    public string? BackgroundColor { get; }

    /// <summary>All filled polygons in document order (GDS layer shapes).</summary>
    public IReadOnlyList<RegistryPreviewPolygon> Polygons { get; }
}

/// <summary>One filled polygon of a registry geometry preview.</summary>
public class RegistryPreviewPolygon
{
    /// <summary>Creates a polygon with its style.</summary>
    public RegistryPreviewPolygon(
        IReadOnlyList<(double X, double Y)> points, string fillColor, double fillOpacity)
    {
        Points = points;
        FillColor = fillColor;
        FillOpacity = fillOpacity;
    }

    /// <summary>Closed outline vertices in viewBox coordinates.</summary>
    public IReadOnlyList<(double X, double Y)> Points { get; }

    /// <summary>Fill as a hex color (e.g. <c>#38bdf8</c>).</summary>
    public string FillColor { get; }

    /// <summary>Fill opacity in [0, 1].</summary>
    public double FillOpacity { get; }
}
