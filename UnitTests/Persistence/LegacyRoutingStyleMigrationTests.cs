using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using System.Collections.ObjectModel;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The routing styles "Straight" and "Euler" were removed from <see cref="WaveguideType"/>.
/// Designs saved before the removal must still load without crashing: "Euler" migrates to
/// <see cref="WaveguideType.Bend"/> (it was drawn as the same generous arc), "Straight" and
/// any other unknown style name fall back to <see cref="WaveguideType.Auto"/>.
/// </summary>
public class LegacyRoutingStyleMigrationTests
{
    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Theory]
    [InlineData("Euler", WaveguideType.Bend)]
    [InlineData("Straight", WaveguideType.Auto)]
    [InlineData("SomeFutureStyle", WaveguideType.Auto)]
    [InlineData("Bend", WaveguideType.Bend)]
    [InlineData("SBend", WaveguideType.SBend)]
    [InlineData("Cobra", WaveguideType.Cobra)]
    public async Task LoadDesign_WithSavedRoutingStyle_MigratesToSupportedStyle(
        string savedStyle, WaveguideType expected)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"legacy_style_{Guid.NewGuid():N}.cappro");
        try
        {
            await SaveDesignWithStyledConnection(tempFile);

            // Rewrite the saved style to the legacy/unknown name under test.
            var json = await File.ReadAllTextAsync(tempFile);
            json = json.Replace("\"RoutingStyle\": \"Cobra\"", $"\"RoutingStyle\": \"{savedStyle}\"");
            await File.WriteAllTextAsync(tempFile, json);

            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            loadCanvas.Connections.Count.ShouldBe(1, "the design must load without crashing");
            loadCanvas.Connections[0].Connection.Type.ShouldBe(expected);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>Saves a two-component design whose single connection carries a Cobra style —
    /// a placeholder the test rewrites to the legacy style name in the raw JSON.</summary>
    private async Task SaveDesignWithStyledConnection(string tempFile)
    {
        var (saveVm, saveCanvas) = CreateSetup();
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");

        var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 27.5);
        comp1.Identifier = "legacy_mmi_1";
        saveCanvas.AddComponent(comp1, mmiTemplate.Name);
        var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 200, 27.5);
        comp2.Identifier = "legacy_mmi_2";
        saveCanvas.AddComponent(comp2, mmiTemplate.Name);

        var startPin = comp1.PhysicalPins.First(p => p.Name == "out1");
        var endPin = comp2.PhysicalPins.First(p => p.Name == "in");
        var connVm = await saveCanvas.ConnectPinsAsync(startPin, endPin);
        connVm.ShouldNotBeNull();
        connVm!.Connection.Type = WaveguideType.Cobra;

        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(tempFile);
        saveVm.FileDialogService = dialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(tempFile).ShouldBeTrue("design file must be created during save");
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

    private static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }
}
