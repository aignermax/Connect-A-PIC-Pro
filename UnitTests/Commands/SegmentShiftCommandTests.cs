using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using Shouldly;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Verifies that an in-canvas segment parallel-shift drag commits as one undoable command
/// (issue #791): Execute applies the dragged cumulative offset and Undo restores the offset
/// captured when the drag began, so Ctrl+Z reverts the edit exactly.
/// </summary>
public class SegmentShiftCommandTests
{
    private const double Tolerance = 1e-6;
    private const int MiddleStraightIndex = 1;

    [Fact]
    public void ExecuteThenUndo_RestoresOriginalGeometryAndOffset()
    {
        var conn = CreateZConnection();
        var vm = new WaveguideConnectionViewModel(conn);
        int applyCount = 0;

        var command = new SegmentShiftCommand(vm, MiddleStraightIndex,
                                              beforeOffset: 0, afterOffset: 20,
                                              afterApply: () => applyCount++);

        command.Execute();
        MiddleX(conn).ShouldBe(40, Tolerance); // normal (-1,0): +20 µm moves the straight west
        conn.StraightShiftOffsets[MiddleStraightIndex].ShouldBe(20, Tolerance);

        command.Undo();
        MiddleX(conn).ShouldBe(60, Tolerance);
        conn.StraightShiftOffsets[MiddleStraightIndex].ShouldBe(0, Tolerance);

        applyCount.ShouldBe(2); // afterApply (collision re-check) runs on Execute and Undo
    }

    [Fact]
    public void Execute_AfterLiveDragAlreadyApplied_IsANoOp()
    {
        var conn = CreateZConnection();
        var vm = new WaveguideConnectionViewModel(conn);
        // The gesture recognizer applies the shift live during the drag …
        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 20, out _).ShouldBeTrue();

        // … so committing the command must not move the geometry a second time.
        new SegmentShiftCommand(vm, MiddleStraightIndex, beforeOffset: 0, afterOffset: 20).Execute();

        MiddleX(conn).ShouldBe(40, Tolerance);
    }

    [Fact]
    public void Execute_OnANonShiftableSegment_DegradesToNoOp()
    {
        var conn = CreateZConnection();
        var vm = new WaveguideConnectionViewModel(conn);
        int applyCount = 0;

        // Straight #0 is pin-adjacent — a stale command (recorded before a re-route changed
        // the path) must leave the geometry untouched.
        var command = new SegmentShiftCommand(vm, straightIndex: 0,
                                              beforeOffset: 0, afterOffset: 20,
                                              afterApply: () => applyCount++);
        command.Execute();

        MiddleX(conn).ShouldBe(60, Tolerance);
        conn.StraightShiftOffsets.ShouldBeEmpty();
        applyCount.ShouldBe(0);
    }

    private static double MiddleX(WaveguideConnection conn) =>
        ((StraightSegment)conn.RoutedPath!.Segments[2]).StartPoint.X;

    /// <summary>Z-path: east 50 µm, 90° bend, north 40 µm at x=60, 90° bend, east 50 µm.</summary>
    private static WaveguideConnection CreateZConnection()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(60, 10, 60, 50, 90));
        path.Segments.Add(new BendSegment(70, 50, 10, 90, -90));
        path.Segments.Add(new StraightSegment(70, 60, 120, 60, 0));

        var conn = new WaveguideConnection();
        conn.RestoreCachedPath(path);
        return conn;
    }
}
