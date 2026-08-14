using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsPlacementExecutor"/>: exact-position placement (no
/// placement-search nudging, which would break GDS abutment), pre-placement
/// rotation, abutment connection reconstruction, grouping, and skip reporting.
/// Uses a real headless <see cref="DesignCanvasViewModel"/> like the canvas
/// command tests.
/// </summary>
public class GdsPlacementExecutorTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>10×4 µm two-port waveguide template ("wg" in "testpdk"), pins in/out.</summary>
    private static ComponentTemplate WaveguideTemplate() => new()
    {
        Name = "wg",
        Category = "Test",
        PdkSource = "testpdk",
        WidthMicrometers = 10,
        HeightMicrometers = 4,
        PinDefinitions = new[]
        {
            new PinDefinition("in", 0, 2, 180),
            new PinDefinition("out", 10, 2, 0),
        },
        CreateSMatrix = pins => new CAP_Core.LightCalculation.SMatrix(
            pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(),
            new List<(Guid, double)>()),
    };

    private static GdsPlacementInstruction Placement(
        string instanceName, double x, double y,
        string? identifier = "wg", string? pdkSource = "testpdk",
        double rotationDegrees = 0, string? warning = null) => new()
    {
        InstanceName = instanceName,
        ComponentIdentifier = identifier,
        PdkSource = identifier is null ? null : pdkSource,
        XUm = x,
        YUm = y,
        RotationDegrees = rotationDegrees,
        Warning = warning,
    };

    private static GdsConnectionInstruction Connection(
        int aIndex, string aPin, int bIndex, string bPin, string? note = null) => new()
    {
        A = new GdsConnectionEndpoint { InstanceIndex = aIndex, PinName = aPin },
        B = new GdsConnectionEndpoint { InstanceIndex = bIndex, PinName = bPin },
        Note = note,
    };

    private static (DesignCanvasViewModel canvas, CommandManager commands, GdsPlacementExecutor executor)
        CreateExecutor(params ComponentTemplate[] templates)
    {
        var canvas = new DesignCanvasViewModel();
        var commands = new CommandManager();
        var executor = new GdsPlacementExecutor(canvas, commands, () => templates);
        return (canvas, commands, executor);
    }

    /// <summary>The group is the only top-level canvas component after grouping.</summary>
    private static ComponentGroup SingleGroupOn(DesignCanvasViewModel canvas) =>
        canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();

    // ── Happy path: place → connect → group ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TwoAbuttingInstances_PlacesExactlyConnectsAndGroups()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", x: 0, y: 0),
                Placement("wgB#1", x: 10, y: 0), // abuts wgA's right edge — zero gap
            },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(2);
        report.ConnectedCount.ShouldBe(1);
        report.SkippedPlacements.ShouldBeEmpty();
        report.SkippedConnections.ShouldBeEmpty();
        report.GroupCreated.ShouldBeTrue();
        report.GroupName.ShouldBe("TOP");

        var group = SingleGroupOn(canvas);
        group.GroupName.ShouldBe("TOP");
        group.ChildComponents.Count.ShouldBe(2);

        // Exact positions: a nudging placement (PlaceComponentCommand.TryCreate)
        // would push the abutting instances apart (5 µm minimum gap rule).
        var ordered = group.ChildComponents.OrderBy(c => c.PhysicalX).ToList();
        ordered[0].PhysicalX.ShouldBe(0);
        ordered[0].PhysicalY.ShouldBe(0);
        ordered[1].PhysicalX.ShouldBe(10);
        ordered[1].PhysicalY.ShouldBe(0);

        // The abutment connection is frozen into the group with the right pins.
        var path = group.InternalPaths.ShouldHaveSingleItem();
        var pins = new[] { path.StartPin!.Name, path.EndPin!.Name };
        pins.ShouldBe(new[] { "out", "in" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ExecuteAsync_PlacementsAreUndoable()
    {
        var (canvas, commands, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        await executor.ExecuteAsync(plan);
        canvas.Components.ShouldNotBeEmpty();

        // Grouping is the last command; undoing the whole history empties the canvas.
        while (commands.CanUndo)
            commands.Undo();

        canvas.Components.ShouldBeEmpty();
    }

    // ── Free-space placement on a non-empty canvas ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_NonEmptyCanvas_ShiftsImportRightOfExistingContent_KeepingInternalSpacing()
    {
        var (canvas, commands, executor) = CreateExecutor(WaveguideTemplate());
        // Existing design: one waveguide at (100, 200), 10×4 µm → content bbox maxX = 110, minY = 200.
        commands.ExecuteCommand(PlaceComponentCommand.CreateExact(canvas, WaveguideTemplate(), 100, 200));
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", x: 0, y: 0), Placement("wgB#1", x: 10, y: 0) },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        // Import bbox min corner (0,0) → offset (110 + margin − 0, 200 − 0) = (160, 200):
        // right of the existing content with the margin, top-aligned with it.
        const double expectedOffsetX = 100 + 10 + GdsPlacementExecutor.ExistingContentMarginUm;
        const double expectedOffsetY = 200.0;

        var group = canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().ShouldHaveSingleItem();
        var ordered = group.ChildComponents.OrderBy(c => c.PhysicalX).ToList();
        ordered[0].PhysicalX.ShouldBe(expectedOffsetX);
        ordered[0].PhysicalY.ShouldBe(expectedOffsetY);
        (ordered[1].PhysicalX - ordered[0].PhysicalX).ShouldBe(10, 1e-9);
        (ordered[1].PhysicalY - ordered[0].PhysicalY).ShouldBe(0, 1e-9);

        // The pre-existing component was not touched.
        var seed = canvas.Components.Select(c => c.Component).First(c => c is not ComponentGroup);
        seed.PhysicalX.ShouldBe(100);
        seed.PhysicalY.ShouldBe(200);

        // The user hears about the shift ("+160" appears in every language's format string).
        report.Warnings.ShouldContain(w => w.Contains("+160"));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCanvas_KeepsExactGdsCoordinatesWithoutOffsetWarning()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", x: 5, y: 7), Placement("wgB#1", x: 15, y: 7) },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.Warnings.ShouldBeEmpty("no existing content → no placement offset");
        var group = SingleGroupOn(canvas);
        var ordered = group.ChildComponents.OrderBy(c => c.PhysicalX).ToList();
        ordered[0].PhysicalX.ShouldBe(5);
        ordered[0].PhysicalY.ShouldBe(7);
        ordered[1].PhysicalX.ShouldBe(15);
        ordered[1].PhysicalY.ShouldBe(7);
    }

    [Fact]
    public async Task ExecuteAsync_NonEmptyCanvas_FrozenRoutePathsShiftWithTheImportOffset()
    {
        var (canvas, commands, executor) = CreateExecutor(WaveguideTemplate());
        commands.ExecuteCommand(PlaceComponentCommand.CreateExact(canvas, WaveguideTemplate(), 100, 200));
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[] { Connection(0, "out", 1, "in") },
            TopCellWaveguidePolygons = new[]
            {
                new GdsOutlinePolygon
                {
                    Layer = 1,
                    Points = new GdsOutlinePoint[] { new(10, 2.25), new(12, 2.25), new(12, 1.75), new(10, 2.25) },
                },
            },
        };

        await executor.ExecuteAsync(plan);

        var group = canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().ShouldHaveSingleItem();
        var routePath = group.InternalPaths.Single(p => p.StartPin is null);
        // Plan-space (10, 2.25) + offset (160, 200): frozen paths hold absolute canvas coordinates.
        routePath.Path.Segments[0].StartPoint.X.ShouldBe(170, 1e-9);
        routePath.Path.Segments[0].StartPoint.Y.ShouldBe(202.25, 1e-9);
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RotatedInstance_RotatesModelAndKeepsExactTopLeft()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", x: 50, y: 60, rotationDegrees: 90) },
        };

        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(1);
        report.GroupCreated.ShouldBeFalse("a single component is not grouped");

        var component = canvas.Components.ShouldHaveSingleItem().Component;
        component.RotationDegrees.ShouldBe(90);
        component.WidthMicrometers.ShouldBe(4, "90° rotation swaps width and height");
        component.HeightMicrometers.ShouldBe(10);

        // (50,60) is the ROTATED bounding box's top-left — rotation must not shift it.
        component.PhysicalX.ShouldBe(50);
        component.PhysicalY.ShouldBe(60);

        // Pin offsets rotated 90° CCW around the center: out (10,2)→(2,10), in (0,2)→(2,0).
        var outPin = component.PhysicalPins.Single(p => p.Name == "out");
        outPin.OffsetXMicrometers.ShouldBe(2, 1e-9);
        outPin.OffsetYMicrometers.ShouldBe(10, 1e-9);
        var inPin = component.PhysicalPins.Single(p => p.Name == "in");
        inPin.OffsetXMicrometers.ShouldBe(2, 1e-9);
        inPin.OffsetYMicrometers.ShouldBe(0, 1e-9);
    }

    [Fact]
    public async Task ExecuteAsync_NonCardinalRotation_KeptExactlyWithoutSnapWarning()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0, rotationDegrees: 44) },
        };

        var report = await executor.ExecuteAsync(plan);

        report.Warnings.ShouldBeEmpty(
            "non-cardinal angles are placed exactly — the importer already warned per cell");
        canvas.Components.ShouldHaveSingleItem().Component.RotationDegrees.ShouldBe(44, 1e-9);
    }

    // ── Skips ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullIdentifierPlacement_IsSkippedAndReported()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0),
                Placement("blob#1", 30, 0, identifier: null, warning: "Cell 'blob' was not registered."),
                Placement("wgB#2", 10, 0),
            },
            Connections = new[] { Connection(0, "out", 2, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(2);
        report.SkippedPlacements.ShouldHaveSingleItem().ShouldContain("blob#1");
        report.ConnectedCount.ShouldBe(1, "the connection between the two placed instances still lands");

        var group = SingleGroupOn(canvas);
        group.ChildComponents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTemplate_IsSkippedAndReported()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("mmi#0", 0, 0, identifier: "mmi1x2", pdkSource: "otherpdk") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(0);
        report.SkippedPlacements.ShouldHaveSingleItem().ShouldContain("not in the library");
        canvas.Components.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TopLevelPortConnection_IsSkippedAndReported()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[]
            {
                Connection(-1, "o1", 0, "in", note: "involves a top-cell port of the imported circuit — left free in v1"),
                Connection(0, "out", 1, "in"),
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.ConnectedCount.ShouldBe(1);
        report.SkippedConnections.ShouldHaveSingleItem().ShouldContain("top-cell port");
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionToSkippedInstance_IsSkippedAndReported()
    {
        var (_, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0),
                Placement("blob#1", 10, 0, identifier: null),
            },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.ConnectedCount.ShouldBe(0);
        report.SkippedConnections.ShouldHaveSingleItem().ShouldContain("not placed");
    }

    // ── Per-instance line grouping ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RepeatedSkipReasons_GroupIntoOneLinePerDistinctMessage()
    {
        var (_, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0),
                Placement("blob#1", 30, 0, identifier: null, warning: "Cell 'blob' was not registered; this instance cannot be placed."),
                Placement("blob#2", 40, 0, identifier: null, warning: "Cell 'blob' was not registered; this instance cannot be placed."),
                Placement("blob#3", 50, 0, identifier: null, warning: "Cell 'blob' was not registered; this instance cannot be placed."),
                Placement("coal#4", 60, 0, identifier: null, warning: "Cell 'coal' was not registered; this instance cannot be placed."),
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(1);
        report.SkippedPlacements.Count.ShouldBe(2, "one grouped line per distinct message");
        report.SkippedPlacements[0].ShouldBe(
            "'blob#1': Cell 'blob' was not registered; this instance cannot be placed. — × 3 instances",
            "first example named, count appended");
        report.SkippedPlacements[1].ShouldBe(
            "'coal#4': Cell 'coal' was not registered; this instance cannot be placed.",
            "a single occurrence keeps the plain per-instance line (no suffix)");
    }

    [Fact]
    public async Task ExecuteAsync_MissingTemplates_GroupPerTemplate()
    {
        var (_, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("mmi#0", 0, 0, identifier: "mmi1x2", pdkSource: "otherpdk"),
                Placement("mmi#1", 30, 0, identifier: "mmi1x2", pdkSource: "otherpdk"),
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(0);
        report.SkippedPlacements.ShouldHaveSingleItem().ShouldBe(
            "'mmi#0': template 'mmi1x2' from PDK 'otherpdk' is not in the library. — × 2 instances");
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedInstanceWarnings_GroupIntoOneLine()
    {
        var (_, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0, warning: "something noteworthy"),
                Placement("wgA#1", 30, 0, warning: "something noteworthy"),
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.Warnings.ShouldHaveSingleItem().ShouldBe("'wgA#0': something noteworthy — × 2 instances");
    }

    [Fact]
    public async Task ExecuteAsync_IdenticalNonCardinalRotations_KeptExactlyWithoutWarnings()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0, rotationDegrees: 44),
                Placement("wgA#1", 30, 0, rotationDegrees: 44),
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.Warnings.ShouldBeEmpty("exact placement of a non-cardinal angle is not a caveat");
        SingleGroupOn(canvas).ChildComponents.Select(c => c.RotationDegrees)
            .ShouldAllBe(r => Math.Abs(r - 44) < 1e-9);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var (_, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0) },
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(plan, progress: null, cts.Token));
    }

    /// <summary>
    /// Cancels synchronously inside <see cref="IProgress{T}.Report"/> — unlike
    /// <see cref="Progress{T}"/>, which posts to the thread pool and would make
    /// the cancel timing nondeterministic in a headless test.
    /// </summary>
    private sealed class SyncCancelProgress(CancellationTokenSource cts) : IProgress<string>
    {
        public void Report(string value) => cts.Cancel();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidPlacement_PlacedCountSoFarReportsWhatLanded()
    {
        var (_, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0),
                Placement("wgB#1", 10, 0),
                Placement("wgC#2", 20, 0),
            },
        };
        using var cts = new CancellationTokenSource();
        // The first progress report fires BEFORE the first placement, so exactly
        // one placement lands before the next loop iteration's cancellation check.
        var progress = new SyncCancelProgress(cts);

        await Should.ThrowAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(plan, progress, cts.Token));

        executor.PlacedCountSoFar.ShouldBe(1,
            "the dialog's cancel message names this count — it must track placements live");
    }

    // ── Routing batching ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MultipleConnections_RecalculatesRoutesOnceForTheWholeBatch()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        // Count routing passes as IsRouting rising edges: RecalculateRoutesAsync
        // flips IsRouting true exactly once per pass, and both the executor's
        // awaited pass and the grouping command's fire-and-forget pass raise
        // their first StateChanged synchronously (the routing semaphore starts
        // free), so the count is settled when ExecuteAsync returns.
        var routingPasses = 0;
        var wasRouting = false;
        canvas.Routing.StateChanged += () =>
        {
            if (canvas.Routing.IsRouting && !wasRouting)
                routingPasses++;
            wasRouting = canvas.Routing.IsRouting;
        };
        // Spaced (non-abutting) instances: these connections carry no recovered
        // geometry, so they still need the router — the batching this test guards.
        // (Coincident abutments get frozen cached straights and never route, see
        // ExecuteAsync_CoincidentAbutments_UseCachedStraightRoutes.)
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0),
                Placement("wgB#1", 30, 0),
                Placement("wgC#2", 60, 0),
            },
            Connections = new[]
            {
                Connection(0, "out", 1, "in"),
                Connection(1, "out", 2, "in"),
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.ConnectedCount.ShouldBe(2);
        report.CachedRouteCount.ShouldBe(0);
        routingPasses.ShouldBe(2,
            "ONE batched routing pass covers both connections (plus one from " +
            "the grouping command) — the old per-connection ConnectPinsAsync re-routed " +
            "once per connection (O(N²))");
    }

    [Fact]
    public async Task ExecuteAsync_CoincidentAbutments_UseCachedStraightRoutes()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var routingPasses = 0;
        var wasRouting = false;
        canvas.Routing.StateChanged += () =>
        {
            if (canvas.Routing.IsRouting && !wasRouting)
                routingPasses++;
            wasRouting = canvas.Routing.IsRouting;
        };
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.ConnectedCount.ShouldBe(1);
        report.CachedRouteCount.ShouldBe(1);
        routingPasses.ShouldBe(1,
            "only the grouping command's pass runs — a coincident abutment gets the exact " +
            "pin-to-pin straight as a frozen cached route and never sees the router");

        var group = SingleGroupOn(canvas);
        var path = group.InternalPaths.ShouldHaveSingleItem();
        path.IsRouteFrozen.ShouldBeTrue();
        path.Path.IsBlockedFallback.ShouldBeFalse(
            "the cached abutment straight is honest geometry — the router's degenerate " +
            "CSC fallback used to flag these blocked");
        var segment = path.Path.Segments.ShouldHaveSingleItem();
        var straight = segment.ShouldBeOfType<CAP_Core.Routing.StraightSegment>();
        straight.LengthMicrometers.ShouldBe(0.0, 1e-9);
    }

    // ── Group naming ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RouteDerivedConnection_UsesTracedCachedRoute()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var routingPasses = 0;
        var wasRouting = false;
        canvas.Routing.StateChanged += () =>
        {
            if (canvas.Routing.IsRouting && !wasRouting)
                routingPasses++;
            wasRouting = canvas.Routing.IsRouting;
        };
        // wgA.out at (10, 2), wgB.in at (20, 2), bridged by a drawn route stripe.
        var stripe = new GdsOutlinePolygon
        {
            Layer = 1,
            DataType = 0,
            Points = new[]
            {
                new GdsOutlinePoint(10, 1.75), new GdsOutlinePoint(20, 1.75),
                new GdsOutlinePoint(20, 2.25), new GdsOutlinePoint(10, 2.25),
                new GdsOutlinePoint(10, 1.75),
            },
        };
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 20, 0) },
            Connections = new[]
            {
                new GdsConnectionInstruction
                {
                    A = new GdsConnectionEndpoint { InstanceIndex = 0, PinName = "out" },
                    B = new GdsConnectionEndpoint { InstanceIndex = 1, PinName = "in" },
                    IsRouteDerived = true,
                    SourcePolygons = new[] { stripe },
                },
            },
        };

        // Frozen mode: this test pins the traced-cached-route contract; the
        // re-route default is covered by the dialog-level reroute-toggle theory.
        var report = await executor.ExecuteAsync(plan, rerouteImportedConnections: false);

        report.ConnectedCount.ShouldBe(1);
        report.RouteDerivedCount.ShouldBe(1);
        report.CachedRouteCount.ShouldBe(1);
        report.ReroutedCount.ShouldBe(0);
        routingPasses.ShouldBe(1,
            "only the grouping command's pass runs — the drawn polygon IS the route, " +
            "no A* recalculation replaces it");

        var group = SingleGroupOn(canvas);
        var path = group.InternalPaths.ShouldHaveSingleItem();
        path.IsRouteFrozen.ShouldBeTrue("the imported route is hardcoded, like a .lun cached route");
        path.Path.IsValid.ShouldBeTrue();
        path.Path.IsBlockedFallback.ShouldBeFalse();
        path.Path.Segments[0].StartPoint.ShouldBe((10.0, 2.0), "anchored at the placed start pin");
        path.Path.Segments[^1].EndPoint.ShouldBe((20.0, 2.0), "anchored at the placed end pin");
        path.Path.Segments.Any(s =>
            s.StartPoint.X == 10.0 && s.StartPoint.Y == 1.75
            && s.EndPoint.X == 20.0 && s.EndPoint.Y == 1.75).ShouldBeTrue(
            "the drawn stripe's outline is traced into the route");
    }

    [Fact]
    public async Task ExecuteAsync_RouteDerivedWithoutPolygons_FallsBackToRouting()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 20, 0) },
            Connections = new[]
            {
                new GdsConnectionInstruction
                {
                    A = new GdsConnectionEndpoint { InstanceIndex = 0, PinName = "out" },
                    B = new GdsConnectionEndpoint { InstanceIndex = 1, PinName = "in" },
                    IsRouteDerived = true,
                    // No SourcePolygons (a hand-built plan may carry none): the batch
                    // routing pass must still give the connection a route.
                },
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.ConnectedCount.ShouldBe(1);
        report.RouteDerivedCount.ShouldBe(1);
        report.CachedRouteCount.ShouldBe(0);
        var group = SingleGroupOn(canvas);
        group.InternalPaths.ShouldHaveSingleItem().Path.Segments.ShouldNotBeEmpty();
    }

    // ── Group naming ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_GroupIsSelectedWithItsFinalName()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        await executor.ExecuteAsync(plan);

        // The group is named BEFORE it is selected: bound panels never observe
        // the placeholder Group_HHmmss name (DisplayName has no change notification).
        var groupVm = canvas.Components.ShouldHaveSingleItem();
        canvas.SelectedComponent.ShouldBeSameAs(groupVm);
        groupVm.DisplayName.ShouldBe("TOP");
    }

    // ── Non-cardinal rotation ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HalfwayRotation_KeptExactly()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0, rotationDegrees: 45) },
        };

        var report = await executor.ExecuteAsync(plan);

        // 45° is no longer snapped to a cardinal — the exact angle keeps the
        // instance's pins on the true joints the import projected.
        report.Warnings.ShouldBeEmpty();
        canvas.Components.ShouldHaveSingleItem().Component.RotationDegrees.ShouldBe(45, 1e-9);
    }

    // ── Top-cell route geometry (frozen paths) ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TopCellWaveguidePolygons_BecomePinLessFrozenPathsOnGroup()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[] { Connection(0, "out", 1, "in") },
            TopCellWaveguidePolygons = new[]
            {
                new GdsOutlinePolygon
                {
                    Layer = 1,
                    DataType = 0,
                    Points = new GdsOutlinePoint[]
                    {
                        new(10, 2.25), new(12, 2.25), new(12, 1.75), new(10, 1.75), new(10, 2.25),
                    },
                },
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.GroupCreated.ShouldBeTrue();
        report.Warnings.ShouldBeEmpty();

        var group = SingleGroupOn(canvas);
        group.InternalPaths.Count.ShouldBe(2, "the frozen abutment connection plus the route outline");
        var routePath = group.InternalPaths.Single(p => p.StartPin is null);
        routePath.EndPin.ShouldBeNull("imported route geometry is pin-less on BOTH ends");
        routePath.Path.Segments.Count.ShouldBe(4, "the rectangle's four edges, first point repeated at the end");
        routePath.Path.Segments.Select(s => (s.StartPoint.X, s.StartPoint.Y, s.EndPoint.X, s.EndPoint.Y))
            .ShouldBe(new[]
            {
                (10.0, 2.25, 12.0, 2.25),
                (12.0, 2.25, 12.0, 1.75),
                (12.0, 1.75, 10.0, 1.75),
                (10.0, 1.75, 10.0, 2.25),
            });

        // The frozen abutment connection is untouched by the imported geometry.
        var abutment = group.InternalPaths.Single(p => p.StartPin is not null);
        abutment.StartPin!.Name.ShouldBe("out");
    }

    [Fact]
    public async Task ExecuteAsync_RoutePolygonsWithoutGroup_ReportsDroppedGeometry()
    {
        var (canvas, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0) }, // one component → no group
            TopCellWaveguidePolygons = new[]
            {
                new GdsOutlinePolygon
                {
                    Layer = 1,
                    Points = new GdsOutlinePoint[] { new(0, 0), new(5, 0), new(5, 1), new(0, 0) },
                },
            },
        };

        var report = await executor.ExecuteAsync(plan);

        report.GroupCreated.ShouldBeFalse();
        var warning = report.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("no group was created to hold the frozen paths");
    }
}
