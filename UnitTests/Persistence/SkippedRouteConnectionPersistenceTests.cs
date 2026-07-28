using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
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
using System.Collections.ObjectModel;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// A connection's <see cref="RoutedPath.IsInvalidGeometry"/> and
/// <see cref="RoutedPath.IsPlaceholderGeometry"/> flags must survive a Save/Load round trip —
/// before this, only <see cref="RoutedPath.IsBlockedFallback"/> was persisted, so a broken
/// route reloaded as an ordinary cached route and re-exported (still broken) without any
/// warning. Mirrors the Save/Load helpers in <c>PinCalibrationMigrationTests</c>.
/// </summary>
public class SkippedRouteConnectionPersistenceTests
{
    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Fact]
    public async Task Load_PlaceholderGeometryConnection_StaysPlaceholderAndSkippedOnReexport()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"skip_persist_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");

            var (sx, sy) = startPin.GetAbsolutePosition();
            var (ex, ey) = endPin.GetAbsolutePosition();
            var placeholderPath = new RoutedPath { IsPlaceholderGeometry = true };
            placeholderPath.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
            saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, placeholderPath);
            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.RoutedPath.ShouldNotBeNull();
            loaded.RoutedPath!.IsPlaceholderGeometry.ShouldBeTrue(
                "the placeholder flag must survive the round trip, not silently reset to false");

            var nazcaSkipped = new List<string>();
            new SimpleNazcaExporter().Export(loadCanvas, skippedConnections: nazcaSkipped);
            nazcaSkipped.Count.ShouldBe(1);

            var gdsSkipped = new List<string>();
            new GdsFactoryExporter().Export(
                loadCanvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
                skippedConnections: gdsSkipped);
            gdsSkipped.Count.ShouldBe(1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_InvalidGeometryConnection_StaysInvalidAndSkippedOnReexport()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"skip_persist_invalid_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");

            var (sx, sy) = startPin.GetAbsolutePosition();
            var (ex, ey) = endPin.GetAbsolutePosition();
            var invalidPath = new RoutedPath { IsInvalidGeometry = true };
            invalidPath.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
            saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, invalidPath);
            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.RoutedPath!.IsInvalidGeometry.ShouldBeTrue(
                "the invalid-geometry flag must survive the round trip, not silently reset to false");

            var skipped = new List<string>();
            new SimpleNazcaExporter().Export(loadCanvas, skippedConnections: skipped);
            skipped.Count.ShouldBe(1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_LegacyConnectionWithoutCachedRoute_NeverRoutes_ButStillExportsPinToPin()
    {
        // A connection saved before it was ever routed (or a legacy file predating cached
        // routes) writes no CachedSegments; ConnectPins on load does not trigger routing
        // either, so the connection stays routeless until something moves. That routeless
        // state must still export as the direct pin-to-pin fallback, not be skipped.
        var tempFile = Path.Combine(Path.GetTempPath(), $"skip_persist_legacy_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");
            saveCanvas.ConnectPins(startPin, endPin);   // no routing — RoutedPath stays null
            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.RoutedPath.ShouldBeNull(
                "a legacy/never-routed connection stays routeless after load");

            var skipped = new List<string>();
            var script = new SimpleNazcaExporter().Export(loadCanvas, skippedConnections: skipped);

            script.ShouldContain("ic.sbend_p2p");
            skipped.ShouldBeEmpty();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_PreMigrationFileFixture_BlockedSingleStraightWithNoPlaceholderField_SkippedAndReportedOnExport()
    {
        // A real .lun file saved between the router's self-crossing degrade-to-blocked-
        // fallback step shipping and IsPlaceholderGeometry being introduced: IsBlockedFallback
        // is true, the cached route is a single straight segment, and the JSON has no
        // IsPlaceholderGeometry key at all (simulated below by stripping it after a normal
        // save, since this build always writes it). Loading such a file must still skip and
        // report the placeholder connection on export, not silently re-export it.
        var tempFile = Path.Combine(Path.GetTempPath(), $"skip_persist_fixture_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (comp1, comp2) = PlaceFacingMmis(saveCanvas);
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");

            var (sx, sy) = startPin.GetAbsolutePosition();
            var (ex, ey) = endPin.GetAbsolutePosition();
            var placeholderPath = new RoutedPath { IsBlockedFallback = true, IsPlaceholderGeometry = true };
            placeholderPath.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
            saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, placeholderPath);
            await SaveToFile(saveVm, tempFile);

            // Downgrade the freshly-saved file to the pre-migration shape: remove the
            // IsPlaceholderGeometry key entirely (a real old file never wrote it).
            var json = await File.ReadAllTextAsync(tempFile);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            root["Connections"]![0]!.AsObject().Remove("IsPlaceholderGeometry");
            await File.WriteAllTextAsync(tempFile, root.ToJsonString());

            var (loadVm, loadCanvas, _) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Connections.ShouldHaveSingleItem().Connection;
            loaded.RoutedPath!.IsPlaceholderGeometry.ShouldBeTrue(
                "the pre-migration shape (blocked, one straight segment) must be inferred as a placeholder");

            var skipped = new List<string>();
            var script = new SimpleNazcaExporter().Export(loadCanvas, skippedConnections: skipped);

            script.ShouldNotContain("nd.strt(");
            skipped.Count.ShouldBe(1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Helpers (mirrors PinCalibrationMigrationTests / ComprehensiveRoundtripTests) ──────

    private (Component Component1, Component Component2) PlaceFacingMmis(DesignCanvasViewModel canvas)
    {
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
        var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 27.5);
        comp1.Identifier = "skip_mmi_1";
        canvas.AddComponent(comp1, mmiTemplate.Name);
        var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 200, 27.5);
        comp2.Identifier = "skip_mmi_2";
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
