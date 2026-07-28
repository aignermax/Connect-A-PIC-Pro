using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;
using CAP_Core.Tiles;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// Tests for the Cut-tool candidate finder: guide lines from
/// axis-aligned optical pins and their perpendicular intersections with
/// straight waveguide segments.
/// </summary>
public class ManualCrossingCandidateFinderTests
{
    private const double RequiredRunMicrometers = 10.0;

    private readonly ManualCrossingCandidateFinder _finder = new();

    [Fact]
    public void BuildGuideLines_CardinalPins_ProduceGuidesWithOriginAndDirection()
    {
        var east = CrossingTestCircuit.CreateTerminal("east", 0, 95, pinAngleDegrees: 0);
        var south = CrossingTestCircuit.CreateTerminal("south", 195, 40, pinAngleDegrees: 90);

        var guides = _finder.BuildGuideLines(new[] { east.PhysicalPin, south.PhysicalPin });

        guides.Count.ShouldBe(2);
        guides[0].Origin.ShouldBe((10.0, 100.0));
        guides[0].Direction.ShouldBe((1.0, 0.0));
        guides[0].IsHorizontal.ShouldBeTrue();
        guides[1].Origin.ShouldBe((200.0, 50.0));
        guides[1].Direction.ShouldBe((0.0, 1.0));
        guides[1].IsHorizontal.ShouldBeFalse();
    }

    [Fact]
    public void BuildGuideLines_NonCardinalPin_IsSkipped()
    {
        var terminal = CrossingTestCircuit.CreateTerminal("diag", 0, 0, pinAngleDegrees: 0);
        terminal.PhysicalPin.AngleDegrees = 45;

        var guides = _finder.BuildGuideLines(new[] { terminal.PhysicalPin });

        guides.ShouldBeEmpty();
    }

    [Fact]
    public void BuildGuideLines_ElectricalPin_IsSkipped()
    {
        var terminal = CrossingTestCircuit.CreateTerminal("el", 0, 0, pinAngleDegrees: 0);
        terminal.PhysicalPin.LogicalPin =
            new Pin("el", 0, MatterType.Electricity, RectSide.Right);

        var guides = _finder.BuildGuideLines(new[] { terminal.PhysicalPin });

        guides.ShouldBeEmpty();
    }

    [Fact]
    public void FindCandidates_PerpendicularIntersection_IsFound()
    {
        var connection = CreateHorizontalConnection();
        var guidePin = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        var guides = _finder.BuildGuideLines(new[] { guidePin.PhysicalPin });

        var candidates = _finder.FindCandidates(guides, new[] { connection }, RequiredRunMicrometers);

        var candidate = candidates.ShouldHaveSingleItem();
        candidate.IntersectionPoint.ShouldBe((200.0, 100.0));
        candidate.SegmentIsHorizontal.ShouldBeTrue();
        candidate.SegmentDirection.X.ShouldBe(1.0, 1e-9);
        candidate.SegmentDirection.Y.ShouldBe(0.0, 1e-9);
        candidate.Connection.ShouldBeSameAs(connection);
    }

    [Fact]
    public void FindCandidates_GuideFromConnectionsOwnEndpointPin_IsSkipped()
    {
        // L-shaped path: horizontal run at y=100, then vertical up to the end
        // pin at (200, 20) facing south. Its guide ray hits the horizontal
        // segment's interior at (200, 100) but must be skipped as self-cut.
        var start = CrossingTestCircuit.CreateTerminal("start", 0, 95, pinAngleDegrees: 0);
        var end = CrossingTestCircuit.CreateTerminal("end", 195, 10, pinAngleDegrees: 90);
        var connection = CreateRoutedConnection(start.PhysicalPin, end.PhysicalPin,
            new StraightSegment(10, 100, 390, 100, 0),
            new StraightSegment(200, 100, 200, 20, 270));
        var guides = _finder.BuildGuideLines(new[] { end.PhysicalPin });

        var candidates = _finder.FindCandidates(guides, new[] { connection }, RequiredRunMicrometers);

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public void FindCandidates_InsufficientStraightRunAroundIntersection_IsRejected()
    {
        // Intersection at (15, 100) leaves only 5 µm to the segment start.
        var connection = CreateHorizontalConnection();
        var guidePin = CrossingTestCircuit.CreateTerminal("guide", 10, 40, pinAngleDegrees: 90);
        var guides = _finder.BuildGuideLines(new[] { guidePin.PhysicalPin });

        var candidates = _finder.FindCandidates(guides, new[] { connection }, RequiredRunMicrometers);

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public void FindCandidates_IntersectionTooCloseToGuideOrigin_IsRejected()
    {
        // Guide pin at (200, 95) is only 5 µm above the waveguide — the
        // crossing body would overlap the guide component.
        var connection = CreateHorizontalConnection();
        var guidePin = CrossingTestCircuit.CreateTerminal("guide", 195, 85, pinAngleDegrees: 90);
        var guides = _finder.BuildGuideLines(new[] { guidePin.PhysicalPin });

        var candidates = _finder.FindCandidates(guides, new[] { connection }, RequiredRunMicrometers);

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public void FindCandidates_TwoGuidesHittingSameSpot_AreDeduplicated()
    {
        var connection = CreateHorizontalConnection();
        var above = CrossingTestCircuit.CreateTerminal("above", 195, 40, pinAngleDegrees: 90);
        var below = CrossingTestCircuit.CreateTerminal("below", 195, 350, pinAngleDegrees: 270);
        var guides = _finder.BuildGuideLines(new[] { above.PhysicalPin, below.PhysicalPin });

        var candidates = _finder.FindCandidates(guides, new[] { connection }, RequiredRunMicrometers);

        candidates.ShouldHaveSingleItem();
    }

    [Fact]
    public void FindCandidates_ParallelGuide_ProducesNoCandidate()
    {
        var connection = CreateHorizontalConnection();
        var guidePin = CrossingTestCircuit.CreateTerminal("guide", 0, 195, pinAngleDegrees: 0);
        var guides = _finder.BuildGuideLines(new[] { guidePin.PhysicalPin });

        var candidates = _finder.FindCandidates(guides, new[] { connection }, RequiredRunMicrometers);

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public void FindCandidates_ElectricalConnection_IsSkipped()
    {
        var start = CrossingTestCircuit.CreateTerminal("start", 0, 95, pinAngleDegrees: 0);
        var end = CrossingTestCircuit.CreateTerminal("end", 390, 95, pinAngleDegrees: 180);
        start.PhysicalPin.LogicalPin = new Pin("el", 0, MatterType.Electricity, RectSide.Right);
        end.PhysicalPin.LogicalPin = new Pin("el", 1, MatterType.Electricity, RectSide.Left);
        var connection = CreateRoutedConnection(start.PhysicalPin, end.PhysicalPin,
            new StraightSegment(10, 100, 390, 100, 0));
        var guidePin = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        var guides = _finder.BuildGuideLines(new[] { guidePin.PhysicalPin });

        var candidates = _finder.FindCandidates(guides, new[] { connection }, RequiredRunMicrometers);

        candidates.ShouldBeEmpty();
    }

    /// <summary>Horizontal net (10,100)→(390,100) with an injected straight route.</summary>
    private static WaveguideConnection CreateHorizontalConnection()
    {
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        return CreateRoutedConnection(left.PhysicalPin, right.PhysicalPin,
            new StraightSegment(10, 100, 390, 100, 0));
    }

    private static WaveguideConnection CreateRoutedConnection(
        PhysicalPin start, PhysicalPin end, params StraightSegment[] segments)
    {
        var connection = new WaveguideConnection { StartPin = start, EndPin = end };
        var path = new RoutedPath();
        path.Segments.AddRange(segments);
        connection.RestoreCachedPath(path);
        return connection;
    }
}
