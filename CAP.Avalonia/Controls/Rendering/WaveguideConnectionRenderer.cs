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

        foreach (var conn in vm.Connections)
        {
            if (!WaveguideFilteringHelper.IsConnectionInternalToAnyGroup(conn.Connection, allGroups))
                DrawWaveguideConnection(context, conn, vm);
        }

        if (vm.ShowPowerFlow && rc.InteractionState.HoveredConnection != null)
            DrawPowerHoverLabel(context, rc.InteractionState.HoveredConnection, vm);
    }

    private static void DrawWaveguideConnection(DrawingContext context, WaveguideConnectionViewModel conn, DesignCanvasViewModel vm)
    {
        var segments = conn.Connection.GetPathSegments();
        var pen = CreateWaveguidePen(conn, vm);
        bool pathIsStale = segments.Count > 0 && IsPathStale(segments, conn);

        if (segments.Count == 0 || pathIsStale)
        {
            context.DrawLine(pen, new Point(conn.StartX, conn.StartY), new Point(conn.EndX, conn.EndY));
            return;
        }

        DrawPathSegments(context, pen, segments);
        DrawConnectionLabel(context, conn, vm);
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

    private static Pen CreateWaveguidePen(WaveguideConnectionViewModel conn, DesignCanvasViewModel vm)
    {
        if (conn.IsSelected)
            return new Pen(Brushes.Yellow, 3);

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

    private static void DrawConnectionLabel(DrawingContext context, WaveguideConnectionViewModel conn, DesignCanvasViewModel vm)
    {
        var midX = (conn.StartX + conn.EndX) / 2;
        var midY = (conn.StartY + conn.EndY) / 2;
        string labelText;
        IBrush labelBrush;

        if (vm.ShowPowerFlow && vm.PowerFlowVisualizer.CurrentResult != null)
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

        context.DrawText(
            new FormattedText(labelText, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Arial"), 10, labelBrush),
            new Point(midX, midY - 15));
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
