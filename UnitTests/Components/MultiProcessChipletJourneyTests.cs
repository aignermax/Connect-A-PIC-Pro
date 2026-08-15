using System.Collections.ObjectModel;
using System.Numerics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Multi-process chiplet journey (issue #933, #537-guardrail, rung-6 pre-probe),
/// exercised headlessly on the real bundled PDKs — chiplet A from CornerStone SiN
/// 300nm, chiplet B from SiEPIC EBeam — composed pin-to-pin on one canvas
/// (<see cref="MultiProcessChipletJourneyDesign"/>, recipe: #929).
///
/// The journey is an INVENTORY of the single-process assumptions on the road to
/// per-chiplet fabrication layer stacks (north-star #537, rung 6/7). Green steps
/// prove real behavior with honest value assertions; every station that hits a
/// single-process/single-chip assumption stays documented-red via Skip + link to
/// the issue filed for that exact assumption (pattern #910 → #909). The test does
/// not lie green.
///
///   Step 1 (green): both chiplets group, place and compose pin-to-pin across the
///         process boundary; pins carry their own PDK's width/layer stamps.
///   Step 2 (green): S-matrix simulation delivers physically correct power across
///         the chiplet boundary (value assertions from the bundled PDK data).
///   Step 3 (RED, #935): placement policy is canvas-global — a process-locked
///         canvas cannot take a second-process chiplet; only Playground can mix.
///   Step 4 (RED, #936): DRC-lite rules come from the single active process — a
///         two-process design checks nothing PDK-dependent at all.
///   Step 5 (green, #937): the router resolves the bend-radius floor per connection
///         from the endpoint components' chiplet process — chiplet A routes at
///         Cornerstone's 30 µm, chiplet B at SiEPIC's 5 µm, cross-process pairs at
///         the stricter of the two.
///   Step 6 (green): .lun round-trip — both per-component PDK assignments, the
///         groups, the abutment and the physics survive (the design reloads as
///         Playground: pinned here as current behavior, #938 is the fix).
///   Step 7 (RED, #938): no per-chiplet process binding exists to persist.
///   Step 8 (RED, #939): GDS export routes every waveguide through one global
///         interconnect (width/radius/layer), not each chiplet's own stack.
///
/// The red steps 3 and 8 additionally carry GREEN "today" pins in the
/// CurrentBehavior partial: the canvas-global lock really does reject the second
/// process, and GDS export really does size every route with one majority
/// cross-section — so a future per-chiplet fix turns those pins red as a tripwire.
/// (Step 5's tripwire fired with #937 and was replaced by the green step itself.)
/// </summary>
public partial class MultiProcessChipletJourneyTests : IDisposable
{
    private const int WavelengthNm = 1550;
    private const double AmplitudeTolerance = 1e-6;
    private const double PositionTolerance = 1e-9;

    private readonly string _designFilePath =
        Path.Combine(Path.GetTempPath(), $"multiprocess_chiplet_{Guid.NewGuid():N}.lun");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_designFilePath)) File.Delete(_designFilePath);
        }
        catch
        {
            // Temp cleanup must never fail the test run.
        }
    }

    [Fact]
    public void Step1_PlaceAndCompose_TwoProcessChiplets_OnOneCanvas()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();

        design.ChipletA.ChildComponents.Count.ShouldBe(2, "Step 1: chiplet A owns coupler + MMI");
        design.ChipletA.InternalPaths.Count.ShouldBe(1, "Step 1: the coupler→MMI wire freezes into chiplet A");
        design.ChipletA.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            MultiProcessChipletJourneyDesign.ChipletAPinNames.OrderBy(n => n),
            "Step 1: chiplet A exposes the free coupler ports and both MMI outputs");
        design.ChipletB.ChildComponents.Count.ShouldBe(2, "Step 1: chiplet B owns Y-branch + taper");
        design.ChipletB.InternalPaths.Count.ShouldBe(1, "Step 1: the Y-branch→taper wire freezes into chiplet B");
        design.ChipletB.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            MultiProcessChipletJourneyDesign.ChipletBPinNames.OrderBy(n => n),
            "Step 1: chiplet B exposes the combiner input, the free arm and the output");

        // Every pin keeps the width/layer stamp of the PDK it came from — the
        // per-component data substrate any per-chiplet stack must build on.
        var aOut = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o2");
        var bIn = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 1");
        aOut.WaveguideWidthMicrometers.ShouldBe(1.2, "Step 1: Cornerstone xs_nc width stamps chiplet A's pins");
        aOut.Layer.ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneGdsLayer,
            "Step 1: Cornerstone NITRIDE layer stamps chiplet A's pins");
        bIn.WaveguideWidthMicrometers.ShouldBe(0.5, "Step 1: SiEPIC strip width stamps chiplet B's pins");
        bIn.Layer.ShouldBe(MultiProcessChipletJourneyDesign.SiepicGdsLayer,
            "Step 1: SiEPIC WG layer stamps chiplet B's pins");

        var (ax, ay) = aOut.GetAbsolutePosition();
        var (bx, by) = bIn.GetAbsolutePosition();
        bx.ShouldBe(ax, PositionTolerance, "Step 1: the cross-process pin pair must coincide in X");
        by.ShouldBe(ay, PositionTolerance, "Step 1: the cross-process pin pair must coincide in Y");

        design.Canvas.ConnectionManager.Connections.Count.ShouldBe(1,
            "Step 1: exactly the one inter-chiplet abutment exists at canvas level");
        var findings = new DesignValidator().Validate(design.Canvas.ConnectionManager.Connections);
        findings.ShouldAllBe(
            i => i.Type == DesignIssueType.PinMismatch,
            "Step 1: the only findings at the boundary are the honest pin mismatches — a direct " +
            "SiN↔SOI butt joint really has a width (1.2↔0.5 µm) and layer (203↔1) step a real " +
            "edge coupler would absorb; the PDK-stamped pins report it correctly");
        findings.Count.ShouldBe(2, "Step 1: one width + one layer mismatch, nothing else");
        findings.ShouldAllBe(
            i => ReferenceEquals(i.Connection, design.Canvas.ConnectionManager.Connections.Single()),
            "Step 1: both mismatches attribute to the inter-chiplet abutment");
        findings.Count(i => i.Type == DesignIssueType.BlockedPath).ShouldBe(0,
            "Step 1: the cross-process abutment must not raise BlockedPath (#923)");
    }

    [Fact]
    public async Task Step2_Simulation_LightCrossesChipletBoundary()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var input = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o1");
        var fields = await SimulateAsync(design.Canvas, InjectLight("source", input));

        double boundary = Amplitude(fields,
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o2").LogicalPin!.IDOutFlow);
        double output = Amplitude(fields,
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_taper_port 2").LogicalPin!.IDOutFlow);
        double freeArm = Amplitude(fields,
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 3").LogicalPin!.IDOutFlow);
        double leakage = Amplitude(fields,
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o2").LogicalPin!.IDOutFlow);

        boundary.ShouldBe(MultiProcessChipletJourneyDesign.ExpectedBoundaryAmplitude, 2e-3,
            "Step 2: coupler × MMI from the Cornerstone S-matrices leaves chiplet A");
        output.ShouldBe(MultiProcessChipletJourneyDesign.ExpectedOutputAmplitude, MultiProcessChipletJourneyDesign.SolverValueTolerance,
            "Step 2: boundary × Y-branch × taper from the SiEPIC S-matrices arrives at chiplet B's output");
        freeArm.ShouldBe(MultiProcessChipletJourneyDesign.ExpectedFreeArmAmplitude, MultiProcessChipletJourneyDesign.SolverValueTolerance,
            "Step 2: the Y-branch's free arm carries its physical share — the boundary is truly crossed");
        leakage.ShouldBeLessThan(0.02,
            "Step 2: only the Y-branch's port-1 reflection leaks back to the unexcited coupler input");
    }

    [Fact]
    public void Step5_BendRadiusFloor_FollowsEachChipletsProcess()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var drafts = new List<PdkDraft> { design.Cornerstone, design.Siepic };
        double canvasWideFallback = WaveguideBendRadiusResolver.Resolve(
            ActiveProcessSelection.Playground(), drafts);
        canvasWideFallback.ShouldBe(WaveguideBendRadiusResolver.FallbackMinimumMicrometers,
            "Step 5: Playground knows no member PDKs — the canvas-wide value is the bare fallback");

        // The MainViewModel wiring, headless: each endpoint component's own PDK resolves
        // its optical minimum and the STRICTER of the pair governs the whole route (#937).
        design.Canvas.Router.ProcessMinBendRadiusForPinPair = (startPin, endPin) =>
            WaveguideBendRadiusResolver.ResolveForEndpointPdks(
                ComponentPdkSourceResolver.Resolve(startPin.ParentComponent, design.Templates),
                ComponentPdkSourceResolver.Resolve(endPin.ParentComponent, design.Templates),
                drafts, canvasWideFallback);

        var csCoupler = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4");
        var csMmi = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3");
        var siYBranch = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 3");
        var siTaper = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_taper_port 2");

        design.Canvas.Router.ProcessFloorFor(csCoupler, csMmi).ShouldBe(
            MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
            "Step 5: a route inside chiplet A honors Cornerstone's 30 µm floor, not the fallback");
        design.Canvas.Router.ProcessFloorFor(siYBranch, siTaper).ShouldBe(
            SiepicMinBendRadiusUm,
            "Step 5: a route inside chiplet B honors SiEPIC's 5 µm floor — no longer over-constrained");
        design.Canvas.Router.ProcessFloorFor(csMmi, siYBranch).ShouldBe(
            MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
            "Step 5: a cross-process route honors the STRICTER chiplet's floor — " +
            "anything looser would undercut Cornerstone's foundry minimum");
    }

    // ── Journey helpers ─────────────────────────────────────────────────────────

    private static (ExternalInput Input, Guid PinIdInFlow) InjectLight(string name, PhysicalPin pin) =>
        (new ExternalInput(name, new LaserType(LightColor.Red), 0, new Complex(1.0, 0), true),
         pin.LogicalPin!.IDInFlow);

    /// <summary>Runs the S-matrix field propagation over everything currently on the canvas.</summary>
    private static async Task<Dictionary<Guid, Complex>> SimulateAsync(
        DesignCanvasViewModel canvas, params (ExternalInput Input, Guid PinIdInFlow)[] inputs)
    {
        var portManager = new PhysicalExternalPortManager();
        foreach (var (input, pinIdInFlow) in inputs)
        {
            portManager.AddLightSource(input, pinIdInFlow);
        }

        var tileManager = new ComponentListTileManager();
        foreach (var viewModel in canvas.Components)
        {
            tileManager.AddComponent(viewModel.Component);
        }

        var grid = GridManager.CreateForSimulation(tileManager, canvas.ConnectionManager, portManager);
        var calculator = new GridLightCalculator(new SystemMatrixBuilder(grid), grid);
        return await calculator.CalculateFieldPropagationAsync(new CancellationTokenSource(), WavelengthNm);
    }

    private static double Amplitude(Dictionary<Guid, Complex> fields, Guid pinFlow) =>
        fields.TryGetValue(pinFlow, out var value)
            ? value.Magnitude
            : throw new ShouldAssertException($"pin flow {pinFlow} missing from simulated fields");

    /// <summary>Creates the file-operations facade used for the .lun save/load round-trip.</summary>
    private static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas, List<ComponentTemplate> templates)
    {
        var library = new ObservableCollection<ComponentTemplate>(templates);
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new CAP_Core.Export.SaxExporter(),
            library,
            new GdsExportViewModel(new CAP_Core.Export.GdsExportService()),
            new CAP.Avalonia.ViewModels.Export.PhotonTorchExportViewModel(
                new CAP_Core.Export.PhotonTorchExporter(), canvas),
            null!,
            errorConsole: new CAP_Core.ErrorConsoleService());
    }
}
