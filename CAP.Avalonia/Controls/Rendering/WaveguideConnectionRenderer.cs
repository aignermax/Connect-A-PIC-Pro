using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Visualization;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.PinKinds;
using CAP_Core.Routing;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Renders waveguide connections (routed paths with power flow visualization).
/// Skips connections that are internal to component groups.
/// Implements <see cref="ICanvasRenderer"/> for world-space rendering.
/// </summary>
public sealed class WaveguideConnectionRenderer : ICanvasRenderer
{
    /// <inheritdoc/>
    public void Render(DrawingContext context, CanvasRenderContext rc)
    {
        var vm = rc.ViewModel;
        var allGroups = WaveguideFilteringHelper.CollectAllGroups(vm.Components.Select(c => c.Component));

        var hovered = rc.InteractionState.HoveredConnection;
        foreach (var conn in vm.Connections)
        {
            if (!WaveguideFilteringHelper.IsConnectionInternalToAnyGroup(conn.Connection, allGroups))
                DrawWaveguideConnection(context, conn, vm, ReferenceEquals(conn, hovered), rc.Zoom);
        }

        if (vm.ShowPowerFlow && rc.InteractionState.HoveredConnection != null)
            DrawPowerHoverLabel(context, rc.InteractionState.HoveredConnection, vm);
    }

    private static void DrawWaveguideConnection(DrawingContext context, WaveguideConnectionViewModel conn,
        DesignCanvasViewModel vm, bool isHovered, double zoom)
    {
        var segments = conn.Connection.GetPathSegments();
        var pen = CreateWaveguidePen(conn, vm, isHovered);
        bool pathIsStale = segments.Count > 0 && UsesStaleFallback(conn) && IsPathStale(segments, conn);

        if (segments.Count == 0 || pathIsStale)
        {
            context.DrawLine(pen, new Point(conn.StartX, conn.StartY), new Point(conn.EndX, conn.EndY));
            return;
        }

        DrawPathSegments(context, pen, segments);

        // Length/loss is detail info the design already conveys via width/thickness/colour, so
        // an unselected, unhovered connection stays label-free and only reveals its numbers
        // on demand.
        if (isHovered || conn.IsSelected)
            DrawConnectionLabel(context, conn, vm, zoom);
    }

    /// <summary>Copper/gold marking electrical (metal) connections, matching the electrical
    /// pin colour in <see cref="PinRenderer"/> (#519/#682).</summary>
    private static readonly Color ElectricalTraceColor = Color.FromRgb(218, 165, 32);

    /// <summary>Metal traces are drawn markedly thicker than optical waveguides so they read as
    /// physical metal strips on the canvas, not thin light paths (#682 field feedback).</summary>
    private const double ElectricalTraceThickness = 5;

    /// <summary>True when both endpoints are electrical pins — i.e. this is a metal trace,
    /// not an optical waveguide (matches the export classification in the exporters).</summary>
    private static bool IsElectricalTrace(WaveguideConnectionViewModel conn) =>
        PinKindHelper.IsElectrical(conn.Connection.StartPin)
        && PinKindHelper.IsElectrical(conn.Connection.EndPin);

    /// <summary>Extra stroke width (px) added to the connection under the cursor.</summary>
    private const double HoverThicknessBoost = 2.0;

    private static Pen CreateWaveguidePen(WaveguideConnectionViewModel conn, DesignCanvasViewModel vm, bool isHovered)
    {
        // Selection styling wins over hover — a selected connection is already emphasized.
        if (conn.IsSelected)
            return new Pen(Brushes.Yellow, 3);

        return Emphasize(CreateBasePen(conn, vm), isHovered);
    }

    /// <summary>Builds the connection's normal (non-selected, non-hovered) pen.</summary>
    private static Pen CreateBasePen(WaveguideConnectionViewModel conn, DesignCanvasViewModel vm)
    {
        // Electrical connections are metal traces: a thick copper/gold strip, distinct from
        // optical waveguides. They carry no optical power flow, so this takes precedence over the
        // power-flow styling below.
        if (IsElectricalTrace(conn))
            return new Pen(new SolidColorBrush(ElectricalTraceColor), ElectricalTraceThickness)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };

        if (vm.ShowPowerFlow && vm.PowerFlowVisualizer.CurrentResult != null)
        {
            var flow = vm.PowerFlowVisualizer.GetFlowForConnection(conn.Connection.Id);
            return flow != null
                ? PowerFlowRenderer.CreatePowerPen(flow, vm.PowerFlowVisualizer.FadeThresholdDb)
                : new Pen(new SolidColorBrush(Color.FromArgb(40, 80, 80, 120)), 1);
        }

        if (conn.IsBlockedFallback || conn.Connection.RoutedPath?.IsInvalidGeometry == true)
            return new Pen(Brushes.Red, 2) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) };

        return new Pen(Brushes.Orange, 2);
    }

    /// <summary>
    /// Emphasizes the hovered connection so the user sees it is clickable: keeps the base pen's
    /// colour intent but draws it thicker and brighter (a solid-colour brush is lightened toward
    /// white). Returns the pen unchanged when not hovered.
    /// </summary>
    private static Pen Emphasize(Pen basePen, bool isHovered)
    {
        if (!isHovered)
            return basePen;

        var brush = basePen.Brush is ISolidColorBrush solid
            ? new SolidColorBrush(Lighten(solid.Color))
            : basePen.Brush;

        return new Pen(brush, basePen.Thickness + HoverThicknessBoost)
        {
            DashStyle = basePen.DashStyle,
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
    }

    /// <summary>Blends a colour halfway toward white for a brighter hover highlight.</summary>
    private static Color Lighten(Color c) => Color.FromArgb(
        c.A,
        (byte)((c.R + 255) / 2),
        (byte)((c.G + 255) / 2),
        (byte)((c.B + 255) / 2));

    /// <summary>
    /// Only automatic, non-frozen routes may be replaced by the straight endpoint-to-endpoint
    /// fallback when their endpoints drift (<see cref="IsPathStale"/>). Styled (non-Auto) and
    /// frozen routes always draw their REAL geometry: their curve is the truth the user chose
    /// and the exporter writes — e.g. an honest Straight between non-collinear pins visibly
    /// stops short of the end pin instead of being faked into a diagonal line.
    /// </summary>
    private static bool UsesStaleFallback(WaveguideConnectionViewModel conn) =>
        conn.Connection.Type == CAP_Core.Components.Connections.WaveguideType.Auto
        && !conn.Connection.IsRouteFrozen;

    private static bool IsPathStale(IReadOnlyList<CAP_Core.Routing.PathSegment> segments, WaveguideConnectionViewModel conn)
    {
        var first = segments[0];
        var last = segments[^1];
        double startDist = Math.Sqrt(Math.Pow(first.StartPoint.X - conn.StartX, 2) + Math.Pow(first.StartPoint.Y - conn.StartY, 2));
        double endDist = Math.Sqrt(Math.Pow(last.EndPoint.X - conn.EndX, 2) + Math.Pow(last.EndPoint.Y - conn.EndY, 2));
        return startDist > 1.0 || endDist > 1.0;
    }

    private static void DrawPathSegments(DrawingContext context, Pen pen, IReadOnlyList<CAP_Core.Routing.PathSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment is StraightSegment straight)
                context.DrawLine(pen, new Point(straight.StartPoint.X, straight.StartPoint.Y), new Point(straight.EndPoint.X, straight.EndPoint.Y));
            else if (segment is BendSegment bend)
                DrawArc(context, pen, bend);
        }
    }

    /// <summary>World-space font size for the length/loss label, before the screen-space cap.</summary>
    private const double LabelFontSizeWorld = 10.0;

    /// <summary>World-space font size for the manual-style badge, before the screen-space cap.</summary>
    private const double BadgeFontSizeWorld = 9.0;

    private static void DrawConnectionLabel(DrawingContext context, WaveguideConnectionViewModel conn, DesignCanvasViewModel vm, double zoom)
    {
        if (!PinScreenSize.IsLabelReadable(LabelFontSizeWorld, zoom))
            return;

        var midX = (conn.StartX + conn.EndX) / 2;
        var midY = (conn.StartY + conn.EndY) / 2;
        string labelText;
        IBrush labelBrush;

        // Metal traces carry no optical power, so neither the power-flow figures nor an
        // optical loss make sense on them — label them with their length only, in the
        // electrical copper/gold tint (#682).
        if (IsElectricalTrace(conn))
        {
            labelText = $"{conn.PathLength:F0}µm";
            labelBrush = new SolidColorBrush(ElectricalTraceColor);
        }
        else if (vm.ShowPowerFlow && vm.PowerFlowVisualizer.CurrentResult != null)
        {
            var flow = vm.PowerFlowVisualizer.GetFlowForConnection(conn.Connection.Id);
            if (flow != null && flow.AveragePower > 0)
            {
                labelText = $"{flow.NormalizedPowerDb:F1}dB ({flow.NormalizedPowerFraction * 100:F0}%)";
                var fraction = Math.Clamp(flow.NormalizedPowerFraction, 0, 1);
                labelBrush = new SolidColorBrush(PowerFlowRenderer.InterpolatePowerColor(fraction));
            }
            else
            {
                labelText = "no signal";
                labelBrush = new SolidColorBrush(Color.FromArgb(120, 150, 150, 150));
            }
        }
        else
        {
            labelText = $"{conn.PathLength:F0}µm, {conn.LossDb:F2}dB";
            labelBrush = Brushes.LightGray;
        }

        double labelFontSize = PinScreenSize.CapWorldFontSize(LabelFontSizeWorld, zoom);
        context.DrawText(
            new FormattedText(labelText, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Arial"), labelFontSize, labelBrush),
            new Point(midX, midY - 15));

        // Badge for a manually styled connection (Type != Auto): shows the Nazca style name
        // above the label so it's obvious at a glance that the autorouter no longer owns this
        // route. Style names are Nazca terms and stay untranslated, like the picker entries.
        if (conn.Connection.Type != CAP_Core.Components.Connections.WaveguideType.Auto
            && PinScreenSize.IsLabelReadable(BadgeFontSizeWorld, zoom))
        {
            double badgeFontSize = PinScreenSize.CapWorldFontSize(BadgeFontSizeWorld, zoom);
            context.DrawText(
                new FormattedText(conn.Connection.Type.ToString(),
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Arial", FontStyle.Normal, FontWeight.Bold), badgeFontSize, Brushes.Orange),
                new Point(midX, midY - 28));
        }
    }

    private static void DrawPowerHoverLabel(DrawingContext context, WaveguideConnectionViewModel conn, DesignCanvasViewModel vm)
    {
        var flow = vm.PowerFlowVisualizer.GetFlowForConnection(conn.Connection.Id);
        if (flow == null) return;
        PowerFlowRenderer.DrawPowerLabel(context, flow, new Point((conn.StartX + conn.EndX) / 2, (conn.StartY + conn.EndY) / 2 - 25));
    }

    private static void DrawArc(DrawingContext context, Pen pen, BendSegment bend)
    {
        int numSegments = Math.Max(8, (int)(Math.Abs(bend.SweepAngleDegrees) / 5));
        if (numSegments == 0 || Math.Abs(bend.SweepAngleDegrees) < 0.1)
        {
            context.DrawLine(pen, new Point(bend.StartPoint.X, bend.StartPoint.Y), new Point(bend.EndPoint.X, bend.EndPoint.Y));
            return;
        }

        var points = new List<Point>(numSegments + 1);
        for (int i = 0; i <= numSegments; i++)
        {
            double t = i / (double)numSegments;
            double startRad = bend.StartAngleDegrees * Math.PI / 180;
            double sweepRad = bend.SweepAngleDegrees * Math.PI / 180;
            double angle = startRad + sweepRad * t;
            double sign = Math.Sign(bend.SweepAngleDegrees) == 0 ? 1 : Math.Sign(bend.SweepAngleDegrees);
            double perpAngle = angle - Math.PI / 2 * sign;
            points.Add(new Point(
                bend.Center.X + bend.RadiusMicrometers * Math.Cos(perpAngle),
                bend.Center.Y + bend.RadiusMicrometers * Math.Sin(perpAngle)));
        }

        for (int i = 0; i < points.Count - 1; i++)
            context.DrawLine(pen, points[i], points[i + 1]);
    }
}
