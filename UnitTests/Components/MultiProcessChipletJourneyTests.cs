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
///   Step 5 (RED, #937): the router's bend-radius floor is one canvas-wide value.
///   Step 6 (green): .lun round-trip — both per-component PDK assignments, the
///         groups, the abutment and the physics survive (the design reloads as
///         Playground: pinned here as current behavior, #938 is the fix).
///   Step 7 (RED, #938): no per-chiplet process binding exists to persist.
///   Step 8 (RED, #939): GDS export routes every waveguide through one global
///         interconnect (width/radius/layer), not each chiplet's own stack.
/// </summary>
public class MultiProcessChipletJourneyTests : IDisposable
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

    [Fact(Skip = "Single-process assumption (#933 inventory): the placement policy is canvas-global — a process-locked canvas rejects the second chiplet's process; per-chiplet process scope is https://github.com/aignermax/Lunima/issues/935")]
    public void Step3_ProcessLockedCanvas_AcceptsSecondProcessChiplet()
    {
        var cornerstone = MultiProcessChipletJourneyDesign.LoadPdk(MultiProcessChipletJourneyDesign.CornerstonePdkFile);
        var siepic = MultiProcessChipletJourneyDesign.LoadPdk(MultiProcessChipletJourneyDesign.SiepicPdkFile);
        var catalog = ProcessCatalog.BuildGroups(new[]
        {
            new PdkProcessEntry(cornerstone.Name, ProcessFingerprintFactory.From(cornerstone)),
            new PdkProcessEntry(siepic.Name, ProcessFingerprintFactory.From(siepic)),
        });
        var active = ActiveProcessSelection.ForGroup(
            catalog.Single(g => g.MemberPdkNames.Contains(cornerstone.Name)));

        var canvas = new DesignCanvasViewModel();
        var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());
        interaction.PlacementContext = new PlacementPolicyContext(
            () => active, () => Array.Empty<string>(), _ => null);

        interaction.SelectedTemplate = MultiProcessChipletJourneyDesign.TemplateFor(cornerstone, "Coupler");
        interaction.CanvasClicked(100, 100);
        canvas.Components.Count.ShouldBe(1, "the Cornerstone chiplet's process owns the canvas");

        // Rung 6: a SiEPIC chiplet must be placeable NEXT TO the Cornerstone chiplet,
        // carrying its own process — today the canvas-global lock rejects it.
        interaction.SelectedTemplate = MultiProcessChipletJourneyDesign.TemplateFor(siepic, "Y-Branch 1550");
        interaction.CanvasClicked(500, 100);
        canvas.Components.Count.ShouldBe(2,
            "rung 6: the second chiplet's process must be placeable as a chiplet-scoped process (#935)");
    }

    [Fact(Skip = "Single-process assumption (#933 inventory): DRC-lite derives its whole rule set from the single active process (first member PDK) — per-chiplet limits are https://github.com/aignermax/Lunima/issues/936")]
    public void Step4_DrcLite_ChecksEachChipletAgainstItsOwnProcess()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();

        // A deliberately narrow styled route inside chiplet A's process scope:
        // 0.2 µm < Cornerstone's 0.25 µm foundry minimum (#924).
        var narrow = design.Canvas.ConnectPinsWithCachedRoute(
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3"),
            StraightPath(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3")));
        narrow.ShouldNotBeNull();
        narrow!.Connection.WidthMicrometers = 0.2;

        // Production behavior for a two-process design: it can only exist in Playground
        // (#935), so RunDesignChecks runs with processLockActive = false and NO process
        // rules at all — the Cornerstone violation passes silently.
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            design.Canvas.ConnectionManager.Connections,
            allComponents: design.Canvas.Components.Select(vm => vm.Component),
            processLockActive: false);

        panel.Issues.ShouldContain(
            i => i.Type == DesignIssueType.WaveguideBelowMinWidth && ReferenceEquals(i.Connection, narrow.Connection),
            "chiplet A must be checked against Cornerstone's 0.25 µm minimum (#936)");
        panel.Issues.Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth
                && i.Description.Contains("si_")).ShouldBe(0,
            "chiplet B must stay silent: SiEPIC declares no minWidthUm — no invented values (#926)");
    }

    [Fact(Skip = "Single-process assumption (#933 inventory): the router bend-radius floor is one canvas-wide value (Playground fallback 10 µm) — per-chiplet floors are https://github.com/aignermax/Lunima/issues/937")]
    public void Step5_BendRadiusFloor_FollowsEachChipletsProcess()
    {
        // What production applies on a two-process (Playground) canvas: no member PDKs,
        // so the resolver falls back to the generic 10 µm for EVERY route.
        var floorForChipletA = WaveguideBendRadiusResolver.Resolve(new ProcessDefinition?[0]);

        floorForChipletA.ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
            "chiplet A's routes must honor the Cornerstone 30 µm floor, not the fallback (#937)");
    }

    [Fact]
    public async Task Step6_LunRoundTrip_BothPdkAssignmentsSurvive()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var fieldsBefore = await SimulateAsync(design.Canvas,
            InjectLight("source", MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o1")));
        double outputBefore = Amplitude(fieldsBefore,
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_taper_port 2").LogicalPin!.IDOutFlow);

        var saveVm = CreateFileOperations(design.Canvas, design.Templates);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(_designFilePath).ShouldBeTrue("Step 6: design file must be written");

        // Both per-component PDK assignments are persisted in the file itself.
        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldContain(design.Cornerstone.Name);
        fileText.ShouldContain(design.Siepic.Name);

        var loadedCanvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(loadedCanvas, design.Templates);
        loadVm.ProcessCatalogProvider = () => ProcessCatalog.BuildGroups(new[]
        {
            new PdkProcessEntry(design.Cornerstone.Name, ProcessFingerprintFactory.From(design.Cornerstone)),
            new PdkProcessEntry(design.Siepic.Name, ProcessFingerprintFactory.From(design.Siepic)),
        });
        string? migrationWarning = null;
        loadVm.OnProcessMigrationWarning = w => migrationWarning = w;
        var loadDialog = new Mock<IFileDialogService>();
        loadDialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        loadVm.FileDialogService = loadDialog.Object;
        await loadVm.LoadDesignCommand.ExecuteAsync(null);

        var loadedGroups = loadedCanvas.Components
            .Where(c => c.Component is ComponentGroup)
            .Select(c => (ComponentGroup)c.Component)
            .ToList();
        loadedGroups.Count.ShouldBe(2, "Step 6: both chiplets survive the round-trip");
        var loadedA = loadedGroups.SingleOrDefault(g => g.Identifier == design.ChipletA.Identifier)
            .ShouldNotBeNull("Step 6: chiplet A identity survives");
        var loadedB = loadedGroups.SingleOrDefault(g => g.Identifier == design.ChipletB.Identifier)
            .ShouldNotBeNull("Step 6: chiplet B identity survives");
        loadedA.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            MultiProcessChipletJourneyDesign.ChipletAPinNames.OrderBy(n => n),
            "Step 6: chiplet A keeps its exposed pins");
        loadedB.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            MultiProcessChipletJourneyDesign.ChipletBPinNames.OrderBy(n => n),
            "Step 6: chiplet B keeps its exposed pins");

        loadedCanvas.Connections.Count.ShouldBe(1, "Step 6: the inter-chiplet abutment survives");
        var abutment = loadedCanvas.Connections.Single();
        var startGroup = abutment.Connection.StartPin.ParentComponent?.ParentGroup;
        var endGroup = abutment.Connection.EndPin.ParentComponent?.ParentGroup;
        startGroup.ShouldNotBeNull("Step 6: the abutment start pin must stay inside chiplet A");
        endGroup.ShouldNotBeNull("Step 6: the abutment end pin must stay inside chiplet B");
        ReferenceEquals(startGroup, endGroup).ShouldBeFalse(
            "Step 6: the abutment must keep bridging the two chiplets");

        // Current behavior, pinned honestly: the design-level process record cannot
        // represent two processes, so the reloaded design collapses to Playground
        // ("not manufacturable"). The desired per-chiplet binding is step 7 (#938).
        loadVm.ActiveProcess.ShouldNotBeNull();
        loadVm.ActiveProcess!.IsPlayground.ShouldBeTrue(
            "Step 6: today a two-process design reloads as Playground — the single design-level " +
            "ActiveProcess cannot hold both processes (#938)");
        migrationWarning.ShouldNotBeNull();
        migrationWarning!.ShouldContain("multiple processes");

        var loadedFields = await SimulateAsync(loadedCanvas,
            InjectLight("source", MultiProcessChipletJourneyDesign.ExposedPin(loadedA, "cs_coupler_o1")));
        Amplitude(loadedFields,
                MultiProcessChipletJourneyDesign.ExposedPin(loadedB, "si_taper_port 2").LogicalPin!.IDOutFlow)
            .ShouldBe(outputBefore, AmplitudeTolerance,
                "Step 6: the reloaded two-process system delivers the same output power");
    }

    [Fact(Skip = "Single-process assumption (#933 inventory): .lun persists one design-level ActiveProcess and no per-chiplet process binding — https://github.com/aignermax/Lunima/issues/938")]
    public async Task Step7_LunRoundTrip_PerChipletProcessBindingSurvives()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var saveVm = CreateFileOperations(design.Canvas, design.Templates);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

        var loadedCanvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(loadedCanvas, design.Templates);
        var loadDialog = new Mock<IFileDialogService>();
        loadDialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        loadVm.FileDialogService = loadDialog.Object;
        await loadVm.LoadDesignCommand.ExecuteAsync(null);

        // Rung 6: chiplet A must reload bound to the Cornerstone process and chiplet B
        // to the SiEPIC process — the design as a whole is not one process.
        loadVm.ActiveProcess.ShouldNotBeNull();
        loadVm.ActiveProcess!.IsPlayground.ShouldBeFalse(
            "each chiplet's process binding must survive the round-trip (#938)");
    }

    [Fact(Skip = "Single-process assumption (#933 inventory): GDS export routes every waveguide through one global interconnect (width/radius/layer from a user preference) — per-chiplet stacks are https://github.com/aignermax/Lunima/issues/939")]
    public void Step8_GdsExport_EachChipletRoutesOnItsOwnProcessStack()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var script = new SimpleNazcaExporter().Export(design.Canvas);

        // Today the header holds ONE global interconnect — neither chiplet's stack appears.
        script.ShouldContain("layer=203"); // chiplet A: Cornerstone NITRIDE (xs_nc, 1.2 µm) (#939)
        script.ShouldContain("layer=1"); // chiplet B: SiEPIC WG (strip, 0.5 µm) (#939)
    }

    // ── Journey helpers ─────────────────────────────────────────────────────────

    private static RoutedPath StraightPath(PhysicalPin from, PhysicalPin to)
    {
        var (x1, y1) = from.GetAbsolutePosition();
        var (x2, y2) = to.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        return path;
    }

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
