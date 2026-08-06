using System.Collections.Concurrent;
using Avalonia.Media;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Deterministic muted color per GDS (layer, datatype) for imported outline and
/// frozen-path geometry: photonics layer classes get fixed, recognizable hues (the
/// waveguide core keeps the historical outline blue, metals run amber, labels teal),
/// everything else falls back to a hash-spread hue at the same muted saturation so
/// any process's layers stay distinguishable. All brushes/pens are cached — the
/// render loop must never allocate per frame (see <see cref="ComponentOutlineRenderer"/>).
/// </summary>
internal static class OutlineLayerPalette
{
    /// <summary>Fill alpha of outline polygons (the historical convention).</summary>
    private const byte FillAlpha = 46;

    /// <summary>Outline-stroke alpha of outline polygons (the historical convention).</summary>
    private const byte OutlineAlpha = 160;

    /// <summary>Stroke alpha of tagged frozen paths (matches the default frozen-path pen).</summary>
    private const byte PathAlpha = 200;

    /// <summary>Stroke thickness of tagged frozen paths (matches the default frozen-path pen).</summary>
    private const double PathThickness = 2.0;

    private static readonly ConcurrentDictionary<(int Layer, int DataType), (IBrush Fill, Pen Outline)> OutlineStyles = new();
    private static readonly ConcurrentDictionary<(int Layer, int DataType), Pen> PathPens = new();

    /// <summary>
    /// The muted RGB color of a (layer, datatype) pair. Pure and deterministic —
    /// the unit-test seam for the per-layer styling (brushes themselves need no
    /// render platform, but the color decision is what matters).
    /// </summary>
    internal static Color ColorFor(int layer, int dataType) => (layer, dataType) switch
    {
        // Waveguide core — the historical outline blue, so the most common layer
        // keeps the exact v1 look.
        (1, 0) => Color.FromRgb(100, 160, 220),
        // Extent / bbox documentation — neutral gray.
        (111, 0) => Color.FromRgb(150, 150, 160),
        // Metal layers — ambers, clearly distinct from any optical layer.
        (11, 0) => Color.FromRgb(210, 160, 70),
        (12, 0) => Color.FromRgb(190, 130, 60),
        // Port-label / text layers — teal.
        (1, 10) => Color.FromRgb(90, 190, 170),
        _ => HashedColor(layer, dataType),
    };

    /// <summary>
    /// Fill brush and outline pen for an outline polygon on (layer, datatype),
    /// cached per pair. Alphas follow the historical single-style conventions.
    /// </summary>
    internal static (IBrush Fill, Pen Outline) OutlineStyleFor(int layer, int dataType) =>
        OutlineStyles.GetOrAdd((layer, dataType), key =>
        {
            var color = ColorFor(key.Layer, key.DataType);
            return (new SolidColorBrush(Color.FromArgb(FillAlpha, color.R, color.G, color.B)),
                new Pen(new SolidColorBrush(Color.FromArgb(OutlineAlpha, color.R, color.G, color.B)), 1));
        });

    /// <summary>
    /// Stroke pen for a frozen path tagged with (layer, datatype), cached per pair —
    /// same alpha/thickness as the untagged default frozen-path pen, so only the hue
    /// carries the layer information.
    /// </summary>
    internal static Pen PathPenFor(int layer, int dataType) =>
        PathPens.GetOrAdd((layer, dataType), key =>
        {
            var color = ColorFor(key.Layer, key.DataType);
            return new Pen(new SolidColorBrush(Color.FromArgb(PathAlpha, color.R, color.G, color.B)), PathThickness);
        });

    /// <summary>
    /// Fallback for layers without a fixed palette entry: a hash-spread hue at muted
    /// saturation/value. Deterministic across runs (string.GetHashCode is not, hence
    /// the explicit Knuth multiplicative hash).
    /// </summary>
    private static Color HashedColor(int layer, int dataType)
    {
        var hash = (((uint)layer * 31u) + (uint)dataType) * 2654435761u;
        double hue = (hash >> 8) % 360;
        return FromHsv(hue, saturation: 0.45, value: 0.80);
    }

    /// <summary>HSV→RGB (h in [0,360), s/v in [0,1]) for the muted hash fallback.</summary>
    private static Color FromHsv(double h, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
        var m = value - c;
        var (r, g, b) = ((int)(h / 60.0) % 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
