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
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The import source-layer tag of a LIVE connection (<see cref="CAP_Core.Components.Connections.WaveguideConnection.SourceGdsLayer"/>)
/// must survive a Save/Load round trip — a re-routed GDS connection the user saves and
/// reloads still exports on the layer its route polygons were drawn on. Files that
/// predate the field load untagged, leaving the process-default export unchanged.
/// Mirrors the Save/Load helpers of <see cref="SkippedRouteConnectionPersistenceTests"/>.
/// </summary>
public class ConnectionSourceLayerPersistenceTests
{
    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Fact]
    public async Task SaveLoad_TaggedConnection_RoundTripsSourceLayerAndExportsOnIt()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"sourcelayer_persist_{Guid.NewGuid():N}.lun");
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
            connVm!.Connection.SourceGdsLayer = 3;
            connVm.Connection.SourceGdsDataType = 1;
            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.SourceGdsLayer.ShouldBe(3, "the import's layer tag must survive the round trip");
            loaded.SourceGdsDataType.ShouldBe(1);

            var script = new SimpleNazcaExporter().Export(loadCanvas);
            script.ShouldContain("layer=(3, 1)",
                customMessage: "the reloaded connection still exports on its source layer");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_PreSourceLayerFile_LoadsUntagged()
    {
        // A file saved before the tag existed carries no SourceGdsLayer/SourceGdsDataType
        // keys at all (simulated by stripping them after a normal save): the connection
        // must load untagged and export exactly like before.
        var tempFile = Path.Combine(Path.GetTempPath(), $"sourcelayer_legacy_{Guid.NewGuid():N}.lun");
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
            root["Connections"]![0]!.AsObject().Remove("SourceGdsLayer");
            root["Connections"]![0]!.AsObject().Remove("SourceGdsDataType");
            await File.WriteAllTextAsync(tempFile, root.ToJsonString());

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.SourceGdsLayer.ShouldBeNull();
            loaded.SourceGdsDataType.ShouldBeNull();

            var script = new SimpleNazcaExporter().Export(loadCanvas);
            script.ShouldNotContain("layer=(3, 1)");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Helpers (mirrors SkippedRouteConnectionPersistenceTests) ─────────────

    private (Component Component1, Component Component2) PlaceFacingMmis(DesignCanvasViewModel canvas)
    {
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
        var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 27.5);
        comp1.Identifier = "layer_mmi_1";
        canvas.AddComponent(comp1, mmiTemplate.Name);
        var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 200, 27.5);
        comp2.Identifier = "layer_mmi_2";
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
