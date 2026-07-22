using CAP_Core.Analysis;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Tests that the waveguide router treats frozen group paths as obstacles.
/// Issue #353: Router should proactively avoid frozen waveguide paths during A* pathfinding.
/// </summary>
[Trait("Category", "Slow")]
public class FrozenPathObstacleTests
{
    private const double CellSize = 4.0;
    private const double MinBendRadius = 10.0;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a minimal component at the given position for use as a pin parent.
    /// </summary>
    private static Component CreateComponent(double x, double y, double w = 20, double h = 20)
    {
        var c = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test", "",
            new Part[1, 1] { { new Part() } },
            -1,
            Guid.NewGuid().ToString(),
            new DiscreteRotation(),
            new List<PhysicalPin>())
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = w,
            HeightMicrometers = h
        };
        return c;
    }

    /// <summary>
    /// Builds a ComponentGroup with a single horizontal frozen path at y=frozenY
    /// spanning x=[fromX, toX].  Two tiny child components serve as path endpoints.
    /// </summary>
    private static ComponentGroup CreateGroupWithHorizontalFrozenPath(
        double fromX, double toX, double frozenY)
    {
        var childA = CreateComponent(fromX - 5, frozenY - 5);
        var childB = CreateComponent(toX, frozenY - 5);

        var group = new ComponentGroup("TestFrozenGroup");
        group.AddChild(childA);
        group.AddChild(childB);

        var segment = new StraightSegment(fromX, frozenY, toX, frozenY, angleDegrees: 0);
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(segment);

        var frozenPath = new FrozenWaveguidePath
        {
            Path = routedPath,
            StartPin = new PhysicalPin
            {
                Name = "startPin",
                ParentComponent = childA,
                OffsetXMicrometers = 5,
                OffsetYMicrometers = 5,
                AngleDegrees = 0
            },
            EndPin = new PhysicalPin
            {
                Name = "endPin",
                ParentComponent = childB,
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 5,
                AngleDegrees = 180
            }
        };
        group.AddInternalPath(frozenPath);
        group.UpdateGroupBounds();

        return group;
    }

    /// <summary>
    /// Checks whether any sampled point along the routed path passes through a
    /// state-3 (frozen path) cell in the grid.
    /// </summary>
    private static bool PathPassesThroughFrozenCells(
        IEnumerable<PathSegment> segments, PathfindingGrid grid)
    {
        foreach (var segment in segments)
        {
            if (segment is not StraightSegment straight) continue;

            double dx = straight.EndPoint.X - straight.StartPoint.X;
            double dy = straight.EndPoint.Y - straight.StartPoint.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001) continue;

            double nx = dx / len;
            double ny = dy / len;
            double step = CellSize * 0.5;

            for (double t = step; t < len - step; t += step)
            {
                double px = straight.StartPoint.X + nx * t;
                double py = straight.StartPoint.Y + ny * t;
                var (gx, gy) = grid.PhysicalToGrid(px, py);
                if (grid.GetCellState(gx, gy) == 3)
                    return true;
            }
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Grid marking tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Cells along a group's frozen straight path must be state=3 after adding the obstacle.
    /// </summary>
    [Fact]
    public void AddGroupObstacle_FrozenStraightPath_CellsAreState3()
    {
        // Arrange: frozen path at y=50 from x=10 to x=90
        var group = CreateGroupWithHorizontalFrozenPath(10, 90, 50);
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0, padding: 0);

        // Act
        grid.AddComponentObstacle(group);

        // Assert: sample the midpoint of the frozen path
        var (gx, gy) = grid.PhysicalToGrid(50, 50);
        grid.GetCellState(gx, gy).ShouldBe((byte)3,
            $"Midpoint of frozen path at physical(50,50) / grid({gx},{gy}) should be state=3");
    }

    /// <summary>
    /// Cells along a frozen bend segment must also be state=3.
    /// </summary>
    [Fact]
    public void AddGroupObstacle_FrozenBend_CellsAreState3()
    {
        // Arrange: 90° bend centred at (50,50) radius=10
        var child = CreateComponent(30, 30);
        var group = new ComponentGroup("BendGroup");
        group.AddChild(child);

        var bend = new BendSegment(50, 50, radius: 10, startAngle: 0, sweepAngle: 90);
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(bend);

        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = routedPath,
            StartPin = new PhysicalPin
            {
                Name = "startPin",
                ParentComponent = child,
                OffsetXMicrometers = 10,
                OffsetYMicrometers = 10,
                AngleDegrees = 0
            }
        });
        group.UpdateGroupBounds();

        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0, padding: 0);

        // Act
        grid.AddComponentObstacle(group);

        // Assert: a point near the bend start (50, 40) — on the arc at 0° — should be blocked
        var (gx, gy) = grid.PhysicalToGrid(50, 40);
        grid.IsBlocked(gx, gy).ShouldBeTrue(
            "Cell near frozen bend should be blocked (state=3)");
    }

    /// <summary>
    /// Free cells outside the frozen path should remain state=0.
    /// </summary>
    [Fact]
    public void AddGroupObstacle_FrozenPath_DoesNotBlockUnrelatedCells()
    {
        // Arrange: frozen path only at y=50
        var group = CreateGroupWithHorizontalFrozenPath(10, 90, 50);
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0, padding: 0);

        // Act
        grid.AddComponentObstacle(group);

        // Assert: cell at y=10 (far from frozen path) is free
        var (gx, gy) = grid.PhysicalToGrid(50, 10);
        grid.GetCellState(gx, gy).ShouldBe((byte)0,
            "Cell away from frozen path should stay free");
    }

    /// <summary>
    /// Removing the group obstacle must clear its frozen path cells (state=3 → 0).
    /// </summary>
    [Fact]
    public void RemoveGroupObstacle_ClearsFrozenPathCells()
    {
        // Arrange
        var group = CreateGroupWithHorizontalFrozenPath(10, 90, 50);
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0, padding: 0);
        grid.AddComponentObstacle(group);

        var (gx, gy) = grid.PhysicalToGrid(50, 50);
        grid.GetCellState(gx, gy).ShouldBe((byte)3, "Should be state=3 before removal");

        // Act
        grid.RemoveComponentObstacle(group);

        // Assert
        grid.GetCellState(gx, gy).ShouldBe((byte)0,
            "Frozen path cells should be freed after group removal");
    }

    /// <summary>
    /// ClearPinCorridor must NOT clear frozen path cells (state=3 is permanent).
    /// </summary>
    [Fact]
    public void ClearPinCorridor_DoesNotClearFrozenPathCells()
    {
        // Arrange: frozen path at y=50, corridor clears along y=50
        var group = CreateGroupWithHorizontalFrozenPath(10, 90, 50);
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0, padding: 0);
        grid.AddComponentObstacle(group);

        // Act: clear a corridor along the frozen path
        grid.ClearPinCorridor(pinX: 50, pinY: 50, angleDegrees: 0,
                              corridorLength: 80, corridorWidth: 4);

        // Assert: frozen cells remain
        var (gx, gy) = grid.PhysicalToGrid(50, 50);
        grid.GetCellState(gx, gy).ShouldBe((byte)3,
            "ClearPinCorridor must not overwrite frozen path cells (state=3)");
    }

    // -----------------------------------------------------------------------
    // Router avoidance tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// The A* router must route around a frozen straight path that blocks the
    /// direct route, and the returned path must not pass through state=3 cells.
    /// </summary>
    [Fact]
    public void Router_AvoidsFrozenStraightPath_PathDoesNotCrossBarrier()
    {
        // Arrange ─────────────────────────────────────────────────────────
        // Frozen horizontal path at y=100 from x=30 to x=170.
        // This forms a partial wall; A* can detour via x<30 or x>170.
        //
        //  Grid: (-50,-50) → (250, 250)  cellSize=4
        //
        //  compC pin:  (100, 60)  angle=270° (exits south)
        //  compD pin:  (100, 140) angle=90°  (exits north → endInputAngle=270)
        //  Frozen path: y=100, x=30..170  (directly between the two pins)
        // ─────────────────────────────────────────────────────────────────

        var group = CreateGroupWithHorizontalFrozenPath(30, 170, 100);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = MinBendRadius,
            MinWaveguideSpacingMicrometers = 2.0,
            AStarCellSize = CellSize
        };
        router.InitializePathfindingGrid(-50, -50, 250, 250, new Component[] { group }, CellSize);

        var compC = CreateComponent(85, 10);
        var compD = CreateComponent(85, 130);

        var startPin = new PhysicalPin
        {
            Name = "out",
            ParentComponent = compC,
            OffsetXMicrometers = 15,  // abs x = 85+15 = 100
            OffsetYMicrometers = 20,  // abs y = 10+20 = 30 → too close; adjust below
            AngleDegrees = 270        // south
        };

        // Place start pin at (100, 60): compC.PhysicalY=40, offset=(15,20)
        compC.PhysicalY = 40;   // abs y = 40+20 = 60  ✓

        var endPin = new PhysicalPin
        {
            Name = "in",
            ParentComponent = compD,
            OffsetXMicrometers = 15,  // abs x = 100
            OffsetYMicrometers = 0,   // abs y = 130+0 = 130  (just below path)
            AngleDegrees = 90         // north  → endInputAngle = 270
        };
        compD.PhysicalY = 140;  // abs y = 140+0 = 140  ✓
        endPin.OffsetYMicrometers = 0;

        // Act
        var path = router.Route(startPin, endPin);

        // Assert
        path.ShouldNotBeNull();
        path.Segments.ShouldNotBeEmpty("Router should produce a path");

        // The path must not pass through any frozen-path cell
        bool crossesFrozen = PathPassesThroughFrozenCells(
            path.Segments, router.PathfindingGrid!);
        crossesFrozen.ShouldBeFalse(
            "Routed path must not traverse frozen path cells (state=3)");
    }

    // -----------------------------------------------------------------------
    // Integration test: C→D scenario from the issue description
    // -----------------------------------------------------------------------

    /// <summary>
    /// Full C→D scenario:
    ///   1. Route A→B, freeze as group.
    ///   2. Route C→D — router must avoid the frozen A→B path.
    ///   3. The C→D path must not cross state=3 cells.
    /// </summary>
    [Fact]
    public void Integration_NewRoute_AvoidsExistingFrozenGroupPath()
    {
        // ── Layout ──────────────────────────────────────────────────────
        //  A ──────────── B   (frozen horizontal path at y=50)
        //        C              (above frozen path, pin pointing south)
        //        D              (below frozen path, pin pointing north)
        // ────────────────────────────────────────────────────────────────

        // Step 1 – simulate group with frozen A→B path
        var group = CreateGroupWithHorizontalFrozenPath(
            fromX: 20, toX: 180, frozenY: 50);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = MinBendRadius,
            MinWaveguideSpacingMicrometers = 2.0,
            AStarCellSize = CellSize
        };
        router.InitializePathfindingGrid(-50, -50, 300, 200, new Component[] { group }, CellSize);

        // Step 2 – set up C and D on opposite sides of the frozen path
        var compC = CreateComponent(x: 90, y: 0);   // above A→B path
        var compD = CreateComponent(x: 90, y: 90);  // below A→B path

        var pinC = new PhysicalPin
        {
            Name = "out",
            ParentComponent = compC,
            OffsetXMicrometers = 10,   // abs x=100
            OffsetYMicrometers = 20,   // abs y=20
            AngleDegrees = 270         // exits south
        };
        var pinD = new PhysicalPin
        {
            Name = "in",
            ParentComponent = compD,
            OffsetXMicrometers = 10,   // abs x=100
            OffsetYMicrometers = 0,    // abs y=90
            AngleDegrees = 90          // exits north → receives from south
        };

        // Act
        var path = router.Route(pinC, pinD);

        // Assert
        path.ShouldNotBeNull();
        path.Segments.ShouldNotBeEmpty("C→D route must produce segments");

        bool crossesFrozen = PathPassesThroughFrozenCells(
            path.Segments, router.PathfindingGrid!);
        crossesFrozen.ShouldBeFalse(
            "C→D route must not pass through frozen A→B path cells");
    }

    /// <summary>
    /// High-level integration test: Uses DesignValidator (same as "Run Design Checks" button)
    /// to verify that routes avoiding frozen paths don't trigger validation errors.
    /// This tests the user-facing workflow, not just grid internals.
    /// Issue #360: Tests should use DesignValidator instead of just segment counting.
    /// </summary>
    [Fact]
    public void DesignValidator_NewRoute_DoesNotReportOverlapWithFrozenPath()
    {
        // Arrange: Create group with frozen A→B path
        var group = CreateGroupWithHorizontalFrozenPath(
            fromX: 20, toX: 180, frozenY: 50);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = MinBendRadius,
            MinWaveguideSpacingMicrometers = 2.0,
            AStarCellSize = CellSize
        };
        router.InitializePathfindingGrid(-50, -50, 300, 200,
            new Component[] { group }, CellSize);

        // Create C and D on opposite sides of frozen path
        var compC = CreateComponent(x: 90, y: 0);   // above frozen path
        var compD = CreateComponent(x: 90, y: 90);  // below frozen path

        var pinC = new PhysicalPin
        {
            Name = "out",
            ParentComponent = compC,
            OffsetXMicrometers = 10,
            OffsetYMicrometers = 20,
            AngleDegrees = 270  // exits south
        };
        var pinD = new PhysicalPin
        {
            Name = "in",
            ParentComponent = compD,
            OffsetXMicrometers = 10,
            OffsetYMicrometers = 0,
            AngleDegrees = 90  // exits north
        };

        // Act: Route C→D (should avoid frozen A→B path)
        var path = router.Route(pinC, pinD);
        path.ShouldNotBeNull("Router should find a path avoiding frozen path");

        // Create WaveguideConnection for validation
        var connection = new WaveguideConnection
        {
            StartPin = pinC,
            EndPin = pinD
        };
        connection.RestoreCachedPath(path);

        // Use DesignValidator - same code path as "Run Design Checks" button!
        var validator = new DesignValidator();
        var issues = validator.Validate(
            new[] { connection },
            new[] { group });

        // Assert: No validation errors (especially no overlapping paths)
        issues.ShouldBeEmpty(
            "Router should avoid frozen paths - no validation errors expected");

        // Also verify specifically no overlap warnings
        var overlaps = issues.Where(i =>
            i.Type == DesignIssueType.OverlappingPaths).ToList();
        overlaps.ShouldBeEmpty(
            "C→D route must not trigger overlapping path warnings");
    }

    // -----------------------------------------------------------------------
    // Performance test
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialising the grid with 100 groups each carrying a frozen path
    /// must complete within a reasonable time limit (no algorithmic regression).
    /// </summary>
    [Fact]
    public void Performance_GridInitWith100FrozenPaths_CompletesUnder5Seconds()
    {
        // Arrange: 100 groups each with a short horizontal frozen path
        const int groupCount = 100;
        var groups = new List<ComponentGroup>(groupCount);

        for (int i = 0; i < groupCount; i++)
        {
            double y = 10 + i * 3.0; // y = 10, 13, 16, … 307
            groups.Add(CreateGroupWithHorizontalFrozenPath(10, 90, y));
        }

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = MinBendRadius,
            AStarCellSize = CellSize
        };

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        router.InitializePathfindingGrid(-50, -50, 150, 400,
            groups.Cast<Component>(), CellSize);
        sw.Stop();

        // Assert
        sw.ElapsedMilliseconds.ShouldBeLessThan(5_000,
            $"Grid init with {groupCount} frozen paths took {sw.ElapsedMilliseconds} ms — too slow");

        router.PathfindingGrid.ShouldNotBeNull();
        router.PathfindingGrid!.GetBlockedCellCount().ShouldBeGreaterThan(0,
            "Grid should have blocked cells after loading frozen path groups");
    }

}
