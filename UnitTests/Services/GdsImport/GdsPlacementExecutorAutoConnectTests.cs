using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for the experimental auto-connect pass and the post-batch validation
/// net in <see cref="GdsPlacementExecutor"/> (issue #808 follow-up): facing
/// free pins are paired and connected when the flag is on, occupied and
/// non-optical pins are excluded, every decision lands in the report, and
/// <see cref="CAP_Core.Analysis.DesignValidator"/> issues surface as
/// validation warnings. Fixtures follow <see cref="GdsPlacementExecutorTests"/>.
/// </summary>
public class GdsPlacementExecutorAutoConnectTests
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

    /// <summary>"wg" plus an electrical heater pin ("vcc") on the bottom edge.</summary>
    private static ComponentTemplate HeatedWaveguideTemplate()
    {
        var template = WaveguideTemplate();
        template.Name = "wgE";
        template.PinDefinitions = new[]
        {
            new PinDefinition("in", 0, 2, 180),
            new PinDefinition("out", 10, 2, 0),
            new PinDefinition("vcc", 5, 4, 270, MatterType.Electricity),
        };
        return template;
    }

    private static GdsPlacementInstruction Placement(
        string instanceName, double x, double y,
        string? identifier = "wg", string? pdkSource = "testpdk",
        double rotationDegrees = 0) => new()
    {
        InstanceName = instanceName,
        ComponentIdentifier = identifier,
        PdkSource = identifier is null ? null : pdkSource,
        XUm = x,
        YUm = y,
        RotationDegrees = rotationDegrees,
    };

    private static GdsConnectionInstruction Connection(
        int aIndex, string aPin, int bIndex, string bPin) => new()
    {
        A = new GdsConnectionEndpoint { InstanceIndex = aIndex, PinName = aPin },
        B = new GdsConnectionEndpoint { InstanceIndex = bIndex, PinName = bPin },
    };

    private static (DesignCanvasViewModel canvas, GdsPlacementExecutor executor)
        CreateExecutor(params ComponentTemplate[] templates)
    {
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => templates);
        return (canvas, executor);
    }

    // ── Auto-connect on/off ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AutoConnectOn_ConnectsFacingFreePinsAndReportsThem()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate());
        // wgA.out (10,2, 0°) faces wgB.in (100,2, 180°) across a 90 µm gap — no abutment.
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 100, 0) },
            Connections = Array.Empty<GdsConnectionInstruction>(),
        };

        var report = await executor.ExecuteAsync(
            plan, autoConnectFreePins: true, autoConnectRadiusUm: 100);

        report.ConnectedCount.ShouldBe(0, "no abutment connections exist in this plan");
        report.AutoConnectedCount.ShouldBe(1);
        var pair = report.AutoConnectedPairs.ShouldHaveSingleItem();
        pair.ShouldContain("wgA#0.out");
        pair.ShouldContain("wgB#1.in");
        report.ValidationWarnings.ShouldBeEmpty("a straight 90 µm route is clean");
        report.SkippedAutoConnect.Count.ShouldBe(2, "the outward-facing in/out pins find no partner");
        report.SkippedAutoConnect.ShouldAllBe(s => s.Contains("no opposing free pin"));

        // The auto-connected route is frozen into the group like an abutment connection.
        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var path = group.InternalPaths.ShouldHaveSingleItem();
        var pins = new[] { path.StartPin.Name, path.EndPin.Name };
        pins.ShouldBe(new[] { "out", "in" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ExecuteAsync_AutoConnectOff_LeavesFreePinsUnconnected()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 100, 0) },
            Connections = Array.Empty<GdsConnectionInstruction>(),
        };

        var report = await executor.ExecuteAsync(plan); // defaults: auto-connect off

        report.AutoConnectedCount.ShouldBe(0);
        report.AutoConnectedPairs.ShouldBeEmpty();
        report.SkippedAutoConnect.ShouldBeEmpty("without the pass there is nothing to report");
        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.InternalPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AutoConnectRadius_ExcludesPartnersBeyondIt()
    {
        var (_, executor) = CreateExecutor(WaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 100, 0) },
            Connections = Array.Empty<GdsConnectionInstruction>(),
        };

        var report = await executor.ExecuteAsync(
            plan, autoConnectFreePins: true, autoConnectRadiusUm: 50); // gap is 90 µm

        report.AutoConnectedCount.ShouldBe(0);
        report.SkippedAutoConnect.Count.ShouldBe(4, "all four free pins are out of each other's radius");
    }

    // ── Exclusions ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AlreadyConnectedPins_AreExcludedFromAutoConnect()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate());
        // Three abutting waveguides: only wgA.in and wgC.out stay free.
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                Placement("wgA#0", 0, 0),
                Placement("wgB#1", 10, 0),
                Placement("wgC#2", 20, 0),
            },
            Connections = new[]
            {
                Connection(0, "out", 1, "in"),
                Connection(1, "out", 2, "in"),
            },
        };

        var report = await executor.ExecuteAsync(plan, autoConnectFreePins: true);

        report.ConnectedCount.ShouldBe(2);
        report.AutoConnectedCount.ShouldBe(1, "only the two remaining free pins can pair");
        var pair = report.AutoConnectedPairs.ShouldHaveSingleItem();
        pair.ShouldContain("wgA#0.in");
        pair.ShouldContain("wgC#2.out");
        report.SkippedAutoConnect.ShouldBeEmpty("occupied pins are excluded, not reported as skipped");

        // The abutment connections were not replaced by the auto-connect pass.
        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.InternalPaths.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ExecuteAsync_NonOpticalPins_AreReportedAsSkipped()
    {
        var (_, executor) = CreateExecutor(HeatedWaveguideTemplate());
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgE#0", 0, 0, identifier: "wgE") },
            Connections = Array.Empty<GdsConnectionInstruction>(),
        };

        var report = await executor.ExecuteAsync(plan, autoConnectFreePins: true);

        report.AutoConnectedCount.ShouldBe(0);
        var skip = report.SkippedAutoConnect.Single(s => s.Contains("vcc"));
        skip.ShouldContain("non-optical");
    }

    // ── Validation net ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ProcessFloorViolation_SurfacesValidationWarning()
    {
        var (canvas, executor) = CreateExecutor(WaveguideTemplate());
        // The pins face each other but are 4 µm off-axis: the route needs an
        // S-jog that a 500 µm process bend-radius floor cannot produce, so the
        // router degrades below the floor and the validator flags it.
        canvas.Router.ProcessMinBendRadiusMicrometers = 500;
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 100, 4) },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.ConnectedCount.ShouldBe(1);
        var warning = report.ValidationWarnings.ShouldHaveSingleItem();
        warning.ShouldContain(nameof(CAP_Core.Analysis.DesignIssueType.BendRadiusBelowProcessMinimum));
        warning.ShouldContain("wg"); // the involved pin names ride along in the description
    }

    [Fact]
    public async Task ExecuteAsync_PerfectAbutment_IsNotFlaggedAsBlocked()
    {
        var (_, executor) = CreateExecutor(WaveguideTemplate());
        // wgA.out and wgB.in sit at the exact same point (10,2) — a perfect GDS
        // abutment. There is no routed geometry to validate, so the degenerate
        // route must NOT surface as a BlockedPath warning.
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[] { Placement("wgA#0", 0, 0), Placement("wgB#1", 10, 0) },
            Connections = new[] { Connection(0, "out", 1, "in") },
        };

        var report = await executor.ExecuteAsync(plan);

        report.ConnectedCount.ShouldBe(1);
        report.ValidationWarnings.ShouldBeEmpty();
    }
}
