using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Pure geometry of the segment parallel-shift handles (issue #791): which straights get a
/// handle, where the handle sits, and how a pointer position maps onto the perpendicular
/// shift axis.
/// </summary>
public class SegmentShiftGeometryTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void GetHandles_ZPath_ReturnsOnlyTheMiddleStraight()
    {
        var segments = ZPath().Segments;

        var handles = SegmentShiftGeometry.GetHandles(segments);

        var handle = handles.ShouldHaveSingleItem();
        handle.StraightIndex.ShouldBe(1); // pin-adjacent straights 0 and 2 are not shiftable
        handle.Midpoint.X.ShouldBe(60, Tolerance);
        handle.Midpoint.Y.ShouldBe(30, Tolerance);
        handle.Direction.X.ShouldBe(0, Tolerance);
        handle.Direction.Y.ShouldBe(1, Tolerance);
        handle.Normal.X.ShouldBe(-1, Tolerance);
        handle.Normal.Y.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void GetHandles_SingleCornerPath_HasNoShiftableStraight()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(60, 10, 60, 60, 90));

        SegmentShiftGeometry.GetHandles(path.Segments).ShouldBeEmpty();
    }

    [Fact]
    public void ProjectOffset_IgnoresMovementAlongTheSegment()
    {
        var handle = SegmentShiftGeometry.GetHandles(ZPath().Segments)[0];

        // Pointer moved 7 µm along the normal and 25 µm along the segment: only the
        // perpendicular component counts.
        var pointer = (handle.Midpoint.X + 7 * handle.Normal.X + 25 * handle.Direction.X,
                       handle.Midpoint.Y + 7 * handle.Normal.Y + 25 * handle.Direction.Y);

        SegmentShiftGeometry.ProjectOffset(handle, pointer).ShouldBe(7, Tolerance);
    }

    [Fact]
    public void GetHandles_DiagonalMiddleSegment_UsesTheFortyFiveDegreeNormal()
    {
        var handles = SegmentShiftGeometry.GetHandles(DiagonalPath().Segments);

        var handle = handles.ShouldHaveSingleItem();
        double s2 = Math.Sqrt(2) / 2;
        handle.Direction.X.ShouldBe(s2, Tolerance);
        handle.Direction.Y.ShouldBe(s2, Tolerance);
        handle.Normal.X.ShouldBe(-s2, Tolerance);
        handle.Normal.Y.ShouldBe(s2, Tolerance);
    }

    [Fact]
    public void IsShiftable_ParallelOuterStraight_IsRejected()
    {
        // A U-turn: both outer straights run parallel to the middle one, so no travel along
        // them can produce a perpendicular displacement.
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 0, 40, 90));
        path.Segments.Add(new BendSegment(10, 40, 10, 90, -90));
        path.Segments.Add(new StraightSegment(10, 50, 10, 55, 90));
        path.Segments.Add(new BendSegment(20, 55, 10, 90, -90));
        path.Segments.Add(new StraightSegment(20, 65, 20, 100, 90));

        // IsShiftable only inspects segment kinds and angles, so exact arc endpoints are moot.
        SegmentShiftGeometry.IsShiftable(path.Segments, 2).ShouldBeFalse();
    }

    /// <summary>Z-shaped path: east 50 µm, 90° left bend (r=10), north 40 µm, 90° right bend
    /// (r=10), east 50 µm. The middle straight runs (60,10)→(60,50).</summary>
    internal static RoutedPath ZPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(60, 10, 60, 50, 90));
        path.Segments.Add(new BendSegment(70, 50, 10, 90, -90));
        path.Segments.Add(new StraightSegment(70, 60, 120, 60, 0));
        return path;
    }

    /// <summary>Path whose middle straight runs at 45°: east, 45° left bend, diagonal,
    /// 45° right bend, east again.</summary>
    internal static RoutedPath DiagonalPath()
    {
        const double Radius = 10;
        double s2 = Math.Sqrt(2) / 2;

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 40, 0, 0));
        var bend1 = new BendSegment(40, Radius, Radius, 0, 45);
        path.Segments.Add(bend1);
        var start = bend1.EndPoint;
        (double X, double Y) end = (start.X + 20 * s2, start.Y + 20 * s2);
        path.Segments.Add(new StraightSegment(start.X, start.Y, end.X, end.Y, 45));
        var bend2 = new BendSegment(end.X + Radius * s2, end.Y - Radius * s2, Radius, 45, -45);
        path.Segments.Add(bend2);
        path.Segments.Add(new StraightSegment(bend2.EndPoint.X, bend2.EndPoint.Y, 120, bend2.EndPoint.Y, 0));
        return path;
    }
}
