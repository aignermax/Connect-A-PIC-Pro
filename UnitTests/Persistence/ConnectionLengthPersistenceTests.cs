using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Routing.MeanderGeneration;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The meander length intent of a connection (<see cref="CAP_Core.Components.Connections.WaveguideConnection.TargetLengthMicrometers"/>)
/// must survive a Save/Load round trip together with the meandered geometry (issue #1008):
/// a reloaded connection keeps its target+tolerance, and re-applying the matcher re-derives
/// the identical geometry. Files that predate the field load without intent, unchanged.
/// Mirrors the Save/Load helpers of <see cref="ConnectionSourceLayerPersistenceTests"/>.
/// </summary>
public class ConnectionLengthPersistenceTests
{
    private const double AssertSlack = 1e-6;
    private const double Tolerance = 1.0;

    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Fact]
    public async Task SaveLoad_MeanderedConnection_KeepsTargetToleranceAndGeometry()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"length_persist_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");

            var (sx, sy) = startPin.GetAbsolutePosition();
            var (ex, ey) = endPin.GetAbsolutePosition();
            var path = new RoutedPath();
            path.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
            var connVm = saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, path);

            var components = saveCanvas.Components.Select(vm => vm.Component).ToList();
            var connection = connVm!.Connection;
            double target = 3.0 * connection.PathLengthMicrometers;
            var applied = new ConnectionLengthMatcher().ApplyTargetLength(
                connection, components, target, Tolerance);
            applied.IsSuccess.ShouldBeTrue(applied.FailureMessage);
            double meanderedLength = connection.PathLengthMicrometers;
            int meanderedSegmentCount = connection.RoutedPath!.Segments.Count;
            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.TargetLengthMicrometers.ShouldBe(target,
                "the meander target must survive the round trip");
            loaded.LengthToleranceMicrometers.ShouldBe(Tolerance);
            loaded.PathLengthMicrometers.ShouldBe(meanderedLength, AssertSlack);
            loaded.RoutedPath!.Segments.Count.ShouldBe(meanderedSegmentCount);

            var rederived = new ConnectionLengthMatcher().ApplyTargetLength(
                loaded, loadCanvas.Components.Select(vm => vm.Component).ToList(),
                loaded.TargetLengthMicrometers.Value, loaded.LengthToleranceMicrometers.Value);
            rederived.IsSuccess.ShouldBeTrue(rederived.FailureMessage);
            loaded.PathLengthMicrometers.ShouldBe(meanderedLength, AssertSlack);
            loaded.RoutedPath!.Segments.Count.ShouldBe(meanderedSegmentCount);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_PreTargetLengthFile_LoadsWithoutLengthIntent()
    {
        // A file saved before the length intent existed carries no TargetLengthMicrometers/
        // LengthToleranceMicrometers keys at all (simulated by stripping them after a
        // normal save): the connection must load without intent, unchanged.
        var tempFile = Path.Combine(Path.GetTempPath(), $"length_legacy_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");

            var (sx, sy) = startPin.GetAbsolutePosition();
            var (ex, ey) = endPin.GetAbsolutePosition();
            var path = new RoutedPath();
            path.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
            saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, path);
            await SaveToFile(saveVm, tempFile);

            var json = await File.ReadAllTextAsync(tempFile);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            root["Connections"]![0]!.AsObject().Remove("TargetLengthMicrometers");
            root["Connections"]![0]!.AsObject().Remove("LengthToleranceMicrometers");
            await File.WriteAllTextAsync(tempFile, root.ToJsonString());

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.TargetLengthMicrometers.ShouldBeNull();
            loaded.LengthToleranceMicrometers.ShouldBeNull();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Helpers (mirrors ConnectionSourceLayerPersistenceTests) ─────────────

    private (Component Component1, Component Component2) PlaceFacingMmis(DesignCanvasViewModel canvas)
    {
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
        var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 27.5);
        comp1.Identifier = "length_mmi_1";
        canvas.AddComponent(comp1, mmiTemplate.Name);
        var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 200, 27.5);
        comp2.Identifier = "length_mmi_2";
        canvas.AddComponent(comp2, mmiTemplate.Name);
        return (comp1, comp2);
    }

    private (FileOperationsViewModel vm, DesignCanvasViewModel canvas, ErrorConsoleService errorConsole)
        CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var errorConsole = new ErrorConsoleService();
        var vm = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            _library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!,
            errorConsole: errorConsole);
        return (vm, canvas, errorConsole);
    }

    private static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue();
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
