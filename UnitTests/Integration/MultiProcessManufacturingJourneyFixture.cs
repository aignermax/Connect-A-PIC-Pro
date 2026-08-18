using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Moq;
using Shouldly;
using UnitTests.Components;
using UnitTests.Export;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Shared fixture for <see cref="MultiProcessManufacturingJourneyTests"/> (issue #1010):
/// performs the journey's stateful stations once so each fact asserts one step of the
/// same continuous road — build + bind (steps 1–2), save → load (step 4), export script
/// (step 5) and, when a nazca-capable Python exists, the executed GDS export the gated
/// steps 6–7 drive.
/// </summary>
public class MultiProcessManufacturingJourneyFixture : IAsyncLifetime
{
    private readonly string _designFilePath;

    /// <summary>Creates the fixture with a throwaway temp working directory.</summary>
    public MultiProcessManufacturingJourneyFixture()
    {
        WorkDirectory = Path.Combine(Path.GetTempPath(), "multiprocess-manufacturing-" + Guid.NewGuid().ToString("N"));
        _designFilePath = Path.Combine(WorkDirectory, "journey.lun");
    }

    /// <summary>Temp working directory for the .lun file, the export and the DRC report.</summary>
    public string WorkDirectory { get; }

    /// <summary>The composed two-chiplet design (Cornerstone SiN + SiEPIC EBeam).</summary>
    public MultiProcessChipletJourneyDesign Design { get; private set; } = null!;

    /// <summary>The production process catalog over the journey's two bundled PDKs.</summary>
    public IReadOnlyList<ProcessGroup> Catalog { get; private set; } = null!;

    /// <summary>Absolute positions of both chiplets' exposed pins, captured before the save.</summary>
    public IReadOnlyDictionary<string, (double X, double Y)> PinPositionsBeforeSave { get; private set; } = null!;

    /// <summary>Raw text of the saved .lun file.</summary>
    public string SavedFileText { get; private set; } = null!;

    /// <summary>Process-migration warning raised by the reload, when any.</summary>
    public string? MigrationWarning { get; private set; }

    /// <summary>The canvas the saved design reloaded onto.</summary>
    public DesignCanvasViewModel LoadedCanvas { get; private set; } = null!;

    /// <summary>Chiplet A after the round-trip.</summary>
    public ComponentGroup LoadedChipletA { get; private set; } = null!;

    /// <summary>Chiplet B after the round-trip.</summary>
    public ComponentGroup LoadedChipletB { get; private set; } = null!;

    /// <summary>The nazca export script of the journey design.</summary>
    public string NazcaScript { get; private set; } = null!;

    /// <summary>The nazca-capable Python the executed export ran with (null = gated steps skip).</summary>
    public string? NazcaPython { get; private set; }

    /// <summary>The executed export's GDS (null when no nazca Python is available or the export failed).</summary>
    public string? ExportedGdsPath { get; private set; }

    /// <summary>Export process stdout/stderr, kept for readable failure messages.</summary>
    public string ExportLog { get; private set; } = string.Empty;

    /// <summary>Builds the design, binds the chiplets, round-trips the file, exports the script.</summary>
    public async Task InitializeAsync()
    {
        Design = MultiProcessChipletJourneyDesign.BuildComposed();
        Catalog = ProcessCatalog.BuildGroups(new[]
        {
            new PdkProcessEntry(Design.Cornerstone.Name, ProcessFingerprintFactory.From(Design.Cornerstone)),
            new PdkProcessEntry(Design.Siepic.Name, ProcessFingerprintFactory.From(Design.Siepic)),
        });
        BindChipletsViaPlacementPolicy();
        PinPositionsBeforeSave = CapturePinPositions(Design.ChipletA, Design.ChipletB);
        await SaveAndLoadAsync();
        NazcaScript = new SimpleNazcaExporter().Export(Design.Canvas);
        await TryExecuteExportAsync();
    }

    /// <summary>Removes the temp working directory.</summary>
    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(WorkDirectory)) Directory.Delete(WorkDirectory, recursive: true);
        }
        catch
        {
            // temp cleanup is best effort
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Binds both chiplets through the exact code path the UI uses when a chiplet is
    /// placed: <see cref="PlacementPolicyContext.CheckGroupPlacementAt"/> derives the
    /// binding from the children's PDK sources and the caller pins it onto the group
    /// (CanvasInteractionViewModel assigns <c>GroupToPlace.ProcessBinding</c> the same way).
    /// </summary>
    private void BindChipletsViaPlacementPolicy()
    {
        var policy = new PlacementPolicyContext(
            () => ActiveProcessSelection.Playground(),
            () => Array.Empty<string>(),
            component => ComponentPdkSourceResolver.Resolve(component, Design.Templates),
            getProcessCatalog: () => Catalog);
        foreach (var chiplet in new[] { Design.ChipletA, Design.ChipletB })
        {
            var (isAllowed, blockReason, derivedBinding) =
                policy.CheckGroupPlacementAt(chiplet, targetGroup: null, chiplet.GroupName);
            isAllowed.ShouldBeTrue($"the placement policy must admit chiplet '{chiplet.GroupName}': {blockReason}");
            chiplet.ProcessBinding = derivedBinding;
        }
    }

    private async Task SaveAndLoadAsync()
    {
        Directory.CreateDirectory(WorkDirectory);
        var saveVm = CreateFileOperations(Design.Canvas, Design.Templates);
        saveVm.ProcessCatalogProvider = () => Catalog;
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(_designFilePath).ShouldBeTrue("the journey design file must be written");
        SavedFileText = await File.ReadAllTextAsync(_designFilePath);

        LoadedCanvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(LoadedCanvas, Design.Templates);
        loadVm.ProcessCatalogProvider = () => Catalog;
        loadVm.OnProcessMigrationWarning = w => MigrationWarning = w;
        var loadDialog = new Mock<IFileDialogService>();
        loadDialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        loadVm.FileDialogService = loadDialog.Object;
        await loadVm.LoadDesignCommand.ExecuteAsync(null);

        var loadedGroups = LoadedCanvas.Components
            .Where(c => c.Component is ComponentGroup)
            .Select(c => (ComponentGroup)c.Component)
            .ToList();
        LoadedChipletA = loadedGroups.SingleOrDefault(g => g.Identifier == Design.ChipletA.Identifier)
            .ShouldNotBeNull("chiplet A identity must survive the round-trip");
        LoadedChipletB = loadedGroups.SingleOrDefault(g => g.Identifier == Design.ChipletB.Identifier)
            .ShouldNotBeNull("chiplet B identity must survive the round-trip");
    }

    /// <summary>Executes the export script when a nazca-capable Python exists (CI does; local suites usually cannot).</summary>
    private async Task TryExecuteExportAsync()
    {
        NazcaPython = await GdsUserDesignFixture.FindNazcaPythonAsync();
        if (NazcaPython == null)
            return;

        var exportDir = Path.Combine(WorkDirectory, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "multiprocess_journey.py");
        await File.WriteAllTextAsync(scriptPath, NazcaScript);
        var export = await SiepicRealGeometryExportTests.RunPythonAsync(NazcaPython, exportDir, scriptPath);
        ExportLog = $"exit {export.ExitCode}\nstdout:\n{export.StdOut}\nstderr:\n{export.StdErr}";
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        if (export.ExitCode == 0 && File.Exists(gdsPath))
            ExportedGdsPath = gdsPath;
    }

    /// <summary>Snapshot of both chiplets' exposed-pin absolute positions, keyed by pin name.</summary>
    private static IReadOnlyDictionary<string, (double X, double Y)> CapturePinPositions(
        params ComponentGroup[] chiplets) =>
        chiplets
            .SelectMany(chiplet => chiplet.ExternalPins)
            .ToDictionary(
                pin => pin.Name,
                pin =>
                {
                    var (x, y) = pin.InternalPin!.GetAbsolutePosition();
                    return (x, y);
                });

    /// <summary>Creates the file-operations facade used for the .lun save/load round-trip.</summary>
    private static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas, List<ComponentTemplate> templates)
    {
        var library = new ObservableCollection<ComponentTemplate>(templates);
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new CAP_Core.Export.SaxExporter(),
            library,
            new GdsExportViewModel(new CAP_Core.Export.GdsExportService()),
            new CAP.Avalonia.ViewModels.Export.PhotonTorchExportViewModel(
                new CAP_Core.Export.PhotonTorchExporter(), canvas),
            null!,
            errorConsole: new CAP_Core.ErrorConsoleService());
    }
}
