using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;
using System.Globalization;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Draws the Logic panel's waveform strip (issue #1129, rung 5 visualizer): one
/// horizontal 0/1 step trace per named signal lane over the event timeline, the
/// "── clock #k ──" boundaries as thin vertical lines across all lanes, and the
/// replay cursor marking the replayed instant. The drawing is a pure projection of
/// <see cref="LogicWaveformModel"/> — the mapper already normalized times to x
/// fractions, so this renderer only scales fractions to pixels: the label column
/// holds the signal names, the trace area spans the remaining width up to a right
/// padding, 0 maps to its left edge and 1 to its right edge. Same dark-chip style
/// family as the canvas logic renderers (<see cref="LogicGateStateBadgeRenderer"/>,
/// issue #994): muted green traces and gray labels, the gold of the timeline's
/// clock dividers for the boundary lines. Read-only — no zoom, pan, or hover in
/// this slice.
/// </summary>
internal static class LogicWaveformRenderer
{
    /// <summary>Width of the left column carrying the signal names.</summary>
    internal const double LabelWidth = 84;

    /// <summary>Height of one signal lane.</summary>
    internal const double LaneHeight = 22;

    /// <summary>Height of the top band the clock-divider labels sit in.</summary>
    internal const double HeaderHeight = 16;

    /// <summary>Empty space below the last lane.</summary>
    internal const double BottomPadding = 4;

    /// <summary>Empty space right of the last edge, so a final edge never hugs the border.</summary>
    internal const double RightPadding = 12;

    /// <summary>Distance of the high (1) line from its lane band's top.</summary>
    internal const double LaneHighInset = 3;

    /// <summary>Distance of the low (0) line from its lane band's top.</summary>
    internal const double LaneLowInset = 17;

    private const double LabelFontSize = 10;
    private const double DividerFontSize = 9;
    private const double TraceThickness = 1.5;
    private const double LabelLeft = 4;

    private static readonly Color TraceColor = Color.FromRgb(139, 224, 139); // muted light green, like the live "1" digits
    private static readonly Color LabelColor = Colors.LightGray;
    private static readonly Color DividerColor = Color.FromRgb(230, 200, 96); // the timeline list's divider gold
    private static readonly Color CursorColor = Color.FromRgb(245, 245, 245);

    private static readonly IBrush LabelBrush = new SolidColorBrush(LabelColor);
    private static readonly IBrush DividerBrush = new SolidColorBrush(DividerColor);
    private static readonly Pen TracePen = new(new SolidColorBrush(TraceColor), TraceThickness);
    private static readonly Pen DividerPen = new(DividerBrush, 1);
    private static readonly Pen CursorPen = new(new SolidColorBrush(CursorColor), 1);

    /// <summary>The strip's natural height for a model: header band + one band per lane.</summary>
    internal static double DesiredHeight(LogicWaveformModel? model) =>
        model == null ? 0 : HeaderHeight + model.Lanes.Count * LaneHeight + BottomPadding;

    /// <summary>The x of the trace area's left edge (normalized 0).</summary>
    internal static double TraceLeft => LabelWidth;

    /// <summary>The trace area's width for a strip of <paramref name="totalWidth"/> pixels.</summary>
    internal static double TraceWidth(double totalWidth) =>
        Math.Max(0, totalWidth - LabelWidth - RightPadding);

    /// <summary>The pixel x of a normalized timeline position.</summary>
    internal static double MapX(double xFraction, double totalWidth) =>
        LabelWidth + xFraction * TraceWidth(totalWidth);

    /// <summary>The pixel y of a lane band's top edge.</summary>
    internal static double LaneBandTop(int laneIndex) => HeaderHeight + laneIndex * LaneHeight;

    /// <summary>The pixel y of a lane's high (1) line.</summary>
    internal static double LaneHighY(int laneIndex) => LaneBandTop(laneIndex) + LaneHighInset;

    /// <summary>The pixel y of a lane's low (0) line.</summary>
    internal static double LaneLowY(int laneIndex) => LaneBandTop(laneIndex) + LaneLowInset;

    /// <summary>
    /// Snaps a vertical line's x to the nearest pixel center, so a 1px rule lands on
    /// one pixel column at full strength instead of splitting across two.
    /// </summary>
    private static double SnapToPixelCenter(double x) => Math.Round(x) + 0.5;

    /// <summary>Draws the whole strip: dividers behind the lanes, the cursor on top.</summary>
    /// <param name="context">Drawing context.</param>
    /// <param name="model">The waveform model to project; empty models draw nothing.</param>
    /// <param name="bounds">The strip's pixel size.</param>
    public static void Render(DrawingContext context, LogicWaveformModel model, Size bounds)
    {
        if (model.Lanes.Count == 0)
            return;
        foreach (var divider in model.Dividers)
            DrawDivider(context, divider, bounds);
        for (var i = 0; i < model.Lanes.Count; i++)
            DrawLane(context, model.Lanes[i], i, bounds);
        if (model.CursorXFraction.HasValue)
        {
            var x = SnapToPixelCenter(MapX(model.CursorXFraction.Value, bounds.Width));
            context.DrawLine(CursorPen, new Point(x, 0), new Point(x, bounds.Height - BottomPadding));
        }
    }

    /// <summary>Draws one clock boundary: a vertical gold line across all lanes plus its label.</summary>
    private static void DrawDivider(
        DrawingContext context, LogicWaveformClockDivider divider, Size bounds)
    {
        var x = MapX(divider.XFraction, bounds.Width);
        var lineX = SnapToPixelCenter(x);
        context.DrawLine(DividerPen, new Point(lineX, 0), new Point(lineX, bounds.Height - BottomPadding));
        var text = new FormattedText(
            divider.Label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            DividerFontSize,
            DividerBrush);
        context.DrawText(text, new Point(x + 3, 2));
    }

    /// <summary>Draws one lane: its signal name in the label column and its step trace.</summary>
    private static void DrawLane(
        DrawingContext context, LogicWaveformLane lane, int laneIndex, Size bounds)
    {
        var bandTop = LaneBandTop(laneIndex);
        var label = new FormattedText(
            lane.SignalName,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            LabelFontSize,
            LabelBrush);
        context.DrawText(label, new Point(LabelLeft, bandTop + (LaneHeight - label.Height) / 2));

        var yHigh = bandTop + LaneHighInset;
        var yLow = bandTop + LaneLowInset;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var y = lane.InitialLevel ? yHigh : yLow;
            ctx.BeginFigure(new Point(TraceLeft, y), false);
            foreach (var edge in lane.Edges)
            {
                var x = MapX(edge.XFraction, bounds.Width);
                ctx.LineTo(new Point(x, y));
                y = edge.NewLevel ? yHigh : yLow;
                ctx.LineTo(new Point(x, y));
            }
            ctx.LineTo(new Point(TraceLeft + TraceWidth(bounds.Width), y));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, TracePen, geometry);
    }
}
