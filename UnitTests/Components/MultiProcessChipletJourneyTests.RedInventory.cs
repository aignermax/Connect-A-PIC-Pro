using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Documented-red inventory stations of the multi-process journey (steps 7–8) —
/// each Skip links the issue filed for that exact single-process assumption. See
/// <see cref="MultiProcessChipletJourneyTests"/> for the full journey description.
/// (Step 3 turned green with the per-chiplet placement scope, issue #935; step 4
/// with the per-connection DRC rule sets, issue #936; step 5 with the per-connection
/// bend-radius floors, issue #937.)
/// </summary>
public partial class MultiProcessChipletJourneyTests
{
    [Fact]
    public void Step4_DrcLite_ChecksEachChipletAgainstItsOwnProcess()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();

        // A deliberately narrow styled route inside chiplet A's process scope:
        // 0.2 µm < Cornerstone's 0.25 µm foundry minimum (#924).
        var narrow = design.Canvas.ConnectPinsWithCachedRoute(
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3"),
            MultiProcessChipletJourneyDesign.StraightPath(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3")));
        narrow.ShouldNotBeNull();
        narrow!.Connection.WidthMicrometers = 0.2;

        // A two-process design can only exist in Playground (#935), so RunDesignChecks
        // runs with processLockActive = false — and still checks per chiplet (#936):
        // the per-connection provider keys each connection's rules to its own endpoint
        // PDKs, wired here exactly like MainViewModel.RunDesignChecks wires it.
        var drafts = new List<PdkDraft> { design.Cornerstone, design.Siepic };
        string? PdkSourceOf(CAP_Core.Components.Core.PhysicalPin? pin) =>
            pin?.ParentComponent is { } component
                ? ComponentPdkSourceResolver.Resolve(component, design.Templates)
                : null;
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            design.Canvas.ConnectionManager.Connections,
            allComponents: design.Canvas.Components.Select(vm => vm.Component),
            processLockActive: false,
            connectionDrcRuleProvider: connection =>
                ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
                    PdkSourceOf(connection.StartPin), PdkSourceOf(connection.EndPin), drafts));

        panel.Issues.ShouldContain(
            i => i.Type == DesignIssueType.WaveguideBelowMinWidth && ReferenceEquals(i.Connection, narrow.Connection),
            "chiplet A must be checked against Cornerstone's 0.25 µm minimum (#936)");
        panel.Issues.Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth
                && i.Description.Contains("si_")).ShouldBe(0,
            "chiplet B must stay silent: SiEPIC declares no minWidthUm — no invented values (#926)");
    }

    [Fact]
    public void Step5_BendRadiusFloor_FollowsEachChipletsProcess()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var abutment = design.Canvas.ConnectionManager.Connections.Single();

        // One more route inside each chiplet, so all three endpoint combinations
        // exist on the canvas: A–A, B–B and the cross-process A–B abutment.
        var intraA = design.Canvas.ConnectPinsWithCachedRoute(
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3"),
            MultiProcessChipletJourneyDesign.StraightPath(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3")));
        intraA.ShouldNotBeNull();
        var intraB = design.Canvas.ConnectPinsWithCachedRoute(
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 3"),
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_taper_port 2"),
            MultiProcessChipletJourneyDesign.StraightPath(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 3"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_taper_port 2")));
        intraB.ShouldNotBeNull();

        // #937: the floor is per connection now — each endpoint component's PDK process
        // contributes its minimum and the stricter side governs. The provider is wired
        // exactly like MainViewModel wires RoutingOrchestrator.BuildConnectionProcessFloorProvider.
        var drafts = new List<PdkDraft> { design.Cornerstone, design.Siepic };
        string? PdkSourceOf(CAP_Core.Components.Core.PhysicalPin? pin) =>
            pin?.ParentComponent is { } component
                ? ComponentPdkSourceResolver.Resolve(component, design.Templates)
                : null;
        double? FloorBetween(CAP_Core.Components.Core.PhysicalPin start, CAP_Core.Components.Core.PhysicalPin end) =>
            WaveguideBendRadiusResolver.ResolveForEndpointPdkNames(
                PdkSourceOf(start), PdkSourceOf(end), drafts);

        FloorBetween(intraA!.Connection.StartPin, intraA!.Connection.EndPin)
            .ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
                "an intra-chiplet-A route honors the Cornerstone 30 µm floor, not the fallback (#937)");
        FloorBetween(intraB!.Connection.StartPin, intraB!.Connection.EndPin)
            .ShouldBe(MultiProcessChipletJourneyDesign.SiepicMinBendRadiusUm,
                "an intra-chiplet-B route uses SiEPIC's declared 5 µm — below the generic 10 µm fallback (#937)");
        FloorBetween(abutment.StartPin, abutment.EndPin)
            .ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
                "the cross-chiplet abutment keeps the stricter side: Cornerstone 30 µm over SiEPIC 5 µm (#937)");
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
}
