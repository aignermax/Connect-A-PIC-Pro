using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// Tests that a Cut-tool candidate's crossing footprint is actually checked against the
/// pathfinding grid — mirroring the adaptive pass's <see cref="CrossingInserter"/> bounding-box
/// guard, which the original manual-crossing candidate search skipped entirely. Applies to
/// both the guide-based and the free-cut candidate path, since both fall through
/// <see cref="ManualCrossingCandidateFinder.TryCreateFreeCandidate"/> or the equivalent guard
/// inside <see cref="ManualCrossingCandidateFinder.FindCandidates"/>.
/// </summary>
public class ManualCrossingFootprintClearanceTests
{
    private const double RequiredRunMicrometers = 10.0;
    private const double CrossingHalfExtentMicrometers = 5.0;

    private readonly ManualCrossingCandidateFinder _finder = new();

    [Fact]
    public void TryCreateFreeCandidate_RejectsWhenNeighborComponentBlocksFootprint()
    {
        var connection = CreateHorizontalConnection(); // (10,100) -> (390,100)
        var blocker = CrossingTestCircuit.CreateTerminal("blocker", 195, 95, pinAngleDegrees: 0);
        var footprint = BuildFootprint(new[] { blocker.Component });

        var segment = (StraightSegment)connection.GetPathSegments()[0];
        var candidate = _finder.TryCreateFreeCandidate(
            connection, segment, (200.0, 100.0), RequiredRunMicrometers, footprint);

        candidate.ShouldBeNull("the crossing's bounding box would overlap the blocking component");
    }

    [Fact]
    public void TryCreateFreeCandidate_AcceptsWhenAreaIsClear()
    {
        var connection = CreateHorizontalConnection();
        var footprint = BuildFootprint(Array.Empty<Component>());

        var segment = (StraightSegment)connection.GetPathSegments()[0];
        var candidate = _finder.TryCreateFreeCandidate(
            connection, segment, (200.0, 100.0), RequiredRunMicrometers, footprint);

        candidate.ShouldNotBeNull();
    }

    [Fact]
    public void FindCandidates_RejectsGuideCandidateWhenNeighborComponentBlocksFootprint()
    {
        var connection = CreateHorizontalConnection();
        var guidePin = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        var blocker = CrossingTestCircuit.CreateTerminal("blocker", 195, 95, pinAngleDegrees: 0);
        var guides = _finder.BuildGuideLines(new[] { guidePin.PhysicalPin });
        var footprint = BuildFootprint(new[] { blocker.Component });

        var candidates = _finder.FindCandidates(
            guides, new[] { connection }, RequiredRunMicrometers, footprint);

        candidates.ShouldBeEmpty("the same bounding-box guard must gate guide-based candidates too");
    }

    [Fact]
    public void FindCandidates_AcceptsGuideCandidateWhenAreaIsClear()
    {
        var connection = CreateHorizontalConnection();
        var guidePin = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        var guides = _finder.BuildGuideLines(new[] { guidePin.PhysicalPin });
        var footprint = BuildFootprint(Array.Empty<Component>());

        var candidates = _finder.FindCandidates(
            guides, new[] { connection }, RequiredRunMicrometers, footprint);

        candidates.ShouldHaveSingleItem();
    }

    /// <summary>Horizontal net (10,100)→(390,100) with an injected straight route.</summary>
    private static WaveguideConnection CreateHorizontalConnection()
    {
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var connection = new WaveguideConnection { StartPin = left.PhysicalPin, EndPin = right.PhysicalPin };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(10, 100, 390, 100, 0));
        connection.RestoreCachedPath(path);
        return connection;
    }

    private static FootprintClearance BuildFootprint(IReadOnlyCollection<Component> obstacles)
    {
        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0, AStarCellSize = 4.0 };
        router.InitializePathfindingGrid(0, 0, 400, 400, obstacles);
        return new FootprintClearance(router.PathfindingGrid!, CrossingHalfExtentMicrometers);
    }
}
