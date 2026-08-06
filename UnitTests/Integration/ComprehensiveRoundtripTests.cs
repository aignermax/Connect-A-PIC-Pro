using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Moq;
using Shouldly;
using System.Collections.ObjectModel;

namespace UnitTests.Integration;

/// <summary>
/// Comprehensive save/load roundtrip tests for regular components and waveguide connections.
/// Verifies ALL critical properties are preserved through a complete file save/load cycle.
/// Covers issue #316.
/// </summary>
public class ComprehensiveRoundtripTests
{
    private readonly ObservableCollection<ComponentTemplate> _library;

    /// <summary>Initializes the test suite with the full component library.</summary>
    public ComprehensiveRoundtripTests()
    {
        _library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
    }

    /// <summary>
    /// Verifies ALL regular component properties: Identifier, HumanReadableName,
    /// PhysicalX/Y, Rotation, IsLocked, SliderValue, and LaserConfig.
    /// </summary>
    [Fact]
    public async Task RegularComponent_AllProperties_PreservedAfterRoundtrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"roundtrip_regular_{Guid.NewGuid():N}.cappro");
        try
        {
            // Arrange: Directional Coupler (has slider)
            var (saveVm, saveCanvas) = CreateSetup();
            var dcTemplate = _library.First(t => t.Name == "Directional Coupler");
            var dc = ComponentTemplates.CreateFromTemplate(dcTemplate, 100, 200);
            dc.Identifier = "dc_test_1";
            dc.HumanReadableName = "My DC Component";
            var dcVm = saveCanvas.AddComponent(dc, dcTemplate.Name);
            dcVm.SliderValue = 75.0;

            // Arrange: Grating Coupler (has LaserConfig)
            var gcTemplate = _library.First(t => t.Name == "Grating Coupler");
            var gc = ComponentTemplates.CreateFromTemplate(gcTemplate, 300, 50);
            gc.Identifier = "gc_test_1";
            var gcVm = saveCanvas.AddComponent(gc, gcTemplate.Name);
            gcVm.LaserConfig!.WavelengthNm = 1310;
            gcVm.LaserConfig!.InputPower = 0.8;
            gcVm.LaserConfig!.LineShape = CAP_Core.ExternalPorts.LaserSpectrum.LaserLineShape.Lorentzian;
            gcVm.LaserConfig!.LinewidthFwhmNm = 3.5;
            gcVm.LaserConfig!.RinDbPerHz = -130;

            // Arrange: MMI (rotated + locked)
            var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
            var mmi = ComponentTemplates.CreateFromTemplate(mmiTemplate, 500, 100);
            mmi.Identifier = "mmi_locked_1";
            mmi.HumanReadableName = "Locked MMI";
            mmi.IsLocked = true;
            var mmiVm = saveCanvas.AddComponent(mmi, mmiTemplate.Name);
            // Apply one rotation
            mmi.Rotation90CounterClock = DiscreteRotation.R90;

            await SaveToFile(saveVm, tempFile);

            // Act: Load
            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            // Assert: Directional Coupler
            var loadedDcVm = loadCanvas.Components.First(c => c.Component.Identifier == "dc_test_1");
            loadedDcVm.Component.HumanReadableName.ShouldBe("My DC Component",
                "HumanReadableName must survive roundtrip");
            loadedDcVm.Component.PhysicalX.ShouldBe(100, "PhysicalX must survive roundtrip");
            loadedDcVm.Component.PhysicalY.ShouldBe(200, "PhysicalY must survive roundtrip");
            loadedDcVm.SliderValue.ShouldBe(75.0, tolerance: 0.01, "SliderValue must survive roundtrip");

            // Assert: Grating Coupler laser config
            var loadedGcVm = loadCanvas.Components.First(c => c.Component.Identifier == "gc_test_1");
            loadedGcVm.IsLightSource.ShouldBeTrue("Loaded GC must still be a light source");
            loadedGcVm.LaserConfig!.WavelengthNm.ShouldBe(1310, "LaserWavelengthNm must survive roundtrip");
            loadedGcVm.LaserConfig!.InputPower.ShouldBe(0.8, tolerance: 0.001, "LaserPower must survive roundtrip");
            loadedGcVm.LaserConfig!.LineShape.ShouldBe(
                CAP_Core.ExternalPorts.LaserSpectrum.LaserLineShape.Lorentzian,
                "LaserLineShape must survive roundtrip (#819)");
            loadedGcVm.LaserConfig!.LinewidthFwhmNm.ShouldBe(3.5, tolerance: 0.001,
                "LaserLinewidthFwhmNm must survive roundtrip (#819)");
            loadedGcVm.LaserConfig!.RinDbPerHz.ShouldBe(-130, tolerance: 0.001,
                "LaserRinDbPerHz must survive roundtrip (#819)");

            // Assert: Locked MMI
            var loadedMmiVm = loadCanvas.Components.First(c => c.Component.Identifier == "mmi_locked_1");
            loadedMmiVm.Component.HumanReadableName.ShouldBe("Locked MMI", "HumanReadableName must survive roundtrip");
            loadedMmiVm.Component.IsLocked.ShouldBeTrue("IsLocked must survive roundtrip");
            ((int)loadedMmiVm.Component.Rotation90CounterClock).ShouldBe(
                (int)DiscreteRotation.R90, "Rotation must survive roundtrip");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Verifies waveguide connection properties: StartPin/EndPin component mapping,
    /// cached path segments, and lock state.
    /// </summary>
    [Fact]
    public async Task WaveguideConnection_AllProperties_PreservedAfterRoundtrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"roundtrip_conn_{Guid.NewGuid():N}.cappro");
        try
        {
            // Arrange: two MMI components
            var (saveVm, saveCanvas) = CreateSetup();
            var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");

            var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 27.5);
            comp1.Identifier = "conn_mmi_1";
            saveCanvas.AddComponent(comp1, mmiTemplate.Name);

            var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 200, 27.5);
            comp2.Identifier = "conn_mmi_2";
            saveCanvas.AddComponent(comp2, mmiTemplate.Name);

            // Create a connection with a cached path
            var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
            var endPin = comp2.PhysicalPins.First(p => p.Name == "in");
            var cachedPath = new RoutedPath();
            cachedPath.Segments.Add(new StraightSegment(80, 25.5, 200, 27.5, 0));
            var connVm = saveCanvas.ConnectPinsWithCachedRoute(startPin, endPin, cachedPath);
            connVm!.Connection.IsLocked = true;

            await SaveToFile(saveVm, tempFile);

            // Act: Load
            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            // Assert
            loadCanvas.Connections.Count.ShouldBe(1, "Connection must survive roundtrip");
            var loadedConn = loadCanvas.Connections[0].Connection;

            loadedConn.StartPin.ShouldNotBeNull("StartPin must be restored");
            loadedConn.EndPin.ShouldNotBeNull("EndPin must be restored");
            loadedConn.StartPin.Name.ShouldBe("out1", "StartPin name must survive roundtrip");
            loadedConn.EndPin.Name.ShouldBe("in", "EndPin name must survive roundtrip");
            loadedConn.StartPin.ParentComponent.Identifier.ShouldBe("conn_mmi_1",
                "StartPin must belong to correct component after roundtrip");
            loadedConn.EndPin.ParentComponent.Identifier.ShouldBe("conn_mmi_2",
                "EndPin must belong to correct component after roundtrip");

            loadedConn.RoutedPath.ShouldNotBeNull("RoutedPath must be restored");
            loadedConn.RoutedPath!.Segments.Count.ShouldBe(1, "Path segments must survive roundtrip");
            loadedConn.RoutedPath.Segments[0].ShouldBeOfType<StraightSegment>(
                "Path segment type must survive roundtrip");

            loadedConn.IsLocked.ShouldBeTrue("Connection IsLocked must survive roundtrip");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
            new CAP.Avalonia.ViewModels.Export.PhotonTorchExportViewModel(new CAP_Core.Export.PhotonTorchExporter(), canvas),
            null!);
        return (vm, canvas);
    }

    private async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue("Design file must be created during save");
    }

    private async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }
}
