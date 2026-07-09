using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.Controls;

/// <summary>
/// Miniature preview control for the component library panel. Draws the real GDS
/// geometry + pins when geometry is available via the shared render service,
/// otherwise falls back to a schematic box (rectangle + pins).
/// </summary>
public class ComponentPreview : Control
{
    public static readonly StyledProperty<double> WidthMicrometersProperty =
        AvaloniaProperty.Register<ComponentPreview, double>(nameof(WidthMicrometers));

    public static readonly StyledProperty<double> HeightMicrometersProperty =
        AvaloniaProperty.Register<ComponentPreview, double>(nameof(HeightMicrometers));

    public static readonly StyledProperty<PinDefinition[]?> PinDefinitionsProperty =
        AvaloniaProperty.Register<ComponentPreview, PinDefinition[]?>(nameof(PinDefinitions));

    public static readonly StyledProperty<string?> NazcaModuleNameProperty =
        AvaloniaProperty.Register<ComponentPreview, string?>(nameof(NazcaModuleName));

    public static readonly StyledProperty<string?> NazcaFunctionNameProperty =
        AvaloniaProperty.Register<ComponentPreview, string?>(nameof(NazcaFunctionName));

    public static readonly StyledProperty<string?> GdsFactoryFunctionProperty =
        AvaloniaProperty.Register<ComponentPreview, string?>(nameof(GdsFactoryFunction));

    /// <summary>Attached service, set once on an ancestor so all thumbnails inherit it.</summary>
    public static readonly AttachedProperty<GdsPreviewRenderService?> RenderServiceProperty =
        AvaloniaProperty.RegisterAttached<ComponentPreview, Control, GdsPreviewRenderService?>("RenderService", inherits: true);

    public static void SetRenderService(Control c, GdsPreviewRenderService? v) => c.SetValue(RenderServiceProperty, v);

    public static GdsPreviewRenderService? GetRenderService(Control c) => c.GetValue(RenderServiceProperty);

    public double WidthMicrometers
    {
        get => GetValue(WidthMicrometersProperty);
        set => SetValue(WidthMicrometersProperty, value);
    }

    public double HeightMicrometers
    {
        get => GetValue(HeightMicrometersProperty);
        set => SetValue(HeightMicrometersProperty, value);
    }

    public PinDefinition[]? PinDefinitions
    {
        get => GetValue(PinDefinitionsProperty);
        set => SetValue(PinDefinitionsProperty, value);
    }

    public string? NazcaModuleName
    {
        get => GetValue(NazcaModuleNameProperty);
        set => SetValue(NazcaModuleNameProperty, value);
    }

    public string? NazcaFunctionName
    {
        get => GetValue(NazcaFunctionNameProperty);
        set => SetValue(NazcaFunctionNameProperty, value);
    }

    /// <summary>Module-qualified gdsfactory factory name for gdsfactory-native components
    /// (e.g. "cspdk.sin300.mmi1x2"); drives the gdsfactory geometry preview (#570).</summary>
    public string? GdsFactoryFunction
    {
        get => GetValue(GdsFactoryFunctionProperty);
        set => SetValue(GdsFactoryFunctionProperty, value);
    }

    private GdsPreviewRenderService? _service;

    static ComponentPreview()
    {
        AffectsRender<ComponentPreview>(
            WidthMicrometersProperty,
            HeightMicrometersProperty,
            PinDefinitionsProperty,
            NazcaModuleNameProperty,
            NazcaFunctionNameProperty,
            GdsFactoryFunctionProperty);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _service = GetRenderService(this);
        if (_service != null) _service.OnPreviewLoaded += OnPreviewReady;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_service != null) { _service.OnPreviewLoaded -= OnPreviewReady; _service = null; }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPreviewReady() => Dispatcher.UIThread.Post(InvalidateVisual);

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        double compW = WidthMicrometers;
        double compH = HeightMicrometers;
        if (compW <= 0 || compH <= 0) return;

        // Scale to fit with padding
        const double pad = 4;
        double availW = bounds.Width - pad * 2;
        double availH = bounds.Height - pad * 2;
        double scale = Math.Min(availW / compW, availH / compH);

        double drawW = compW * scale;
        double drawH = compH * scale;
        double offsetX = pad + (availW - drawW) / 2;
        double offsetY = pad + (availH - drawH) / 2;

        var geometry = _service?.TryGetGeometry(
            new GdsPreviewKey(NazcaModuleName, NazcaFunctionName, null) { GdsFactoryFunction = GdsFactoryFunction });
        if (geometry != null && geometry.Polygons.Count > 0)
            GdsPolygonRenderer.DrawPolygonsAsGeometry(context, geometry, offsetX, offsetY, drawW, drawH);
        else
            DrawSchematicBox(context, new Rect(offsetX, offsetY, drawW, drawH));

        DrawPins(context, offsetX, offsetY, scale);
    }

    /// <summary>Draws the schematic fallback body (filled rectangle + border).</summary>
    private static void DrawSchematicBox(DrawingContext context, Rect rect)
    {
        var fillBrush = new SolidColorBrush(Color.FromRgb(40, 50, 70));
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 110, 130)), 1);
        context.FillRectangle(fillBrush, rect);
        context.DrawRectangle(borderPen, rect);
    }

    /// <summary>Draws the pin dots and direction indicators over the body.</summary>
    private void DrawPins(DrawingContext context, double offsetX, double offsetY, double scale)
    {
        var pins = PinDefinitions;
        if (pins == null || pins.Length == 0) return;

        double pinRadius = Math.Max(2, Math.Min(4, scale * 3));
        double dirLen = pinRadius * 2.5;
        var pinBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        var dirPen = new Pen(new SolidColorBrush(Color.FromRgb(180, 220, 180)), 1);

        foreach (var pin in pins)
        {
            double px = offsetX + pin.OffsetX * scale;
            double py = offsetY + pin.OffsetY * scale;

            // Pin dot
            context.DrawEllipse(pinBrush, null, new Point(px, py), pinRadius, pinRadius);

            // Direction indicator
            double angle = pin.AngleDegrees * Math.PI / 180;
            context.DrawLine(dirPen,
                new Point(px, py),
                new Point(px + Math.Cos(angle) * dirLen, py + Math.Sin(angle) * dirLen));
        }
    }
}
