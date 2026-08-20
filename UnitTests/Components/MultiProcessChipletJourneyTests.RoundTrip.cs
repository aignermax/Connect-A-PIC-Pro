using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Green .lun round-trip stations of the multi-process journey (steps 6–7) — see
/// <see cref="MultiProcessChipletJourneyTests"/> for the full journey description.
/// </summary>
public partial class MultiProcessChipletJourneyTests
{
    [Fact]
    public async Task Step6_LunRoundTrip_BothPdkAssignmentsSurvive()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var fieldsBefore = await SimulateAsync(design.Canvas,
            InjectLight("source", MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o1")));
        double outputBefore = Amplitude(fieldsBefore,
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_taper_port 2").LogicalPin!.IDOutFlow);

        var saveVm = CreateFileOperations(design.Canvas, design.Templates);
        saveVm.ProcessCatalogProvider = () => BuildProcessCatalog(design);
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
        loadVm.ProcessCatalogProvider = () => BuildProcessCatalog(design);
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

        // Since #938 the round-trip is lossless: the per-chiplet bindings fully describe
        // the two-process state, so nothing migrates and no "not manufacturable" warning
        // fires. The canvas-level default stays Playground — the honest state for a
        // carrier that genuinely mixes two processes (step 7 proves the bindings).
        loadVm.ActiveProcess.ShouldNotBeNull();
        loadVm.ActiveProcess!.IsPlayground.ShouldBeTrue(
            "Step 6: a two-process carrier's canvas-level default is Playground — " +
            "manufacturability lives in the per-chiplet bindings (#938)");
        migrationWarning.ShouldBeNull(
            "Step 6: with per-chiplet bindings persisted, the reload has nothing to migrate (#938)");

        var loadedFields = await SimulateAsync(loadedCanvas,
            InjectLight("source", MultiProcessChipletJourneyDesign.ExposedPin(loadedA, "cs_coupler_o1")));
        Amplitude(loadedFields,
                MultiProcessChipletJourneyDesign.ExposedPin(loadedB, "si_taper_port 2").LogicalPin!.IDOutFlow)
            .ShouldBe(outputBefore, AmplitudeTolerance,
                "Step 6: the reloaded two-process system delivers the same output power");
    }

    [Fact]
    public async Task Step7_LunRoundTrip_PerChipletProcessBindingSurvives()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var saveVm = CreateFileOperations(design.Canvas, design.Templates);
        saveVm.ProcessCatalogProvider = () => BuildProcessCatalog(design);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

        // The binding of each top-level group is persisted inside the file itself.
        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldContain("\"ProcessBinding\"", Case.Sensitive,
            "Step 7: each chiplet's process binding must be written into the .lun (#938)");

        var loadedCanvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(loadedCanvas, design.Templates);
        loadVm.ProcessCatalogProvider = () => BuildProcessCatalog(design);
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
        var loadedA = loadedGroups.Single(g => g.Identifier == design.ChipletA.Identifier);
        var loadedB = loadedGroups.Single(g => g.Identifier == design.ChipletB.Identifier);

        // Rung 6: chiplet A reloads bound to the Cornerstone process and chiplet B to
        // the SiEPIC process — the design as a whole is not one process.
        loadedA.ProcessBinding.ShouldNotBeNull("Step 7: chiplet A's process binding survives (#938)");
        loadedA.ProcessBinding!.IsPlayground.ShouldBeFalse(
            "Step 7: a bound chiplet is a first-class manufacturable state, not Playground");
        loadedA.ProcessBinding.MemberPdkNames.ShouldContain(design.Cornerstone.Name);
        loadedB.ProcessBinding.ShouldNotBeNull("Step 7: chiplet B's process binding survives (#938)");
        loadedB.ProcessBinding!.IsPlayground.ShouldBeFalse(
            "Step 7: a bound chiplet is a first-class manufacturable state, not Playground");
        loadedB.ProcessBinding.MemberPdkNames.ShouldContain(design.Siepic.Name);

        migrationWarning.ShouldBeNull(
            "Step 7: the persisted bindings describe the design completely — no Playground migration");
        loadVm.ActiveProcess.ShouldNotBeNull(
            "Step 7: a loaded design never falls back to the unset state (that would re-open the process picker)");
        loadVm.ActiveProcess!.IsPlayground.ShouldBeTrue(
            "Step 7: the canvas-level default for a genuine two-process carrier is Playground; " +
            "the per-chiplet bindings above carry the manufacturability (#938)");
    }

    /// <summary>
    /// Backward compatibility (#938 acceptance): a .lun saved before per-chiplet
    /// bindings existed (no catalog at save time → no bindings in the file) loads
    /// exactly as today — Playground plus the multi-process migration warning.
    /// </summary>
    [Fact]
    public async Task Step7_LegacyFileWithoutBindings_LoadsExactlyAsBefore()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var saveVm = CreateFileOperations(design.Canvas, design.Templates);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldNotContain("\"ProcessBinding\"", Case.Sensitive,
            "a pre-#938 save carries no per-chiplet binding — this test pins that legacy shape");

        var loadedCanvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(loadedCanvas, design.Templates);
        loadVm.ProcessCatalogProvider = () => BuildProcessCatalog(design);
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
        loadedGroups.ShouldAllBe(g => g.ProcessBinding == null,
            "legacy files restore no bindings — placement derives the scope live as before (#935)");
        loadVm.ActiveProcess.ShouldNotBeNull();
        loadVm.ActiveProcess!.IsPlayground.ShouldBeTrue(
            "legacy two-process designs still migrate to Playground");
        migrationWarning.ShouldNotBeNull();
        migrationWarning!.ShouldContain("multiple processes");
    }

    /// <summary>The production process catalog over the journey's two bundled PDKs.</summary>
    private static IReadOnlyList<ProcessGroup> BuildProcessCatalog(MultiProcessChipletJourneyDesign design) =>
        ProcessCatalog.BuildGroups(new[]
        {
            new PdkProcessEntry(design.Cornerstone.Name, ProcessFingerprintFactory.From(design.Cornerstone)),
            new PdkProcessEntry(design.Siepic.Name, ProcessFingerprintFactory.From(design.Siepic)),
        });
}
