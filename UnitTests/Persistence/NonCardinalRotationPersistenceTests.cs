using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using System.Collections.ObjectModel;

namespace UnitTests.Persistence;

/// <summary>
/// Regression tests for issue #872: a GDS-imported instance at a non-cardinal
/// angle (e.g. 330°) and/or with a STRANS-mirrored pin layout must survive a
/// .lun save/load cycle — both standalone and as a group child. Old-format
/// cardinal designs must keep the compact file format (no new JSON fields).
/// </summary>
public class NonCardinalRotationPersistenceTests
{
    private const double Tolerance = 1e-9;
    private const double ExactAngle = 330;

    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Fact]
    public async Task StandaloneComponent_NonCardinalRotationAndMirror_SurviveRoundtrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pose_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas) = CreateSetup();
            var component = PlaceTemplateComponent(saveCanvas, "rotated_mirrored", 100, 100);
            ComponentPoseTransform.MirrorPinsHorizontally(component);
            ComponentPoseTransform.ApplyExactRotation(component, ExactAngle);
            var expectedPins = SnapshotPins(component);

            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Components
                .First(c => c.Component.Identifier == "rotated_mirrored").Component;
            loaded.RotationDegrees.ShouldBe(ExactAngle, Tolerance);
            loaded.IsMirroredHorizontally.ShouldBeTrue();
            AssertPinsMatch(loaded, expectedPins);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GroupChild_NonCardinalRotationAndMirror_SurviveRoundtrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pose_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas) = CreateSetup();
            var posed = PlaceTemplateComponent(saveCanvas, "child_posed", 0, 0);
            ComponentPoseTransform.MirrorPinsHorizontally(posed);
            ComponentPoseTransform.ApplyExactRotation(posed, ExactAngle);
            var plain = PlaceTemplateComponent(saveCanvas, "child_plain", 600, 0);
            var expectedPins = SnapshotPins(posed);

            var members = saveCanvas.Components
                .Where(c => c.Component == posed || c.Component == plain).ToList();
            new CreateGroupCommand(saveCanvas, members).Execute();

            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var group = (ComponentGroup)loadCanvas.Components
                .First(c => c.Component is ComponentGroup).Component;
            var loadedChild = group.ChildComponents
                .First(c => c.Identifier == "child_posed");
            loadedChild.RotationDegrees.ShouldBe(ExactAngle, Tolerance);
            loadedChild.IsMirroredHorizontally.ShouldBeTrue();
            AssertPinsMatch(loadedChild, expectedPins);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CardinalUnmirroredDesign_OmitsNewJsonFields()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pose_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas) = CreateSetup();
            var component = PlaceTemplateComponent(saveCanvas, "cardinal_only", 100, 100);
            ComponentPoseTransform.Rotate90CounterClockwise(component);

            await SaveToFile(saveVm, tempFile);

            var json = await File.ReadAllTextAsync(tempFile);
            json.ShouldNotContain("\"RotationDegrees\"",
                customMessage: "cardinal designs must keep the compact legacy format");
            json.ShouldNotContain("\"Mirrored\"",
                customMessage: "unmirrored designs must keep the compact legacy format");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task QuarterTurnPlusExactRotation_RestoresCombinedPose()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pose_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas) = CreateSetup();
            var component = PlaceTemplateComponent(saveCanvas, "combined_pose", 100, 100);
            ComponentPoseTransform.Rotate90CounterClockwise(component);
            ComponentPoseTransform.ApplyExactRotation(component, 120);
            var expectedPins = SnapshotPins(component);

            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Components
                .First(c => c.Component.Identifier == "combined_pose").Component;
            loaded.RotationDegrees.ShouldBe(120, Tolerance);
            loaded.Rotation90CounterClock.ShouldBe(DiscreteRotation.R90);
            AssertPinsMatch(loaded, expectedPins);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Helpers (same harness as MultiSliderPersistenceTests) ────────────────

    private Component PlaceTemplateComponent(
        DesignCanvasViewModel canvas, string identifier, double x, double y)
    {
        var template = _library.First(t => t.Name == "1x2 MMI Splitter");
        var component = ComponentTemplates.CreateFromTemplate(template, x, y);
        component.Identifier = identifier;
        canvas.AddComponent(component, template.Name);
        return component;
    }

    private static List<(string Name, double X, double Y, double Angle)> SnapshotPins(
        Component component) =>
        component.PhysicalPins
            .Select(p => (p.Name, p.OffsetXMicrometers, p.OffsetYMicrometers, p.AngleDegrees))
            .ToList();

    private static void AssertPinsMatch(
        Component loaded, List<(string Name, double X, double Y, double Angle)> expected)
    {
        foreach (var (name, x, y, angle) in expected)
        {
            var pin = loaded.PhysicalPins.First(p => p.Name == name);
            pin.OffsetXMicrometers.ShouldBe(x, Tolerance, $"pin {name} offset X");
            pin.OffsetYMicrometers.ShouldBe(y, Tolerance, $"pin {name} offset Y");
            pin.AngleDegrees.ShouldBe(angle, Tolerance, $"pin {name} angle");
        }
    }

    private (FileOperationsViewModel vm, DesignCanvasViewModel canvas) CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new CAP_Core.Export.SaxExporter(),
            _library,
            new GdsExportViewModel(new CAP_Core.Export.GdsExportService()),
            new PhotonTorchExportViewModel(new CAP_Core.Export.PhotonTorchExporter(), canvas),
            null!);
        return (vm, canvas);
    }

    private static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue("Design file must be created during save");
    }

    private static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }
}
