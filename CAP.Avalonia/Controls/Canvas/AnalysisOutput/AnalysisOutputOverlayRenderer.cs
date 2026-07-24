using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;

namespace CAP.Avalonia.Controls.Canvas.AnalysisOutput;

/// <summary>
/// Canvas overlay for the analysis-output designation (issue #754). While the
/// eyedropper picker (<see cref="InteractionMode.PickAnalysisOutput"/>) is active,
/// every candidate coupler glows so the user sees what is clickable; the designated
/// output coupler permanently carries a subtle "OUT" tag with an outline. Runs in the
/// world transform; stroke widths and font sizes are divided by the zoom so they stay
/// screen-constant (pattern: <c>BendHandleRenderer</c>).
/// </summary>
public sealed class AnalysisOutputOverlayRenderer : ICanvasRenderer
{
    private const double GlowStrokePx = 2.0;
    private const double GlowPaddingPx = 4.0;
    private const double GlowCornerRadiusPx = 4.0;
    private const double TagFontPx = 10.0;
    private const double TagPaddingPx = 3.0;
    private const double TagCornerRadiusPx = 3.0;
    private const double OutlineStrokePx = 1.5;
    private const string TagText = "OUT";

    private static readonly Color Amber = Color.FromRgb(229, 192, 123);
    private static readonly IBrush GlowStroke = new SolidColorBrush(Color.FromArgb(230, Amber.R, Amber.G, Amber.B));
    private static readonly IBrush GlowFill = new SolidColorBrush(Color.FromArgb(28, Amber.R, Amber.G, Amber.B));
    private static readonly IBrush TagBackground = new SolidColorBrush(Color.FromArgb(220, 40, 40, 40));
    private static readonly IBrush TagForeground = new SolidColorBrush(Amber);
    private static readonly IBrush OutlineStroke = new SolidColorBrush(Color.FromArgb(170, Amber.R, Amber.G, Amber.B));
    private static readonly Typeface TagTypeface = new("Arial", weight: FontWeight.Bold);

    /// <inheritdoc/>
    public void Render(DrawingContext context, CanvasRenderContext rc)
    {
        double zoom = rc.Zoom <= 0 ? 1.0 : rc.Zoom;
        bool picking = rc.MainViewModel?.CanvasInteraction.CurrentMode == InteractionMode.PickAnalysisOutput;
        var designatedId = rc.ViewModel.AnalysisOutput.CouplerId;
        if (!picking && designatedId == null) return;

        foreach (var comp in rc.ViewModel.Components)
        {
            if (comp.LaserConfig == null) continue;
            if (picking)
                DrawCandidateGlow(context, comp, zoom);
            if (designatedId != null && comp.Component.Id == designatedId.Value)
                DrawDesignatedMarker(context, comp, zoom);
        }
    }

    /// <summary>Glowing outline + translucent fill marking a clickable coupler in picker mode.</summary>
    private static void DrawCandidateGlow(DrawingContext context, ComponentViewModel comp, double zoom)
    {
        var bounds = PaddedBounds(comp, zoom);
        var pen = new Pen(GlowStroke, GlowStrokePx / zoom);
        double radius = GlowCornerRadiusPx / zoom;
        context.DrawRectangle(GlowFill, pen, bounds, radius, radius);
    }

    /// <summary>Permanent, subtle marker of the designated output: outline + "OUT" tag.</summary>
    private static void DrawDesignatedMarker(DrawingContext context, ComponentViewModel comp, double zoom)
    {
        var bounds = PaddedBounds(comp, zoom);
        var pen = new Pen(OutlineStroke, OutlineStrokePx / zoom);
        double radius = GlowCornerRadiusPx / zoom;
        context.DrawRectangle(null, pen, bounds, radius, radius);
        DrawOutTag(context, bounds, zoom);
    }

    /// <summary>Small "OUT" chip anchored to the top-right corner of the component bounds.</summary>
    private static void DrawOutTag(DrawingContext context, Rect bounds, double zoom)
    {
        var text = new FormattedText(
            TagText, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, TagTypeface, TagFontPx / zoom, TagForeground);
        double padding = TagPaddingPx / zoom;
        double chipWidth = text.Width + 2 * padding;
        double chipHeight = text.Height + 2 * padding;
        // Sits half above the outline so it reads as a tag, not part of the component.
        var chip = new Rect(bounds.Right - chipWidth, bounds.Top - chipHeight / 2, chipWidth, chipHeight);
        double radius = TagCornerRadiusPx / zoom;
        context.DrawRectangle(TagBackground, new Pen(TagForeground, 1.0 / zoom), chip, radius, radius);
        context.DrawText(text, new Point(chip.X + padding, chip.Y + padding));
    }

    /// <summary>Component bounds expanded by a screen-constant padding.</summary>
    private static Rect PaddedBounds(ComponentViewModel comp, double zoom)
    {
        double pad = GlowPaddingPx / zoom;
        return new Rect(comp.X - pad, comp.Y - pad, comp.Width + 2 * pad, comp.Height + 2 * pad);
    }
}
