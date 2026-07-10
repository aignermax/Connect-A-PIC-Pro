using System.Numerics;
using CAP_Core.Components.Connections;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing.CrossingInsertion;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.CrossingInsertion;

/// <summary>
/// Tests for adaptive crossing insertion (Issue #553): when a detour is more
/// lossy than crossing straight through another waveguide, a real PDK crossing
/// component is placed at the intersection and both nets are split into
/// sub-connections docked at its ports.
/// </summary>
public class CrossingInsertionTests
{
    /// <summary>Bend loss that makes the detour clearly worse than one crossing (~0.18 dB).</summary>
    private const double ExpensiveBendLossDb = 0.5;

    /// <summary>Bend loss that makes the detour clearly cheaper than one crossing.</summary>
    private const double CheapBendLossDb = 0.0001;

    [Fact]
    public void OrthogonalDetour_InsertsOneCrossingAndFourSubConnections()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);

        layout.AddedCrossings.Count.ShouldBe(1, "exactly one crossing component must be placed");
        layout.Service.Records.Count.ShouldBe(1);
        layout.Manager.Connections.Count.ShouldBe(4, "both nets must be split into two sub-connections each");

        var record = layout.Service.Records[0];
        foreach (var sub in record.AllSubConnections)
        {
            layout.Manager.Connections.ShouldContain(sub);
            sub.IsPathValid.ShouldBeTrue("every sub-connection must dock cleanly at a crossing port");
            sub.IsBlockedFallback.ShouldBeFalse();
        }

        // The crossing must be centered on the (200, 100) intersection point.
        var crossing = layout.AddedCrossings[0];
        double centerX = crossing.PhysicalX + crossing.WidthMicrometers / 2.0;
        double centerY = crossing.PhysicalY + crossing.HeightMicrometers / 2.0;
        centerX.ShouldBe(200.0, tolerance: 6.0);
        centerY.ShouldBe(100.0, tolerance: 6.0);
    }

    [Fact]
    public async Task Simulation_ShowsThroughLossAndCrosstalkFromCrossingSMatrix()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        layout.AddedCrossings.Count.ShouldBe(1);

        var portManager = new PhysicalExternalPortManager();
        portManager.AddLightSource(
            new ExternalInput("src", LaserType.Red, 0, new Complex(1.0, 0)),
            layout.ALeft.LogicalPin.IDInFlow);

        var gridManager = GridManager.CreateForSimulation(layout.TileManager, layout.Manager, portManager);
        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        var fieldResults = await calculator.CalculateFieldPropagationAsync(new CancellationTokenSource(), 1550);

        // Through path: A_left → sub A1 → crossing (0.98) → sub A2 → A_right.
        // Normalize by the sub-connections' own (propagation + bend) transmission so
        // the assertion isolates the crossing's S-matrix contribution.
        var record = layout.Service.Records.ShouldHaveSingleItem();
        double horizontalWaveguideFactor = record.SubConnectionsA
            .Aggregate(1.0, (acc, sub) => acc * sub.TransmissionCoefficient.Magnitude);
        horizontalWaveguideFactor.ShouldBeGreaterThan(0);

        double through = fieldResults[layout.ARight.LogicalPin.IDInFlow].Magnitude;
        double crossingThrough = through / horizontalWaveguideFactor;
        crossingThrough.ShouldBeInRange(0.95, 0.99); // |S| = 0.98
        double crossingLossDb = -20.0 * Math.Log10(crossingThrough);
        crossingLossDb.ShouldBeInRange(0.1, 0.3); // ~0.18 dB through-loss

        // Crosstalk leaks into the vertical net's far terminal at ~0.02.
        var subToALeft = record.SubConnectionsA.Single(TouchesComponent(layout.ALeft.Component));
        var subToBBottom = record.SubConnectionsB.Single(TouchesComponent(layout.BBottom.Component));
        double crosstalkWaveguideFactor =
            subToALeft.TransmissionCoefficient.Magnitude * subToBBottom.TransmissionCoefficient.Magnitude;

        double crosstalk = fieldResults[layout.BBottom.LogicalPin.IDInFlow].Magnitude;
        (crosstalk / crosstalkWaveguideFactor).ShouldBeInRange(0.015, 0.025); // |S| = 0.02
    }

    [Fact]
    public void CheapDetour_KeepsAvoidanceAndInsertsNoCrossing()
    {
        var layout = CrossingTestCircuit.Build(CheapBendLossDb);

        layout.AddedCrossings.ShouldBeEmpty("a cheap detour must never be replaced by a crossing");
        layout.Service.Records.ShouldBeEmpty();
        layout.Manager.Connections.Count.ShouldBe(2);
        foreach (var connection in layout.Manager.Connections)
        {
            connection.IsPathValid.ShouldBeTrue();
            connection.IsBlockedFallback.ShouldBeFalse("the detour must be a real avoiding route");
        }
    }

    [Fact]
    public void NonOrthogonalIntersection_YieldsNoCandidate()
    {
        var grid = new PathfindingGrid(0, 0, 100, 100, 4.0, 5.0);
        var inserter = new CrossingInserter();

        // Diagonal cross (both segments at 45°): right angle, but not axis-aligned.
        var diagonalNew = CreateRoutedConnection(0, 0, 100, 100, angleDegrees: 45);
        var diagonalOther = CreateRoutedConnection(0, 100, 100, 0, angleDegrees: -45);
        var diagonalCandidate = inserter.FindCandidate(
            diagonalNew.Connection, diagonalNew.Path, new[] { diagonalOther.Connection },
            grid, CrossingTestCircuit.CrossingEdgeMicrometers, crossingLossDb: 0.18);
        diagonalCandidate.ShouldBeNull("the PDK crossing is strictly orthogonal — diagonal crossings must be rejected");

        // Control: the same setup axis-aligned produces a valid candidate.
        var horizontal = CreateRoutedConnection(0, 50, 100, 50, angleDegrees: 0);
        var vertical = CreateRoutedConnection(50, 0, 50, 100, angleDegrees: 90);
        var orthogonalCandidate = inserter.FindCandidate(
            horizontal.Connection, horizontal.Path, new[] { vertical.Connection },
            grid, CrossingTestCircuit.CrossingEdgeMicrometers, crossingLossDb: 0.18);
        orthogonalCandidate.ShouldNotBeNull();
        orthogonalCandidate.IntersectionPoint.X.ShouldBe(50.0, tolerance: 0.001);
        orthogonalCandidate.IntersectionPoint.Y.ShouldBe(50.0, tolerance: 0.001);
    }

    [Fact]
    public void RemovingCrossedConnection_DissolvesCrossingAndRestoresSurvivor()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        var record = layout.Service.Records.ShouldHaveSingleItem();
        var survivor = record.OriginalA;

        layout.Manager.RemoveConnection(record.SubConnectionsB[0]);

        layout.RemovedCrossings.ShouldContain(record.CrossingComponent,
            "the crossing component must not be orphaned");
        layout.Service.Records.ShouldBeEmpty();
        var remaining = layout.Manager.Connections.ShouldHaveSingleItem();
        remaining.ShouldBeSameAs(survivor);
        remaining.IsPathValid.ShouldBeTrue("the survivor must be re-routed unsplit");
    }

    [Fact]
    public void RemovingEndpointComponent_DissolvesCrossing()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        var record = layout.Service.Records.ShouldHaveSingleItem();

        layout.Manager.RemoveConnectionsForComponent(layout.BTop.Component);

        layout.RemovedCrossings.ShouldContain(record.CrossingComponent);
        layout.Service.Records.ShouldBeEmpty();
        var remaining = layout.Manager.Connections.ShouldHaveSingleItem();
        remaining.ShouldBeSameAs(record.OriginalA, "the untouched net must be restored unsplit");
    }

    /// <summary>Predicate: connection starts or ends on the given component.</summary>
    private static Func<WaveguideConnection, bool> TouchesComponent(CAP_Core.Components.Core.Component component) =>
        connection => connection.StartPin.ParentComponent == component ||
                      connection.EndPin.ParentComponent == component;

    /// <summary>
    /// Creates a connection whose routed path is a single straight segment,
    /// bypassing the router (for direct CrossingInserter tests).
    /// </summary>
    private static (WaveguideConnection Connection, RoutedPath Path) CreateRoutedConnection(
        double x1, double y1, double x2, double y2, double angleDegrees)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, angleDegrees));

        var start = CrossingTestCircuit.CreateTerminal($"t_{Guid.NewGuid():N}", x1, y1, 0);
        var end = CrossingTestCircuit.CreateTerminal($"t_{Guid.NewGuid():N}", x2, y2, 180);
        var connection = new WaveguideConnection
        {
            StartPin = start.PhysicalPin,
            EndPin = end.PhysicalPin,
        };
        connection.RestoreCachedPath(path);
        return (connection, path);
    }
}
