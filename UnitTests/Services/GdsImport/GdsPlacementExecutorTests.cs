using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
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
        var pins = new[] { path.StartPin.Name, path.EndPin.Name };
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
    public async Task ExecuteAsync_NonCardinalRotation_SnapsAndWarns()
    {
        var (_, _, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0, rotationDegrees: 44) },
        };

        var report = await executor.ExecuteAsync(plan);

        report.Warnings.ShouldHaveSingleItem().ShouldContain("wgA#0");
        report.Warnings[0].ShouldContain("snapped");
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
}
