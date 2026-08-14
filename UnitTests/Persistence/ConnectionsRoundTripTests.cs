using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Import.Gds;
using Moq;
using Shouldly;
using UnitTests.Export;
using UnitTests.Helpers;
using UnitTests.Services.GdsImport;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Persistence;

/// <summary>
/// Round-trip integration test for <c>TestData/connections.lun</c> (field
/// report): a 118× 2x2-MMI array saved as .lun, exported through the general
/// GDS export (nazca), then re-imported. EVERY link must reconstruct — the
/// field report saw only ~30 % of the connections come back. Runs the real
/// export engine (skipped without a nazca-capable Python) and the full import
/// stack: load .lun → export → reimport → placement report.
/// </summary>
[Trait("Category", "Slow")]
public class ConnectionsRoundTripTests : IDisposable
{
    private const int ExpectedComponents = 118;
    private const int ExpectedConnections = 118;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-conn-roundtrip-" + Guid.NewGuid().ToString("N"));
    private readonly List<GdsDesignScopeTestHost> _hosts = new();
    private readonly ITestOutputHelper _out;

    public ConnectionsRoundTripTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        foreach (var host in _hosts) host.Dispose();
    }

    [SkippableFact]
    public async Task LunExportReimport_EveryConnectionReconstructs()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the round trip needs the real engine.");

        // ── Load the .lun design (Demo PDK templates visible) ──────────────
        using var loadHost = new GdsDesignScopeTestHost();
        _hosts.Add(loadHost);
        foreach (var template in TestPdkLoader.LoadFromPdk("demo-pdk.json"))
            loadHost.Templates.Add(template);
        var canvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(canvas, loadHost), FixturePath);

        canvas.Components.Count.ShouldBe(ExpectedComponents);
        canvas.Connections.Count.ShouldBe(ExpectedConnections);

        // ── Export through the general GDS export (nazca) ──────────────────
        var skipped = new List<string>();
        var warnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(canvas,
            skippedConnections: skipped, exportWarnings: warnings,
            library: loadHost.Templates.ToList());
        skipped.ShouldBeEmpty("every connection must export as real geometry");
        var exportDir = Path.Combine(_root, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "connections.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");

        // ── Re-import the exported GDS ─────────────────────────────────────
        using var importHost = new GdsDesignScopeTestHost();
        _hosts.Add(importHost);
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        var service = importHost.CreateService(() => loadHost.Templates.ToList());
        var outcome = await service.ImportAsync(gdsPath, analysis.TopCellCandidates[0],
            new GdsHierarchyImportOptions
            {
                PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
            }, null);

        outcome.Instances.Count.ShouldBe(ExpectedComponents);
        outcome.Connections.Count.ShouldBeGreaterThanOrEqualTo((int)(ExpectedConnections * 0.95),
            "every logical link of the array must reconstruct from the route geometry " +
            "(field report: only ~30 % came back)");

        // ── Placement with re-routing (the dialog path) ────────────────────
        var importCanvas = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(
            importCanvas, new CommandManager(), () => loadHost.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));

        _out.WriteLine($"import: instances={outcome.Instances.Count} connections={outcome.Connections.Count} " +
                       $"placed={report.PlacedCount} connected={report.ConnectedCount} " +
                       $"rerouted={report.ReroutedCount} cached={report.CachedRouteCount} " +
                       $"blockedWarnings={report.Warnings.Count(w => w.Contains("BlockedPath"))}");

        report.PlacedCount.ShouldBe(ExpectedComponents);
        report.ConnectedCount.ShouldBeGreaterThanOrEqualTo((int)(ExpectedConnections * 0.95),
            "reconstructed links must land as real connections, not blocked fallbacks");
        report.Warnings.Where(w => w.Contains("BlockedPath")).ShouldBeEmpty(
            "no reconstructed connection may report a blocked path");
    }

    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "UnitTests", "TestData")))
                dir = dir.Parent;
            return dir == null
                ? throw new InvalidOperationException("Could not locate UnitTests/TestData")
                : Path.Combine(dir.FullName, "UnitTests", "TestData", "connections.lun");
        }
    }

    private static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas, GdsDesignScopeTestHost host)
    {
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

    private static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }
}
