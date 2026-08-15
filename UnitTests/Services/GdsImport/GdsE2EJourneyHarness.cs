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
using Moq;
using Shouldly;
using UnitTests.Export;
using UnitTests.Helpers;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Harness behind <see cref="GdsImportE2EJourneyTests"/>: builds the scenario
/// design from the bundled SiEPIC EBeam PDK, runs the real nazca export, and
/// wires the same save/load file-operations stack the .lun round-trip tests use.
/// </summary>
internal static class GdsE2EJourneyHarness
{
    public const string GratingCoupler = "Grating Coupler TE 1550";
    public const string Splitter = "Y-Branch 1550";
    public const string OutlierDc = "Broadband DC TE 1550";
    public const string PdkName = "SiEPIC EBeam PDK";

    /// <summary>The Broadband DC's deliberately non-cardinal placement rotation.</summary>
    public const double OutlierRotationDegrees = 330.0;

    /// <summary>
    /// The three bundled SiEPIC templates the scenario uses. SiEPIC parts are
    /// deliberate picks: their parameterless stub functions (ebeam_gc_te1550, …)
    /// export GDS cells named exactly after the function, which is what lets the
    /// re-import resolve every cell back to its library template. (Demofab's
    /// demo.io/demo.dbr would NOT round-trip: nazca's @hashme bakes the default
    /// parameters into those cell names, e.g. "io_None_3_0_0__b343".)
    /// </summary>
    public static IReadOnlyList<ComponentTemplate> ScenarioTemplates() =>
        TestPdkLoader.LoadAllTemplates()
            .Where(t => t.PdkSource == PdkName
                && t.Name is GratingCoupler or Splitter or OutlierDc)
            .ToList();

    /// <summary>
    /// The original design, deliberately UNCONNECTED so every wire must come from
    /// the auto-connect stage on re-import: an input grating coupler feeding a
    /// Y-branch whose arms face two 180°-rotated output couplers (both arms need
    /// an S-bend), plus a Broadband DC at a non-cardinal 330° far below the
    /// circuit (its four pins face empty space, so they stay unpaired).
    /// </summary>
    public static DesignCanvasViewModel BuildOriginalDesign(IReadOnlyList<ComponentTemplate> templates)
    {
        var canvas = new DesignCanvasViewModel();
        Place(canvas, templates, GratingCoupler, 100, 100);
        Place(canvas, templates, Splitter, 200, 110.169);
        Place(canvas, templates, GratingCoupler, 300, 90, quarterTurns: 2);
        Place(canvas, templates, GratingCoupler, 300, 130, quarterTurns: 2);
        Place(canvas, templates, OutlierDc, 150, 300, exactRotationDegrees: OutlierRotationDegrees);
        return canvas;
    }

    private static void Place(
        DesignCanvasViewModel canvas,
        IReadOnlyList<ComponentTemplate> templates,
        string templateName,
        double x,
        double y,
        int quarterTurns = 0,
        double? exactRotationDegrees = null)
    {
        var template = templates.First(t => t.Name == templateName);
        var command = PlaceComponentCommand.CreateExact(
            canvas, template, x, y, quarterTurns,
            mirrorPinsHorizontally: false, exactRotationDegrees: exactRotationDegrees);
        command.Execute();
        command.PlacedComponent.ShouldNotBeNull($"placing '{templateName}' must succeed");
    }

    /// <summary>Exports <paramref name="canvas"/> through the real nazca engine; returns the .gds path.</summary>
    public static async Task<string> ExportAsync(
        string python,
        string root,
        string subdir,
        DesignCanvasViewModel canvas,
        IEnumerable<ComponentTemplate> library)
    {
        var skipped = new List<string>();
        var warnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, skippedConnections: skipped, exportWarnings: warnings, library: library);
        skipped.ShouldBeEmpty("every connection and frozen path must export as real geometry");

        var exportDir = Path.Combine(root, subdir);
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "journey_design.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");
        return gdsPath;
    }

    /// <summary>
    /// File-operations VM wired like <c>GdsImportDesignRoundTripTests</c>; the
    /// host's template collection must already contain the bundled templates so
    /// loading resolves the placed components by TemplateName + PdkSource.
    /// </summary>
    public static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas, GdsDesignScopeTestHost host) =>
        new(
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

    /// <summary>Saves the design behind a mocked save dialog.</summary>
    public static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue();
    }

    /// <summary>Loads a design behind a mocked open dialog.</summary>
    public static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }

    /// <summary>The component's pin with the given name.</summary>
    public static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);
}
