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
/// Green .lun round-trip station of the multi-process journey (step 6) — see
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
}
