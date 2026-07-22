using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CAP_Core.ComponentRegistry.RegistryClient;

namespace CAP.Avalonia.Controls;

/// <summary>
/// Draws a registry geometry preview (issue #771) from its SVG text — the
/// simple rect-plus-polygons dialect emitted by the registry's
/// <c>render_previews.py</c> — as native Avalonia geometry, so no SVG
/// rendering package is needed. Colors and background come from the SVG;
/// unparseable input renders nothing (callers keep a placeholder visible).
/// </summary>
public class RegistryPreviewControl : Control
{
    /// <summary>Raw SVG text of the preview; empty/invalid renders nothing.</summary>
    public static readonly StyledProperty<string?> SvgProperty =
        AvaloniaProperty.Register<RegistryPreviewControl, string?>(nameof(Svg));

    private RegistryPreviewSvg? _document;
    private List<(StreamGeometry Geometry, IBrush Brush)>? _shapes;

    static RegistryPreviewControl()
    {
        AffectsRender<RegistryPreviewControl>(SvgProperty);
    }

    /// <summary>Raw SVG text of the preview; empty/invalid renders nothing.</summary>
    public string? Svg
    {
        get => GetValue(SvgProperty);
        set => SetValue(SvgProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SvgProperty)
            return;
        _document = RegistryPreviewSvgParser.TryParse(change.GetNewValue<string?>());
        _shapes = _document is null ? null : BuildShapes(_document);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (_document is null || _shapes is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        if (TryParseColor(_document.BackgroundColor) is { } background)
            context.FillRectangle(new SolidColorBrush(background), new Rect(Bounds.Size));

        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushTransform(BuildViewBoxToControlMatrix(_document, Bounds.Size)))
        {
            foreach (var (geometry, brush) in _shapes)
                context.DrawGeometry(brush, null, geometry);
        }
    }

    /// <summary>
    /// Uniform fit of the SVG viewBox into the control, centered on both axes
    /// (letterboxed — never distorts the component's aspect ratio).
    /// </summary>
    internal static Matrix BuildViewBoxToControlMatrix(RegistryPreviewSvg document, Size target)
    {
        var scale = Math.Min(
            target.Width / document.ViewBoxWidth, target.Height / document.ViewBoxHeight);
        var offsetX = (target.Width - document.ViewBoxWidth * scale) / 2.0;
        var offsetY = (target.Height - document.ViewBoxHeight * scale) / 2.0;
        return Matrix.CreateTranslation(-document.ViewBoxMinX, -document.ViewBoxMinY)
             * Matrix.CreateScale(scale, scale)
             * Matrix.CreateTranslation(offsetX, offsetY);
    }

    private static List<(StreamGeometry, IBrush)> BuildShapes(RegistryPreviewSvg document)
    {
        var shapes = new List<(StreamGeometry, IBrush)>(document.Polygons.Count);
        foreach (var polygon in document.Polygons)
        {
            if (TryParseColor(polygon.FillColor) is not { } color)
                continue;
            var brush = new SolidColorBrush(color, polygon.FillOpacity);
            shapes.Add((BuildPolygonGeometry(polygon), brush));
        }
        return shapes;
    }

    private static StreamGeometry BuildPolygonGeometry(RegistryPreviewPolygon polygon)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(polygon.Points[0].X, polygon.Points[0].Y), isFilled: true);
        foreach (var (x, y) in polygon.Points.Skip(1))
            ctx.LineTo(new Point(x, y));
        ctx.EndFigure(isClosed: true);
        return geometry;
    }

    private static Color? TryParseColor(string? raw) =>
        raw is not null && Color.TryParse(raw, out var color) ? color : null;
}
