using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Tests for <see cref="PathIntersectionDetector"/>: self-intersection of a single routed
/// path and minimum clearance between two routed paths.
/// </summary>
public class PathIntersectionDetectorTests
{
    [Fact]
    public void HasSelfIntersection_SimpleLShape_ReturnsFalse()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        path.Segments.Add(new StraightSegment(100, 0, 100, 100, 90));

        PathIntersectionDetector.HasSelfIntersection(path).ShouldBeFalse();
    }

    [Fact]
    public void HasSelfIntersection_PathCrossingItself_ReturnsTrue()
    {
        // The last segment cuts back through the first one.
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        path.Segments.Add(new StraightSegment(100, 0, 50, 50, 135));
        path.Segments.Add(new StraightSegment(50, 50, 50, -50, 270));

        PathIntersectionDetector.HasSelfIntersection(path).ShouldBeTrue();
    }

    [Fact]
    public void HasSelfIntersection_LoopOfArcAndExit_ReturnsTrue()
    {
        // Straight lead-in, 300° arc, and an exit straight that cuts back through the
        // lead-in — the teardrop loop shape the CSC fallback produced at tight spacing.
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        var loop = new BendSegment(50, 30, 30, 0, 300);
        path.Segments.Add(loop);
        double exitRad = 300 * Math.PI / 180.0;
        path.Segments.Add(new StraightSegment(
            loop.EndPoint.X, loop.EndPoint.Y,
            loop.EndPoint.X + 40 * Math.Cos(exitRad), loop.EndPoint.Y + 40 * Math.Sin(exitRad),
            300));

        PathIntersectionDetector.HasSelfIntersection(path).ShouldBeTrue();
    }

    [Fact]
    public void MinimumDistance_ParallelStraights_ReturnsTheirSpacing()
    {
        var first = new RoutedPath();
        first.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        var second = new RoutedPath();
        second.Segments.Add(new StraightSegment(0, 5, 100, 5, 0));

        PathIntersectionDetector.MinimumDistance(first, second).ShouldBe(5.0, 1e-9);
    }

    [Fact]
    public void MinimumDistance_CrossingPaths_ReturnsZero()
    {
        var first = new RoutedPath();
        first.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        var second = new RoutedPath();
        second.Segments.Add(new StraightSegment(50, -50, 50, 50, 90));

        PathIntersectionDetector.MinimumDistance(first, second).ShouldBe(0.0);
    }

    [Fact]
    public void MinimumDistance_OverlappingCollinearPaths_ReturnsZero()
    {
        // Two routes lying on top of each other (the CSC-fallback symptom).
        var first = new RoutedPath();
        first.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        var second = new RoutedPath();
        second.Segments.Add(new StraightSegment(20, 0, 80, 0, 0));

        PathIntersectionDetector.MinimumDistance(first, second).ShouldBe(0.0);
    }
}
