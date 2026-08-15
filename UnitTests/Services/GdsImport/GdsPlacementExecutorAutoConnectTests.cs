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
/// Tests for the opt-in auto-connect stage of <see cref="GdsPlacementExecutor"/>
/// (issue #880): every remaining facing pin pair is routed after placement,
/// unroutable pairs are kept red AND named in the report, cancellation mid-run
/// keeps the already-routed connections, and the default stays OFF.
/// </summary>
public class GdsPlacementExecutorAutoConnectTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>10×4 µm two-port waveguide template, pins in(0,2,180°)/out(10,2,0°).</summary>
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

    /// <summary>
    /// 100×100 µm blocker whose only pin sits at its CENTER: the router's pin
    /// corridor (3·bend-radius) cannot clear a way out of the body, so any route
    /// to this pin fails — deterministic "unroutable" without huge fixtures.
    /// </summary>
    private static ComponentTemplate TrapTemplate() => new()
    {
        Name = "trap",
        Category = "Test",
        PdkSource = "testpdk",
        WidthMicrometers = 100,
        HeightMicrometers = 100,
        PinDefinitions = new[] { new PinDefinition("port", 50, 50, 0) },
        CreateSMatrix = pins => new CAP_Core.LightCalculation.SMatrix(
            pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(),
            new List<(Guid, double)>()),
    };

    private static GdsPlacementInstruction Placement(
        string instanceName, double x, double y, string identifier = "wg") => new()
    {
        InstanceName = instanceName,
        ComponentIdentifier = identifier,
        PdkSource = "testpdk",
        XUm = x,
        YUm = y,
    };

    private static (DesignCanvasViewModel canvas, GdsPlacementExecutor executor)
        CreateExecutor(params ComponentTemplate[] templates)
    {
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => templates);
        return (canvas, executor);
    }

    /// <summary>Three spaced waveguides in a row → two facing pin pairs, no plan connections.</summary>
    private static GdsPlacementPlan ThreeSpacedWaveguides() => new()
    {
        GroupName = "TOP",
        Placements = new[]
        {
            Placement("wgA#0", 0, 0),
            Placement("wgB#1", 40, 0),
            Placement("wgC#2", 80, 0),
        },
    };

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AutoConnectOn_RoutesEveryFacingPairAndReportsTheCount()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate());

        var report = await executor.ExecuteAsync(
            ThreeSpacedWaveguides(), autoConnectAllPins: true);

        report.AutoConnectedCount.ShouldBe(2, "wgA.out↔wgB.in and wgB.out↔wgC.in face each other");
        report.AutoConnectFailedCount.ShouldBe(0);
        report.AutoConnectUnpairedPinCount.ShouldBe(2, "wgA.in and wgC.out have no facing partner");

        // Grouping runs LAST, so the auto-connections are frozen into the group.
        var group = canvas.Components.ShouldHaveSingleItem()
            .Component.ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.InternalPaths.Count.ShouldBe(2);
        group.InternalPaths.ShouldAllBe(p => p.Path.IsValid && !p.Path.IsBlockedFallback);
    }

    [Fact]
    public async Task ExecuteAsync_AutoConnectOffByDefault_LeavesPinsUnconnected()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate());

        var report = await executor.ExecuteAsync(ThreeSpacedWaveguides());

        report.AutoConnectedCount.ShouldBe(0);
        canvas.Connections.ShouldBeEmpty();
        var group = canvas.Components.ShouldHaveSingleItem()
            .Component.ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.InternalPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_PlanConnectedPins_AreNotAutoConnectedAgain()
    {
        var (_, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[]
            {
                new GdsConnectionInstruction
                {
                    A = new GdsConnectionEndpoint { InstanceIndex = 0, PinName = "out" },
                    B = new GdsConnectionEndpoint { InstanceIndex = 1, PinName = "in" },
                },
            },
        };

        var report = await executor.ExecuteAsync(plan, autoConnectAllPins: true);

        report.ConnectedCount.ShouldBe(1);
        report.AutoConnectedCount.ShouldBe(0,
            "the plan connection consumed the only facing pair; the outer pins point apart");
    }

    // ── Unroutable pairs: reported, not silently red ─────────────────────────

    [Fact]
    public async Task ExecuteAsync_UnroutablePair_IsKeptBlockedAndNamedInTheReport()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate(), TrapTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                // trap.port at (150, 50) faces the waveguide's in pin at (250, 50):
                // they pair, but the route out of the trap's body is blocked.
                Placement("trap#0", 100, 0, identifier: "trap"),
                Placement("wgB#1", 250, 48),
            },
        };

        var report = await executor.ExecuteAsync(plan, autoConnectAllPins: true);

        report.AutoConnectFailedCount.ShouldBe(1);
        report.Warnings.ShouldContain(w =>
            w.Contains("Auto-connect could not route") && w.Contains("blocked (red) path"));

        // The blocked connection stays visible on the canvas (inside the group).
        var group = canvas.Components.ShouldHaveSingleItem()
            .Component.ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        var path = group.InternalPaths.ShouldHaveSingleItem();
        (path.Path.IsBlockedFallback || !path.Path.IsValid).ShouldBeTrue();
    }

    // ── Cancellation: anytime semantics ──────────────────────────────────────

    /// <summary>
    /// Cancels synchronously on the first progress report of a given stage —
    /// deterministic, unlike <see cref="Progress{T}"/>'s thread-pool posting.
    /// </summary>
    private sealed class StageCancelProgress(string stageName, CancellationTokenSource cts)
        : IProgress<string>
    {
        public void Report(string value)
        {
            if (value.Contains(stageName))
                cts.Cancel();
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidAutoConnect_KeepsAlreadyRoutedConnections()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate());
        executor.AutoConnectBatchSize = 1;
        executor.ProgressReportInterval = TimeSpan.Zero;
        using var cts = new CancellationTokenSource();
        var progress = new StageCancelProgress("Auto-connecting pins", cts);

        await Should.ThrowAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            ThreeSpacedWaveguides(), progress, cts.Token, autoConnectAllPins: true));

        // The first 1-pair batch was connected AND routed before the cancel check
        // of the second batch fired — nothing is rolled back (anytime semantics).
        var connection = canvas.Connections.ShouldHaveSingleItem().Connection;
        connection.IsPathValid.ShouldBeTrue();
        connection.IsBlockedFallback.ShouldBeFalse();
    }
}
