using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MetalRouting;

/// <summary>
/// Tests for <see cref="WaveguideCrossingDetector"/> — chord-based intersection of
/// metal trace segments with optical waveguide paths (issue #682).
/// </summary>
public class WaveguideCrossingDetectorTests
{
    [Fact]
    public void FindCrossings_PerpendicularSegments_ReturnsIntersectionPoint()
    {
        // Metal runs horizontally through (50, 50); waveguide runs vertically through it.
        var metal = new List<PathSegment> { new StraightSegment(0, 50, 100, 50, 0) };
        var optical = new List<IReadOnlyList<PathSegment>>
        {
            new List<PathSegment> { new StraightSegment(50, 0, 50, 100, 90) }
        };

        var crossings = WaveguideCrossingDetector.FindCrossings(metal, optical);

        crossings.Count.ShouldBe(1);
        crossings[0].X.ShouldBe(50, 0.001);
        crossings[0].Y.ShouldBe(50, 0.001);
    }

    [Fact]
    public void FindCrossings_ParallelSegments_ReturnsEmpty()
    {
        var metal = new List<PathSegment> { new StraightSegment(0, 0, 100, 0, 0) };
        var optical = new List<IReadOnlyList<PathSegment>>
        {
            new List<PathSegment> { new StraightSegment(0, 10, 100, 10, 0) }
        };

        var crossings = WaveguideCrossingDetector.FindCrossings(metal, optical);

        crossings.ShouldBeEmpty();
    }

    [Fact]
    public void FindCrossings_NonOverlappingSegments_ReturnsEmpty()
    {
        // Lines would intersect if extended, but the segments do not overlap.
        var metal = new List<PathSegment> { new StraightSegment(0, 50, 40, 50, 0) };
        var optical = new List<IReadOnlyList<PathSegment>>
        {
            new List<PathSegment> { new StraightSegment(50, 0, 50, 100, 90) }
        };

        var crossings = WaveguideCrossingDetector.FindCrossings(metal, optical);

        crossings.ShouldBeEmpty();
    }

    [Fact]
    public void FindCrossings_EndpointTouch_IsNotACrossing()
    {
        // The waveguide ends exactly on the metal trace — a touch, not a crossing.
        var metal = new List<PathSegment> { new StraightSegment(0, 50, 100, 50, 0) };
        var optical = new List<IReadOnlyList<PathSegment>>
        {
            new List<PathSegment> { new StraightSegment(50, 0, 50, 50, 90) }
        };

        var crossings = WaveguideCrossingDetector.FindCrossings(metal, optical);

        crossings.ShouldBeEmpty();
    }

    [Fact]
    public void FindCrossings_MultipleWaveguides_ReturnsAllCrossings()
    {
        var metal = new List<PathSegment> { new StraightSegment(0, 50, 200, 50, 0) };
        var optical = new List<IReadOnlyList<PathSegment>>
        {
            new List<PathSegment> { new StraightSegment(50, 0, 50, 100, 90) },
            new List<PathSegment> { new StraightSegment(150, 0, 150, 100, 90) }
        };

        var crossings = WaveguideCrossingDetector.FindCrossings(metal, optical);

        crossings.Count.ShouldBe(2);
        crossings.ShouldContain(c => Math.Abs(c.X - 50) < 0.001);
        crossings.ShouldContain(c => Math.Abs(c.X - 150) < 0.001);
    }

    [Fact]
    public void FindCrossings_NoOpticalPaths_ReturnsEmpty()
    {
        var metal = new List<PathSegment> { new StraightSegment(0, 0, 100, 0, 0) };

        var crossings = WaveguideCrossingDetector.FindCrossings(
            metal, Enumerable.Empty<IReadOnlyList<PathSegment>>());

        crossings.ShouldBeEmpty();
    }
}
