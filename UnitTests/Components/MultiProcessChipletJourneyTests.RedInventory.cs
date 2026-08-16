using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Documented-red inventory stations of the multi-process journey (steps 5 and 7) —
/// each Skip links the issue filed for that exact single-process assumption. See
/// <see cref="MultiProcessChipletJourneyTests"/> for the full journey description.
/// (Step 3 turned green with the per-chiplet placement scope, issue #935; step 4
/// with the per-connection DRC rule sets, issue #936; step 8 with the per-process
/// GDS export interconnects, issue #939.)
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

    [Fact(Skip = "Single-process assumption (#933 inventory): the router bend-radius floor is one canvas-wide value (Playground fallback 10 µm) — per-chiplet floors are https://github.com/aignermax/Lunima/issues/937")]
    public void Step5_BendRadiusFloor_FollowsEachChipletsProcess()
    {
        // What production applies on a two-process (Playground) canvas: no member PDKs,
        // so the resolver falls back to the generic 10 µm for EVERY route.
        var floorForChipletA = WaveguideBendRadiusResolver.Resolve(new ProcessDefinition?[0]);

        floorForChipletA.ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
            "chiplet A's routes must honor the Cornerstone 30 µm floor, not the fallback (#937)");
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

    [Fact]
    public void Step8_GdsExport_EachChipletRoutesOnItsOwnProcessStack()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var script = new SimpleNazcaExporter().Export(design.Canvas);

        // One interconnect per process cross-section on the canvas: each chiplet's
        // frozen wires route on their own process' width/radius/layer — resolved from
        // the endpoint pins' PDK stamps, not from one global user preference (#939).
        script.ShouldContain("Interconnect(width=1.2, radius=10, layer=203)"); // chiplet A: Cornerstone NITRIDE (xs_nc, 1.2 µm)
        script.ShouldContain("Interconnect(width=0.5, radius=10, layer=1)");   // chiplet B: SiEPIC WG (strip, 0.5 µm)
        script.ShouldContain("nd.strt(length=5.00, width=1.2, layer=203)");    // chiplet A's coupler→MMI wire
        script.ShouldContain("nd.strt(length=5.01, width=0.5, layer=1)");      // chiplet B's Y-branch→taper wire
    }
}
