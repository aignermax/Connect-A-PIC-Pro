using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Verifies that an in-canvas bend-radius drag commits as one undoable command: Execute applies
/// the dragged radius and Undo restores the radius captured when the drag began (issue #574).
/// </summary>
public class BendRadiusCommandTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void ExecuteThenUndo_RestoresOriginalBendRadius()
    {
        var conn = CreateConnectionWithBend(radius: 10);
        var vm = new WaveguideConnectionViewModel(conn);
        int applyCount = 0;

        var command = new BendRadiusCommand(vm, bendIndex: 0, beforeRadius: 10, afterRadius: 20,
                                            afterApply: () => applyCount++);

        command.Execute();
        BendRadius(conn).ShouldBe(20, Tolerance);

        command.Undo();
        BendRadius(conn).ShouldBe(10, Tolerance);

        applyCount.ShouldBe(2); // afterApply runs on both Execute and Undo
    }

    [Fact]
    public void Execute_BelowProcessMinimum_DegradesToNoOp()
    {
        var conn = CreateConnectionWithBend(radius: 10);
        var vm = new WaveguideConnectionViewModel(conn);
        int applyCount = 0;

        // A radius below the process minimum must never be committed — even if a stale command
        // (recorded before the process changed) replays it, the geometry stays untouched.
        var command = new BendRadiusCommand(vm, bendIndex: 0, beforeRadius: 10, afterRadius: 3,
                                            afterApply: () => applyCount++, minRadiusMicrometers: 5);

        command.Execute();

        BendRadius(conn).ShouldBe(10, Tolerance);
        conn.BendRadiusOverrides.ShouldBeEmpty();
        applyCount.ShouldBe(0);
    }

    [Fact]
    public void ExecuteThenUndo_WithProcessMinimum_AppliesAndRestores()
    {
        var conn = CreateConnectionWithBend(radius: 10);
        var vm = new WaveguideConnectionViewModel(conn);

        var command = new BendRadiusCommand(vm, bendIndex: 0, beforeRadius: 10, afterRadius: 20,
                                            minRadiusMicrometers: 5);

        command.Execute();
        BendRadius(conn).ShouldBe(20, Tolerance);

        command.Undo();
        BendRadius(conn).ShouldBe(10, Tolerance);
    }

    private static double BendRadius(WaveguideConnection conn) =>
        ((BendSegment)conn.RoutedPath!.Segments[1]).RadiusMicrometers;

    /// <summary>Straight (0,0)→(50,0), 90° bend of the given radius, straight upward — one editable
    /// interior bend (matches BendRadiusEditorTests).</summary>
    private static WaveguideConnection CreateConnectionWithBend(double radius)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, radius, radius, 0, 90));
        path.Segments.Add(new StraightSegment(50 + radius, radius, 50 + radius, 60, 90));

        var conn = new WaveguideConnection();
        conn.RestoreCachedPath(path);
        return conn;
    }
}
