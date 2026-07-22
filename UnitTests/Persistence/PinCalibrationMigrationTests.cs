using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Moq;
using Shouldly;
using System.Collections.ObjectModel;

namespace UnitTests.Persistence;

/// <summary>
/// Round-5 review [2] — pin-calibration migration on design load. The DC-Halfring
/// pin-data fix flipped port angles while keeping pin positions, so saved designs
/// carry cached routes whose endpoints still match but whose docking direction now
/// runs against the port. Loading such a design must discard the stale geometry
/// (including its frozen state), re-route, and tell the user via the error console —
/// never silently keep the wrong pins or the wrong geometry.
/// </summary>
public class PinCalibrationMigrationTests
{
    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Fact]
    public async Task Load_RouteDockingAgainstCurrentPinAngles_IsRerouted_WithConsoleHint()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pincal_migrate_{Guid.NewGuid():N}.lun");
        try
        {
            // Arrange: a design whose cached route was built under the OLD calibration —
            // it leaves the start pin backwards (180° instead of the pin's current 0°).
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");

            var (sx, sy) = startPin.GetAbsolutePosition();
            var stalePath = new RoutedPath();
            stalePath.Segments.Add(new StraightSegment(sx, sy, sx - 120, sy, 180));
            var connVm = saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, stalePath);
            connVm!.Connection.IsRouteFrozen = true;
            connVm.Connection.BendRadiusOverrides[0] = 12.5;
            await SaveToFile(saveVm, tempFile);

            // Act
            var (loadVm, loadCanvas, errorConsole) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);
            await loadCanvas.RecalculateRoutesAsync();

            // Assert: stale geometry and its frozen state are gone, the route is fresh…
            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.IsRouteFrozen.ShouldBeFalse(
                "the frozen flag described exactly the discarded stale geometry");
            loaded.BendRadiusOverrides.ShouldBeEmpty();
            loaded.RoutedPath.ShouldNotBeNull("the load path must re-route, not keep nothing");
            CachedRouteValidator.CheckPinDirections(
                    loaded.StartPin, loaded.EndPin, loaded.RoutedPath!)
                .ShouldBe((true, true), "the fresh route must dock along the CURRENT pin angles");

            // …and the user is told which component's calibration changed.
            errorConsole.Entries.ShouldContain(e => e.Message.Contains("cal_mmi_1"),
                "the error console must name the migrated component");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_RouteMatchingCurrentPinAngles_KeepsGeometryAndFrozenState()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pincal_keep_{Guid.NewGuid():N}.lun");
        try
        {
            // Arrange: a healthy design — the cached route docks along the pin angles.
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");

            var (sx, sy) = startPin.GetAbsolutePosition();
            var (ex, ey) = endPin.GetAbsolutePosition();
            var goodPath = new RoutedPath();
            goodPath.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
            var connVm = saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, goodPath);
            connVm!.Connection.IsRouteFrozen = true;
            await SaveToFile(saveVm, tempFile);

            // Act
            var (loadVm, loadCanvas, errorConsole) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            // Assert: no migration — geometry and frozen state survive, no console noise.
            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.IsRouteFrozen.ShouldBeTrue();
            loaded.RoutedPath.ShouldNotBeNull();
            loaded.RoutedPath!.Segments.ShouldHaveSingleItem();
            errorConsole.Entries.ShouldBeEmpty();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Helpers (mirrors ComprehensiveRoundtripTests) ────────────────────────

    private (Component Component1, Component Component2) PlaceFacingMmis(DesignCanvasViewModel canvas)
    {
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
        var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 27.5);
        comp1.Identifier = "cal_mmi_1";
        canvas.AddComponent(comp1, mmiTemplate.Name);
        var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 200, 27.5);
        comp2.Identifier = "cal_mmi_2";
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
            new CAP_Core.Export.SaxExporter(),
            _library,
            new GdsExportViewModel(new CAP_Core.Export.GdsExportService()),
            new PhotonTorchExportViewModel(new CAP_Core.Export.PhotonTorchExporter(), canvas),
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
