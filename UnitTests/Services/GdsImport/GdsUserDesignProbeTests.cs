using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Export;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Services.GdsImport;

/// <summary>SCRATCH probe — user-design import numbers after the network-merging change. Will be removed.</summary>
public class GdsUserDesignProbeTests
{
    private readonly ITestOutputHelper _output;

    public GdsUserDesignProbeTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task Probe()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "no nazca");
        var root = Path.Combine(Path.GetTempPath(), "ud-probe-" + Guid.NewGuid().ToString("N"));
        var prefsPath = Path.Combine(root, "prefs.json");
        Directory.CreateDirectory(root);

        var canvas = GdsUserDesignFixture.BuildUserDesignCanvas();
        var script = new SimpleNazcaExporter().Export(canvas);
        var exportDir = Path.Combine(root, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "user_design.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, run.StdErr);
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        var upgraded = run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal);
        _output.WriteLine($"upgraded={upgraded}");

        var sink = new GdsUserDesignFixture.LibrarySink(prefsPath);
        var service = new GdsImportService(
            GdsUserDesignFixture.CreateStore(root, "user-pdks"), () => sink.Templates.ToList(), sink.Register);
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        var dialogOptions = new GdsHierarchyImportOptions
        {
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        var outcome = await service.ImportAsync(gdsPath, analysis.TopCellCandidates[0], dialogOptions, null);

        _output.WriteLine($"instances={outcome.Instances.Count} connections={outcome.Connections.Count} frozen={outcome.TopCellWaveguidePolygons.Count}");
        foreach (var c in outcome.Connections)
            _output.WriteLine($"  conn #{c.A.InstanceIndex}:{c.A.PinName} <-> #{c.B.InstanceIndex}:{c.B.PinName} route={c.IsRouteDerived} elec={c.IsElectrical}");
        foreach (var w in outcome.Warnings) _output.WriteLine($"  W: {w}");
        foreach (var i in outcome.Infos) _output.WriteLine($"  I: {i}");

        var canvas2 = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(canvas2, null, () => sink.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        _output.WriteLine($"placed={report.PlacedCount} connected={report.ConnectedCount} routeDerived={report.RouteDerivedCount} frozenPaths={report.FrozenRoutePathCount}");
        _output.WriteLine($"report warnings: {string.Join("|", report.Warnings)}");
        _output.WriteLine($"validation warnings: {string.Join("|", report.ValidationWarnings)}");
        var group = canvas2.Components.FirstOrDefault()?.Component as CAP_Core.Components.Core.ComponentGroup;
        _output.WriteLine($"group paths={group?.InternalPaths.Count}");

        // black box
        var sink2 = new GdsUserDesignFixture.LibrarySink(Path.Combine(root, "prefs-bb.json"));
        var service2 = new GdsImportService(
            GdsUserDesignFixture.CreateStore(root, "user-pdks-bb"), () => sink2.Templates.ToList(), sink2.Register);
        var bb = await service2.ImportAsync(
            gdsPath, analysis.TopCellCandidates[0],
            dialogOptions with { Mode = GdsHierarchyImportMode.BlackBox }, null);
        _output.WriteLine($"BB registered={bb.RegisteredComponents.Count} warnings=[{string.Join("|", bb.Warnings)}]");
        var t = sink2.Templates.FirstOrDefault();
        _output.WriteLine($"BB template: {t?.Name} pins={t?.PinDefinitions.Length}");
        if (t != null)
            foreach (var p in t.PinDefinitions)
                _output.WriteLine($"  bb pin {p.Name} @({p.OffsetX:F2},{p.OffsetY:F2}) kind={p.Kind}");
        var canvas3 = new DesignCanvasViewModel();
        var bbReport = await new GdsPlacementExecutor(canvas3, null, () => sink2.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(bb));
        _output.WriteLine($"BB placed={bbReport.PlacedCount} skipped=[{string.Join("|", bbReport.SkippedPlacements)}] group={bbReport.GroupCreated}");
    }
}
