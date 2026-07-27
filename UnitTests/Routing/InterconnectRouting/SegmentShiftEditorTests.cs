using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Mutation semantics of the segment parallel shift (issue #791): the straight translates
/// along its normal, the adjoining bends slide rigidly (radii and sweeps untouched), the
/// route stays connected, and shifts that would collapse a segment are rejected without
/// touching the geometry.
/// </summary>
public class SegmentShiftEditorTests
{
    private const double Tolerance = 1e-9;
    private const int MiddleStraightIndex = 1;

    [Fact]
    public void ApplyShift_MovesTheStraightAlongItsNormal_AndKeepsTheRouteConnected()
    {
        var conn = ZConnection();

        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 20, out var error).ShouldBeTrue(error);

        var segments = conn.RoutedPath!.Segments;
        var middle = (StraightSegment)segments[2];
        middle.StartPoint.X.ShouldBe(40, Tolerance); // normal is (-1,0): +20 moves it west
        middle.EndPoint.X.ShouldBe(40, Tolerance);
        middle.LengthMicrometers.ShouldBe(40, Tolerance);

        ((StraightSegment)segments[0]).EndPoint.X.ShouldBe(30, Tolerance);   // incoming shortens
        ((StraightSegment)segments[4]).StartPoint.X.ShouldBe(50, Tolerance); // outgoing lengthens
        AssertConnected(segments);
    }

    [Fact]
    public void ApplyShift_LeavesBendRadiiAndSweepsUntouched()
    {
        var conn = ZConnection();

        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 20, out _).ShouldBeTrue();

        foreach (var bend in conn.RoutedPath!.Segments.OfType<BendSegment>())
        {
            bend.RadiusMicrometers.ShouldBe(10, Tolerance);
            Math.Abs(bend.SweepAngleDegrees).ShouldBe(90, Tolerance);
        }
    }

    [Fact]
    public void ApplyShift_FreezesTheRoute_AndRecordsTheCumulativeOffset()
    {
        var conn = ZConnection();

        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 15, out _).ShouldBeTrue();

        conn.IsRouteFrozen.ShouldBeTrue();
        conn.StraightShiftOffsets[MiddleStraightIndex].ShouldBe(15, Tolerance);
    }

    [Fact]
    public void ApplyShift_IsCumulative_ReapplyingTheSameOffsetIsANoOp()
    {
        var conn = ZConnection();
        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 20, out _).ShouldBeTrue();
        double xAfterFirst = ((StraightSegment)conn.RoutedPath!.Segments[2]).StartPoint.X;

        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 20, out _).ShouldBeTrue();

        ((StraightSegment)conn.RoutedPath!.Segments[2]).StartPoint.X.ShouldBe(xAfterFirst, Tolerance);
    }

    [Fact]
    public void ApplyShift_BackToZero_RestoresTheOriginalGeometry()
    {
        var conn = ZConnection();
        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 20, out _).ShouldBeTrue();

        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 0, out _).ShouldBeTrue();

        var segments = conn.RoutedPath!.Segments;
        ((StraightSegment)segments[2]).StartPoint.X.ShouldBe(60, Tolerance);
        ((StraightSegment)segments[0]).EndPoint.X.ShouldBe(50, Tolerance);
        ((StraightSegment)segments[4]).StartPoint.X.ShouldBe(70, Tolerance);
        AssertConnected(segments);
    }

    [Fact]
    public void ApplyShift_ThatWouldCollapseANeighbour_IsRejectedWithoutMutation()
    {
        var conn = ZConnection();

        // The incoming straight is 50 µm long; a 60 µm shift would invert it.
        SegmentShiftEditor.TryApplyShift(conn, MiddleStraightIndex, 60, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrEmpty();
        ((StraightSegment)conn.RoutedPath!.Segments[2]).StartPoint.X.ShouldBe(60, Tolerance);
        conn.StraightShiftOffsets.ShouldBeEmpty();
        conn.IsRouteFrozen.ShouldBeFalse();
    }

    [Fact]
    public void ApplyShift_OnAPinAdjacentStraight_IsRejected()
    {
        var conn = ZConnection();

        SegmentShiftEditor.TryApplyShift(conn, 0, 10, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrEmpty();
        ((StraightSegment)conn.RoutedPath!.Segments[0]).EndPoint.X.ShouldBe(50, Tolerance);
    }

    [Fact]
    public void ApplyShift_DiagonalSegment_MovesAlongTheFortyFiveDegreeNormal()
    {
        var conn = new WaveguideConnection();
        conn.RestoreCachedPath(SegmentShiftGeometryTests.DiagonalPath());
        var before = SegmentShiftGeometry.GetHandles(conn.RoutedPath!.Segments).ShouldHaveSingleItem();
        const double Offset = 5;

        SegmentShiftEditor.TryApplyShift(conn, before.StraightIndex, Offset, out var error)
            .ShouldBeTrue(error);

        var after = SegmentShiftGeometry.GetHandles(conn.RoutedPath!.Segments).ShouldHaveSingleItem();
        // The perpendicular displacement equals the offset (the segment may also slide along
        // its own direction because both bends travel along the horizontal outer straights).
        var displacement = (after.Midpoint.X - before.Midpoint.X, after.Midpoint.Y - before.Midpoint.Y);
        SegmentShiftGeometry.Dot(displacement, before.Normal).ShouldBe(Offset, 1e-6);
        after.Direction.ShouldBe(before.Direction);
        foreach (var bend in conn.RoutedPath!.Segments.OfType<BendSegment>())
            bend.RadiusMicrometers.ShouldBe(10, Tolerance);
        AssertConnected(conn.RoutedPath!.Segments);
    }

    private static void AssertConnected(IReadOnlyList<PathSegment> segments)
    {
        for (int i = 0; i < segments.Count - 1; i++)
        {
            segments[i].EndPoint.X.ShouldBe(segments[i + 1].StartPoint.X, 1e-6,
                $"segment {i} end X must meet segment {i + 1} start");
            segments[i].EndPoint.Y.ShouldBe(segments[i + 1].StartPoint.Y, 1e-6,
                $"segment {i} end Y must meet segment {i + 1} start");
        }
    }

    private static WaveguideConnection ZConnection()
    {
        var conn = new WaveguideConnection();
        conn.RestoreCachedPath(SegmentShiftGeometryTests.ZPath());
        return conn;
    }
}
