using System.Collections.ObjectModel;
using System.Numerics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Pins the type-wide effect of saving a component definition (PR #742): placed instances
/// snapshot their S-matrix at placement time, so after an editor save the new PDK matrices
/// must be pushed into every matching live instance — while per-instance and user-global
/// overrides keep winning over the refreshed PDK default.
/// </summary>
public class TemplateSaveInstanceRefreshTests : IDisposable
{
    private readonly string _tempStorePath =
        Path.Combine(Path.GetTempPath(), $"sparam-overrides-{Guid.NewGuid()}.json");

    public void Dispose() { if (File.Exists(_tempStorePath)) File.Delete(_tempStorePath); }

    private static FileOperationsViewModel BuildFileOps(
        DesignCanvasViewModel canvas,
        ObservableCollection<ComponentTemplate> library,
        UserSMatrixOverrideStore? userStore = null)
    {
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!, // verilogAExport — not exercised here
            errorConsole: null,
            userSMatrixOverrideStore: userStore);
    }

    private static ComponentTemplate BuildTemplate(double transmission1550)
    {
        return new ComponentTemplate
        {
            Name = "TestCoupler",
            Category = "Test",
            PdkSource = "test-pdk",
            NazcaFunctionName = "nazca_testcoupler",
            WidthMicrometers = 10,
            HeightMicrometers = 1,
            PinDefinitions = new[]
            {
                new PinDefinition("in", 0, 0.5, 180),
                new PinDefinition("out", 10, 0.5, 0)
            },
            CreateWavelengthSMatrixMap = pins =>
            {
                var allIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
                var sm = new SMatrix(allIds, new List<(Guid, double)>());
                sm.SetValues(new Dictionary<(Guid, Guid), Complex>
                {
                    { (pins[0].IDInFlow, pins[1].IDOutFlow), new Complex(transmission1550, 0) },
                    { (pins[1].IDInFlow, pins[0].IDOutFlow), new Complex(transmission1550, 0) },
                });
                return new Dictionary<int, SMatrix> { { 1550, sm } };
            }
        };
    }

    private static ComponentSMatrixData OverrideData(double magnitude)
    {
        var data = new ComponentSMatrixData { SourceNote = "Test override" };
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2,
            Cols = 2,
            Real = new List<double> { 0.0, magnitude, magnitude, 0.0 },
            Imag = new List<double> { 0, 0, 0, 0 },
            PortNames = new List<string> { "in", "out" }
        };
        return data;
    }

    private static double MaxMagnitude(SMatrix sMatrix)
    {
        double max = 0;
        for (int r = 0; r < sMatrix.SMat.RowCount; r++)
            for (int c = 0; c < sMatrix.SMat.ColumnCount; c++)
                max = Math.Max(max, sMatrix.SMat[r, c].Magnitude);
        return max;
    }

    [Fact]
    public void RefreshInstancesFromTemplate_pushesTheNewPdkMatricesIntoPlacedInstances()
    {
        var oldTemplate = BuildTemplate(transmission1550: 0.1);
        var library = new ObservableCollection<ComponentTemplate> { oldTemplate };
        var canvas = new DesignCanvasViewModel();
        var fileOps = BuildFileOps(canvas, library);

        var instance = ComponentTemplates.CreateFromTemplate(oldTemplate, 0, 0);
        canvas.Components.Add(new ComponentViewModel(instance));
        MaxMagnitude(instance.WaveLengthToSMatrixMap[1550]).ShouldBe(0.1, 1e-9);

        // The editor save replaced the library template with a freshly computed definition.
        var newTemplate = BuildTemplate(transmission1550: 0.5);
        library[0] = newTemplate;
        fileOps.RefreshInstancesFromTemplate(newTemplate);

        MaxMagnitude(instance.WaveLengthToSMatrixMap[1550]).ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void RefreshInstancesFromTemplate_leavesInstancesOfOtherTemplatesAlone()
    {
        var template = BuildTemplate(transmission1550: 0.1);
        var other = BuildTemplate(transmission1550: 0.2);
        other.Name = "OtherComp";
        other.NazcaFunctionName = "nazca_other";
        var library = new ObservableCollection<ComponentTemplate> { template, other };
        var canvas = new DesignCanvasViewModel();
        var fileOps = BuildFileOps(canvas, library);

        var otherInstance = ComponentTemplates.CreateFromTemplate(other, 0, 0);
        canvas.Components.Add(new ComponentViewModel(otherInstance));

        var newTemplate = BuildTemplate(transmission1550: 0.5);
        library[0] = newTemplate;
        fileOps.RefreshInstancesFromTemplate(newTemplate);

        MaxMagnitude(otherInstance.WaveLengthToSMatrixMap[1550]).ShouldBe(0.2, 1e-9);
    }

    [Fact]
    public void RefreshInstancesFromTemplate_perInstanceOverrideStillWins()
    {
        var oldTemplate = BuildTemplate(transmission1550: 0.1);
        var library = new ObservableCollection<ComponentTemplate> { oldTemplate };
        var canvas = new DesignCanvasViewModel();
        var fileOps = BuildFileOps(canvas, library);

        var instance = ComponentTemplates.CreateFromTemplate(oldTemplate, 0, 0);
        canvas.Components.Add(new ComponentViewModel(instance));
        fileOps.StoredSMatrices[instance.Identifier] = OverrideData(0.7);

        var newTemplate = BuildTemplate(transmission1550: 0.5);
        library[0] = newTemplate;
        fileOps.RefreshInstancesFromTemplate(newTemplate);

        MaxMagnitude(instance.WaveLengthToSMatrixMap[1550]).ShouldBe(0.7, 1e-9);
    }

    [Fact]
    public void RefreshInstancesFromTemplate_userGlobalOverrideStillWins()
    {
        var oldTemplate = BuildTemplate(transmission1550: 0.1);
        var library = new ObservableCollection<ComponentTemplate> { oldTemplate };
        var canvas = new DesignCanvasViewModel();
        var userStore = new UserSMatrixOverrideStore(_tempStorePath);
        userStore.Apply($"{oldTemplate.PdkSource}::{oldTemplate.Name}", OverrideData(0.7));
        var fileOps = BuildFileOps(canvas, library, userStore);

        var instance = ComponentTemplates.CreateFromTemplate(oldTemplate, 0, 0);
        canvas.Components.Add(new ComponentViewModel(instance));

        var newTemplate = BuildTemplate(transmission1550: 0.5);
        library[0] = newTemplate;
        fileOps.RefreshInstancesFromTemplate(newTemplate);

        MaxMagnitude(instance.WaveLengthToSMatrixMap[1550]).ShouldBe(0.7, 1e-9);
    }
}
