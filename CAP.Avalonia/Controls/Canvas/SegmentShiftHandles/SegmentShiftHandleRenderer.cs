using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.PinKinds;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;

namespace CAP.Avalonia.Controls.Canvas.SegmentShiftHandles;

/// <summary>
/// Draws the midpoint parallel-shift handles on the straight segments of the selected
/// waveguide connection (issue #791), in the same visual language as the bend-radius handles
/// but diamond-shaped so the two edits are distinguishable at a glance. Runs in the world
/// transform; sizes are divided by <see cref="CanvasRenderContext.Zoom"/> so they stay
/// screen-constant. While a handle is dragged, the live shift delta is shown as a "Δ µm"
/// label; a clamped drag paints the handle red, mirroring the bend handles.
/// </summary>
public sealed class SegmentShiftHandleRenderer : ICanvasRenderer
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
        var handles = SegmentShiftGeometry.GetHandles(conn.Connection.GetPathSegments());
        double radius = HandleRadiusPx / zoom;
        var stroke = new Pen(HandleStrokeBrush, HandleStrokePx / zoom);

        foreach (var handle in handles)
        {
            var fill = SelectFill(handle.StraightIndex, state);
            context.DrawGeometry(fill, stroke, BuildDiamond(handle, radius));
            if (handle.StraightIndex == state.ActiveShiftStraightIndex)
                DrawDeltaLabel(context, handle, state.ActiveShiftDeltaMicrometers, zoom);
        }
    }

    private static IBrush SelectFill(int straightIndex, CanvasInteractionState state)
    {
        if (straightIndex != state.ActiveShiftStraightIndex)
            return HandleFill;
        return state.ActiveShiftClamped ? ClampFill : ActiveFill;
    }

    /// <summary>Diamond aligned with the segment: tips along the direction, waist along
    /// the normal — visually hinting that the drag moves across the segment.</summary>
    private static StreamGeometry BuildDiamond(StraightSegmentHandle handle, double radius)
    {
        var (mx, my) = handle.Midpoint;
        var (dx, dy) = handle.Direction;
        var (nx, ny) = handle.Normal;

        var geometry = new StreamGeometry();
        using var geometryContext = geometry.Open();
        geometryContext.BeginFigure(new Point(mx + radius * dx, my + radius * dy), isFilled: true);
        geometryContext.LineTo(new Point(mx + radius * nx, my + radius * ny));
        geometryContext.LineTo(new Point(mx - radius * dx, my - radius * dy));
        geometryContext.LineTo(new Point(mx - radius * nx, my - radius * ny));
        geometryContext.EndFigure(isClosed: true);
        return geometry;
    }

    private static void DrawDeltaLabel(DrawingContext context, StraightSegmentHandle handle,
                                       double deltaMicrometers, double zoom)
    {
        // Numeric label uses the UI culture (a display string); the unit "µm" is allow-listed.
        string text = string.Create(CultureInfo.CurrentCulture, $"Δ {deltaMicrometers:+0.0;-0.0;0.0} µm");
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, LabelFontPx / zoom, LabelBrush);
        double offset = (HandleRadiusPx + LabelOffsetPx) / zoom;
        double originX = handle.Midpoint.X + offset * handle.Normal.X;
        double originY = handle.Midpoint.Y + offset * handle.Normal.Y;

        // DrawText renders from the text's TOP-LEFT corner; anchor the side facing the handle
        // so the label always grows away from it (same trick as the bend-radius labels).
        if (handle.Normal.X < 0)
            originX -= formatted.Width;
        if (handle.Normal.Y < 0)
            originY -= formatted.Height;

        context.DrawText(formatted, new Point(originX, originY));
    }
}
