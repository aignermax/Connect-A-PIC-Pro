using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.PinKinds;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;

namespace CAP.Avalonia.Controls.Canvas.BendHandles;

/// <summary>
/// Draws Figma-style in-canvas bend-radius handles for the selected waveguide connection
/// (issue #574). Runs in the world transform; all handle sizes are divided by
/// <see cref="CanvasRenderContext.Zoom"/> so they stay screen-constant. Electrical (metal)
/// connections get no handles, matching the #631 export rule. The selected connection comes
/// from <c>MainViewModel.CanvasInteraction.SelectedWaveguideConnection</c>; the active/clamped
/// handle state comes from <see cref="CanvasInteractionState"/>.
/// </summary>
public sealed class BendHandleRenderer : ICanvasRenderer
{
    private const double HandleRadiusPx = 6.0;
    private const double HandleStrokePx = 1.5;
    private const double LabelFontPx = 11.0;
    private const double LabelOffsetPx = 10.0;

    private static readonly IBrush HandleFill = new SolidColorBrush(Color.FromRgb(80, 160, 255));
    private static readonly IBrush ActiveFill = new SolidColorBrush(Color.FromRgb(120, 200, 255));
    private static readonly IBrush ClampFill = new SolidColorBrush(Color.FromRgb(230, 70, 70));
    private static readonly IBrush HandleStrokeBrush = Brushes.White;
    private static readonly IBrush LabelBrush = Brushes.White;
    private static readonly Typeface LabelTypeface = new("Arial");

    /// <inheritdoc/>
    public void Render(DrawingContext context, CanvasRenderContext rc)
    {
        var selected = rc.MainViewModel?.CanvasInteraction.SelectedWaveguideConnection;
        if (selected == null || !IsOptical(selected))
            return;

        double zoom = rc.Zoom <= 0 ? 1.0 : rc.Zoom;
        DrawHandles(context, selected, rc.InteractionState, zoom);
    }

    /// <summary>Optical when it is not a metal trace (both pins electrical), matching #631.</summary>
    private static bool IsOptical(WaveguideConnectionViewModel conn) =>
        !(PinKindHelper.IsElectrical(conn.Connection.StartPin)
          && PinKindHelper.IsElectrical(conn.Connection.EndPin));

    private static void DrawHandles(DrawingContext context, WaveguideConnectionViewModel conn,
                                    CanvasInteractionState state, double zoom)
    {
        var corners = BendRadiusEditor.GetBendCorners(conn.Connection.GetPathSegments());
        double radius = HandleRadiusPx / zoom;
        var stroke = new Pen(HandleStrokeBrush, HandleStrokePx / zoom);

        foreach (var corner in corners)
        {
            var (hx, hy) = BendHandleGeometry.HandlePoint(corner);
            var fill = SelectFill(corner.BendIndex, state);
            context.DrawEllipse(fill, stroke, new Point(hx, hy), radius, radius);
            DrawLabel(context, corner, hx, hy, zoom);
        }
    }

    private static IBrush SelectFill(int bendIndex, CanvasInteractionState state)
    {
        if (bendIndex != state.ActiveBendIndex)
            return HandleFill;
        return state.ActiveBendClamped ? ClampFill : ActiveFill;
    }

    private static void DrawLabel(DrawingContext context, BendCorner corner, double hx, double hy, double zoom)
    {
        // Numeric label uses the UI culture (a display string); the unit "µm" is allow-listed.
        string text = string.Create(CultureInfo.CurrentCulture, $"{corner.RadiusMicrometers:F0} µm");
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, LabelFontPx / zoom, LabelBrush);
        double offset = (HandleRadiusPx + LabelOffsetPx) / zoom;
        var origin = new Point(hx + offset * corner.Bisector.X, hy + offset * corner.Bisector.Y);
        context.DrawText(formatted, origin);
    }
}
