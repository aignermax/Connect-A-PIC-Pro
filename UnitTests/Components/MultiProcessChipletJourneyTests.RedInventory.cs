using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
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
/// Documented-red inventory stations of the multi-process journey (steps 3–5, 7–8) —
/// each Skip links the issue filed for that exact single-process assumption. See
/// <see cref="MultiProcessChipletJourneyTests"/> for the full journey description.
/// </summary>
public partial class MultiProcessChipletJourneyTests
{
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
            MultiProcessChipletJourneyDesign.StraightPath(
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
