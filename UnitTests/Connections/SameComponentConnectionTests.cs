using System.Numerics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export.Netlist;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;
using Component = CAP_Core.Components.Core.Component;

namespace UnitTests.Connections;

/// <summary>
/// Same-component (self-loop) connections: two DIFFERENT pins of one instance may be
/// connected — feedback loops, ring-resonator self-coupling, and black-box GDS imports
/// whose pins have no other meaningful partner. Covers the core route (A* must leave
/// the pin corridor, loop around the component body and enter the other pin without
/// crossing itself or the body), the design validator, the netlist self-edge, the
/// .lun endpoint mapping, click-to-connect, undo/redo, and the light solver, where a
/// self-edge is an ordinary feedback coupling of the component's own pin flows.
/// Only connecting a pin to ITSELF stays invalid (gesture/click layer).
/// </summary>
public class SameComponentConnectionTests
{
    // ── Core routing ────────────────────────────────────────────────────────

    [Fact]
    public void AddConnection_OppositeSidePinsOfSameComponent_RoutesAroundComponentBody()
    {
        // Straight waveguide 250×250 at (100,100): "in" on the west face pointing
        // west, "out" on the east face pointing east. The only clean route exits
        // one pin outward, loops around the body and enters the other pin.
        var comp = CreateSelfLoopWaveguide();
        var manager = CreateRoutedManager(out var router, comp);

        var conn = manager.AddConnection(comp.PhysicalPins[0], comp.PhysicalPins[1]);

        AssertCleanSelfLoopRoute(conn, router, comp);
        var points = SamplePath(conn.RoutedPath!);
        points.ShouldContain(
            p => p.Y < comp.PhysicalY - 1 || p.Y > comp.PhysicalY + comp.HeightMicrometers + 1,
            "the body blocks the straight line between the pins, so the route must "
            + "leave the component bbox (detour above or below it)");
    }

    [Fact]
    public void AddConnection_SameSidePinsOfSameComponent_RoutesAroundWithoutSelfIntersection()
    {
        // Coupler with west0/west1 on the SAME face (100 µm apart), both pointing
        // west: the route is a U-turn west of the body — the 360°-loop pathology
        // of issue #704 must not resurface for same-component endpoints.
        var comp = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("selfloop");
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        var manager = CreateRoutedManager(out var router, comp);
        var west0 = comp.PhysicalPins.First(p => p.Name == "west0");
        var west1 = comp.PhysicalPins.First(p => p.Name == "west1");

        var conn = manager.AddConnection(west0, west1);

        AssertCleanSelfLoopRoute(conn, router, comp);
        var points = SamplePath(conn.RoutedPath!);
        points.ShouldContain(
            p => p.X < comp.PhysicalX - 5,
            "both pins face west, so the U-turn must detour west of the component body");
    }

    /// <summary>Shared invariants of a cleanly routed same-component connection.</summary>
    private static void AssertCleanSelfLoopRoute(
        WaveguideConnection conn, WaveguideRouter router, Component comp)
    {
        conn.IsPathValid.ShouldBeTrue("a same-component connection must route");
        conn.RoutedPath.ShouldNotBeNull();
        conn.RoutedPath.Segments.ShouldNotBeEmpty();
        conn.IsBlockedFallback.ShouldBeFalse("the self-loop is routable — no blocked fallback");
        conn.RoutedPath.IsInvalidGeometry.ShouldBeFalse();
        PathIntersectionDetector.HasSelfIntersection(conn.RoutedPath).ShouldBeFalse(
            "issue #704: a route must never cross itself (360° loop at the pin)");
        router.IsPathBlockedByComponents(conn.RoutedPath.Segments).ShouldBeFalse(
            "the route must go AROUND the component body, not through it");

        var (startX, startY) = conn.StartPin.GetAbsolutePosition();
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        var first = conn.RoutedPath.Segments[0];
        var last = conn.RoutedPath.Segments[^1];
        Distance(first.StartPoint.X, first.StartPoint.Y, startX, startY)
            .ShouldBeLessThan(1.0, "the route starts exactly at the start pin");
        Distance(last.EndPoint.X, last.EndPoint.Y, endX, endY)
            .ShouldBeLessThan(1.0, "the route ends exactly at the end pin");
    }

    // ── Design validator ────────────────────────────────────────────────────

    [Fact]
    public void Validate_CleanSelfLoop_ReportsNoIssues()
    {
        var comp = CreateSelfLoopWaveguide();
        var manager = CreateRoutedManager(out _, comp);
        manager.AddConnection(comp.PhysicalPins[0], comp.PhysicalPins[1]);

        var issues = new DesignValidator().Validate(manager.Connections);

        issues.ShouldBeEmpty("a cleanly routed self-loop is not a design error");
    }

    // ── Netlist derivation ──────────────────────────────────────────────────

    [Fact]
    public void Derive_SelfConnection_EmitsSelfEdgeAndConsumesBothPins()
    {
        var comp = CreateSelfLoopWaveguide();
        var conn = new WaveguideConnection
        {
            StartPin = comp.PhysicalPins[0],
            EndPin = comp.PhysicalPins[1],
        };

        var doc = new NetlistDeriver().Derive(new[] { comp }, new[] { conn }, "selfloop");

        var edge = doc.Connections.ShouldHaveSingleItem();
        edge.InstanceA.ShouldBe(edge.InstanceB, "a self-edge loops within one instance");
        new[] { edge.PortA, edge.PortB }.ShouldBe(new[] { "in", "out" }, ignoreOrder: true);
        doc.Ports.ShouldBeEmpty("both pins are consumed by the self-edge — neither is a free port");
    }

    // ── .lun persistence endpoint mapping ────────────────────────────────────

    [Fact]
    public void ConnectionEndpointMapping_SameComponentBothEnds_ResolvesSameIndexAndDistinctPins()
    {
        // Save side: both endpoints map to the SAME component index with their
        // distinct pin names; load side: the names resolve back to those pins.
        var comp = CreateSelfLoopWaveguide();
        var vms = new List<ComponentViewModel> { new(comp) };

        var (startIdx, startPinName) =
            FileOperationsViewModel.ResolveConnectionEndpoint(vms, comp.PhysicalPins[0]);
        var (endIdx, endPinName) =
            FileOperationsViewModel.ResolveConnectionEndpoint(vms, comp.PhysicalPins[1]);

        startIdx.ShouldBe(0);
        endIdx.ShouldBe(0);
        startPinName.ShouldBe("in");
        endPinName.ShouldBe("out");
        FileOperationsViewModel.ResolvePin(comp, startPinName).ShouldBeSameAs(comp.PhysicalPins[0]);
        FileOperationsViewModel.ResolvePin(comp, endPinName).ShouldBeSameAs(comp.PhysicalPins[1]);
    }

    // ── Click-to-connect and undo ────────────────────────────────────────────

    [Fact]
    public void ClickToConnect_TwoPinsOfSameComponent_CreatesConnection()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateSelfLoopWaveguide();
        canvas.AddComponent(comp, "Loop");
        var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());
        interaction.CurrentMode = InteractionMode.Connect;

        interaction.PinClicked(comp.PhysicalPins[0]);
        interaction.PinClicked(comp.PhysicalPins[1]);

        var vm = canvas.Connections.ShouldHaveSingleItem();
        vm.Connection.StartPin.ParentComponent.ShouldBeSameAs(comp);
        vm.Connection.EndPin.ParentComponent.ShouldBeSameAs(comp);
        vm.Connection.StartPin.ShouldNotBeSameAs(vm.Connection.EndPin);
    }

    [Fact]
    public void ClickToConnect_SamePinTwice_StillRejected()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateSelfLoopWaveguide();
        canvas.AddComponent(comp, "Loop");
        var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());
        interaction.CurrentMode = InteractionMode.Connect;
        string? status = null;
        interaction.UpdateStatus = s => status = s;

        interaction.PinClicked(comp.PhysicalPins[0]);
        interaction.PinClicked(comp.PhysicalPins[0]);

        canvas.Connections.ShouldBeEmpty("a pin connected to itself stays invalid");
        status.ShouldBe("Cannot connect pin to itself");
    }

    [Fact]
    public async Task CreateConnectionCommand_SameComponentPins_ExecuteUndoRedo()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateSelfLoopWaveguide();
        canvas.AddComponent(comp, "Loop");
        var cmd = new CreateConnectionCommand(canvas, comp.PhysicalPins[0], comp.PhysicalPins[1]);

        cmd.Execute();
        await canvas.RecalculateRoutesAsync();
        canvas.Connections.ShouldHaveSingleItem();

        cmd.Undo();
        await canvas.RecalculateRoutesAsync();
        canvas.Connections.ShouldBeEmpty("undo removes the self-loop connection");

        cmd.Execute();
        await canvas.RecalculateRoutesAsync();
        canvas.Connections.ShouldHaveSingleItem("redo re-creates the self-loop connection");
    }

    // ── Light simulation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SystemSMatrix_SelfFeedbackEdge_ConvergesToAnalyticSteadyState()
    {
        // Lossy 2×2 coupler (through = cross = 0.5 amplitude) whose fbout pin loops
        // back into its own fbin pin through a (lossless) waveguide — a self-edge.
        // Driven with unit amplitude at "in", the steady state is analytic:
        //   f(fbout) = 0.5·(1 + f(fbin)) and f(fbin) = f(fbout)  →  both = 1.0
        //   f(out)   = 0.5·(1 + f(fbin))                        →  1.0
        // (without the self-edge f(out) would be 0.5, so the value proves the loop
        // actually participates in the solve). Loop gain 0.5 < 1 → the Neumann
        // series converges; a divergent loop would surface via the existing
        // NonConvergentCircuitException path, not silent corruption.
        var (comp, inP, outP, fbInP, fbOutP) = CreateLossyCouplerWithFeedbackPins();
        var tiles = new ComponentListTileManager();
        tiles.AddComponent(comp);

        var connections = new WaveguideConnectionManager(new WaveguideRouter());
        connections.AddExistingConnection(new WaveguideConnection
        {
            StartPin = comp.PhysicalPins.First(p => p.Name == "fbout"),
            EndPin = comp.PhysicalPins.First(p => p.Name == "fbin"),
        });

        var grid = GridManager.CreateForSimulation(
            tiles, connections, new PhysicalExternalPortManager());
        var systemMatrix = new SystemMatrixBuilder(grid).GetSystemSMatrix(1550);

        var input = MathNet.Numerics.LinearAlgebra.Vector<Complex>.Build.Dense(
            systemMatrix.PinReference.Count);
        input[systemMatrix.PinReference[inP.IDInFlow]] = Complex.One;

        var fields = await systemMatrix.CalcFieldAtPinsAfterStepsAsync(
            input, SMatrix.DefaultMaxIterations, new CancellationTokenSource());

        fields[fbInP.IDInFlow].Magnitude.ShouldBe(1.0, 1e-6,
            "light crosses the self-edge from fbout into fbin");
        fields[fbOutP.IDOutFlow].Magnitude.ShouldBe(1.0, 1e-6);
        fields[outP.IDOutFlow].Magnitude.ShouldBe(1.0, 1e-6,
            "0.5 direct + 0.5 via the self-feedback loop");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Straight 250×250 µm waveguide at (100,100) with physical pins "in"
    /// (west face, pointing west) and "out" (east face, pointing east).
    /// </summary>
    private static Component CreateSelfLoopWaveguide()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.Identifier = "SelfLoop";
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.WidthMicrometers = 250;
        comp.HeightMicrometers = 250;
        return comp;
    }

    /// <summary>
    /// 2×2 coupler with through/cross coupling of 0.5 amplitude (lossy, so the
    /// feedback loop contracts) and physical pins on all four logical pins.
    /// </summary>
    private static (Component comp, Pin inP, Pin outP, Pin fbInP, Pin fbOutP)
        CreateLossyCouplerWithFeedbackPins()
    {
        var inP = new Pin("in", 0, MatterType.Light, RectSide.Left);
        var outP = new Pin("out", 1, MatterType.Light, RectSide.Right);
        var fbInP = new Pin("fbin", 2, MatterType.Light, RectSide.Left);
        var fbOutP = new Pin("fbout", 3, MatterType.Light, RectSide.Right);

        var parts = new Part[2, 2];
        parts[0, 0] = new Part(new List<Pin> { inP });
        parts[1, 0] = new Part(new List<Pin> { outP });
        parts[0, 1] = new Part(new List<Pin> { fbInP });
        parts[1, 1] = new Part(new List<Pin> { fbOutP });

        const double t = 0.5; // through coupling (amplitude)
        const double k = 0.5; // cross coupling (amplitude)
        var pinIds = new[] { inP, outP, fbInP, fbOutP }
            .SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var matrix = new SMatrix(pinIds, new());
        matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            { (inP.IDInFlow, outP.IDOutFlow), t },
            { (inP.IDInFlow, fbOutP.IDOutFlow), k },
            { (fbInP.IDInFlow, outP.IDOutFlow), k },
            { (fbInP.IDInFlow, fbOutP.IDOutFlow), t },
            { (outP.IDInFlow, inP.IDOutFlow), t },
            { (outP.IDInFlow, fbInP.IDOutFlow), k },
            { (fbOutP.IDInFlow, inP.IDOutFlow), k },
            { (fbOutP.IDInFlow, fbInP.IDOutFlow), t },
        });

        var comp = new Component(
            new Dictionary<int, SMatrix> { { 1550, matrix } },
            new List<Slider>(), "lossy_coupler", "", parts, 0, "LossyCoupler", DiscreteRotation.R0);

        foreach (var (name, logical) in new[]
        {
            ("in", inP), ("out", outP), ("fbin", fbInP), ("fbout", fbOutP),
        })
        {
            comp.PhysicalPins.Add(new PhysicalPin
            {
                Name = name,
                ParentComponent = comp,
                LogicalPin = logical,
            });
        }
        return (comp, inP, outP, fbInP, fbOutP);
    }

    /// <summary>
    /// Router + connection manager over the given components, using the same
    /// 4-direction routing configuration as the issue-#704 repro tests.
    /// </summary>
    private static WaveguideConnectionManager CreateRoutedManager(
        out WaveguideRouter router, params Component[] components)
    {
        router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 10.0,
            AStarCellSize = 4.0,
            UseDiagonalRouting = false,
        };
        router.InitializePathfindingGrid(0, 0, 1200, 1000, components.ToList());
        return new WaveguideConnectionManager(router);
    }

    /// <summary>Samples every segment of the path (arc interiors included).</summary>
    private static List<(double X, double Y)> SamplePath(RoutedPath path)
    {
        var points = new List<(double X, double Y)>();
        foreach (var segment in path.Segments)
        {
            switch (segment)
            {
                case StraightSegment straight:
                    points.Add(straight.StartPoint);
                    points.Add(straight.EndPoint);
                    break;
                case BendSegment bend:
                    points.AddRange(ArcSampling.SamplePoints(bend, 2.0));
                    break;
            }
        }
        return points;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
