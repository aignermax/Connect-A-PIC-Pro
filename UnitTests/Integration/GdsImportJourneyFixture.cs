using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Moq;
using Shouldly;
using UnitTests.Import.Gds;
using UnitTests.Services.GdsImport;

namespace UnitTests.Integration;

/// <summary>
/// Shared fixture for <see cref="GdsImportJourneyTests"/>: imports a
/// single-waveguide GDS, places it on a canvas next to a Grating Coupler,
/// and connects them through the real routing path.
/// </summary>
public sealed class GdsImportJourneyFixture : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-journey-" + Guid.NewGuid().ToString("N"));
    private string? _savedPath;

    public GdsDesignScopeTestHost Host { get; }
    public GdsImportOutcome Outcome { get; }
    public DesignCanvasViewModel Canvas { get; }
    public GdsPlacementReport PlaceReport { get; }

    public GdsImportJourneyFixture()
    {
        Host = new GdsDesignScopeTestHost();
        var gdsPath = WriteGds();

        // Step 1: Import through the real service path.
        var service = Host.CreateService(() => Array.Empty<ComponentTemplate>());
        Outcome = service.ImportAsync(gdsPath, "TOP", null, null).GetAwaiter().GetResult();

        // Step 2: Place the imported component on the canvas.
        Canvas = new DesignCanvasViewModel();
        Canvas.InitializeAStarRouting(-200, -100, 200, 100);
        PlaceReport = new GdsPlacementExecutor(Canvas, null, () => Host.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(Outcome)).GetAwaiter().GetResult();

        // Place a Grating Coupler (Demo PDK) to the left of the imported waveguide.
        var templates = TestPdkLoader.LoadAllTemplates();
        var gcTemplate = templates.First(t =>
            t.Name == "Grating Coupler" && t.PdkSource == "Demo PDK");
        var gc = ComponentTemplates.CreateFromTemplate(gcTemplate, -120, -7.5);
        Canvas.AddComponent(gc, gcTemplate.Name, gcTemplate.PdkSource);

        // Connect GC waveguide pin → imported waveguide "in" pin.
        var importedComponent = GetImportedComponent();
        var importedInPin = importedComponent.PhysicalPins.Single(p => p.Name == "in");
        var gcWgPin = gc.PhysicalPins.Single(p => p.Name == "waveguide");
        Canvas.ConnectPins(gcWgPin, importedInPin);
        Canvas.RecalculateRoutesAsync().GetAwaiter().GetResult();
    }

    /// <summary>Returns the imported waveguide component (not the GC).</summary>
    public Component GetImportedComponent() =>
        Canvas.Components
            .Select(vm => vm.Component)
            .First(c => c.PhysicalPins.Any(p => p.Name == "in") &&
                        c.PhysicalPins.Any(p => p.Name == "out") &&
                        c.WidthMicrometers == 10);

    /// <summary>Saves the design to .lun (idempotent — reuses the first save).</summary>
    public async Task<string> SaveDesign()
    {
        if (_savedPath is not null)
            return _savedPath;

        _savedPath = Path.Combine(_root, "journey.lun");
        await SaveToFile(CreateFileOperations(Canvas, Host), _savedPath);
        return _savedPath;
    }

    /// <summary>
    /// Creates a <see cref="FileOperationsViewModel"/> wired to the host's
    /// design scope, with bundled PDK templates pre-loaded so both bundled
    /// and GDS-imported components resolve during load.
    /// </summary>
    internal static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas,
        GdsDesignScopeTestHost host)
    {
        // Pre-populate the host's template collection with bundled PDK
        // templates so both bundled and GDS-imported components resolve
        // during load. The host's collection IS the FileOperationsViewModel's
        // library — design-scope restore adds imported templates to it live.
        foreach (var t in TestPdkLoader.LoadAllTemplates())
        {
            if (!host.Templates.Any(existing =>
                existing.Name == t.Name && existing.PdkSource == t.PdkSource))
                host.Templates.Add(t);
        }

        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            host.Templates,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!,
            errorConsole: new ErrorConsoleService())
        {
            DesignScopedGdsComponents = host.Scope,
        };
    }

    internal static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue();
    }

    internal static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }

    private string WriteGds()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "journey.gds");
        var content = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray();
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        Host.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

/// <summary>GDS fixture cell builder (same shape as GdsImportDesignRoundTripTests).</summary>
file static class GdsJourneyTestCells
{
    /// <summary>
    /// 10×4 µm gdsfactory-style waveguide: a 0.5 µm core stripe on the waveguide
    /// layer (1,0), an extent rectangle on (111,0), and in/out port labels on (1,10).
    /// </summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell();
}
