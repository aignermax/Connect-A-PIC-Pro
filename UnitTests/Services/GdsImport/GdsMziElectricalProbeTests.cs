using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Export;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Services.GdsImport;

/// <summary>SCRATCH probe — dumps the MZI export/GDS structure + import outcome. Will be removed.</summary>
public class GdsMziElectricalProbeTests
{
    private readonly ITestOutputHelper _output;

    public GdsMziElectricalProbeTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task Probe_ExportAndDump()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "no nazca python");

        var canvas = GdsMziElectricalFixture.BuildMziCanvas();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, skippedConnections: skipped, exportWarnings: warnings);
        _output.WriteLine($"skipped=[{string.Join(";", skipped)}] warnings=[{string.Join(";", warnings)}]");

        var root = Path.Combine(Path.GetTempPath(), "mzi-probe-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "export");
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "mzi.py");
        await File.WriteAllTextAsync(scriptPath, script);

        // Normal run (upgrade when klayout+siepic present).
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, dir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca run failed: {run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        var upgraded = run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal);
        var upgradedCopy = Path.Combine(dir, "mzi_upgraded.gds");
        File.Copy(gdsPath, upgradedCopy, overwrite: true);

        // Forced STUB scenario: same script, klayout/siepic imports poisoned.
        var stubRunner = Path.Combine(dir, "mzi_stub.py");
        await File.WriteAllTextAsync(stubRunner,
            "import sys, runpy\n" +
            "sys.modules['klayout'] = None\n" +
            "sys.modules['klayout.db'] = None\n" +
            "sys.modules['siepic_ebeam_pdk'] = None\n" +
            $"sys.argv = [r'{scriptPath}']\n" +
            $"runpy.run_path(r'{scriptPath}', run_name='__main__')\n");
        var stubRun = await SiepicRealGeometryExportTests.RunPythonAsync(python, dir, stubRunner);
        stubRun.ExitCode.ShouldBe(0, $"stub run failed: {stubRun.StdErr}");
        var stubCopy = Path.Combine(dir, "mzi_stub.gds");
        File.Move(gdsPath, stubCopy, overwrite: true);
        _output.WriteLine($"upgraded={upgraded}; stub gds + upgraded gds written under {dir}");

        await DumpImport("STUB", stubCopy);
        await DumpImport(upgraded ? "UPGRADED" : "UPGRADED(same-as-stub?)", upgradedCopy);
    }

    private async Task DumpImport(string label, string gdsPath)
    {
        _output.WriteLine($"══ SCENARIO {label} ══");
        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);
        _output.WriteLine($"cells: {string.Join(", ", library.Cells.Keys)}");
        var designCell = library.Cells["ConnectAPIC_Design"];
        foreach (var g in designCell.Elements.OfType<GdsPolygon>().GroupBy(p => (p.Layer, p.DataType)))
            _output.WriteLine($"  top polys {g.Key}: {g.Count()}");

        using var host = new GdsDesignScopeTestHost();
        var bundled = TestPdkLoader.LoadAllTemplates();
        var service = host.CreateService(() => bundled.Concat(host.Templates).ToList());
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        var dialogOptions = new GdsHierarchyImportOptions
        {
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        var outcome = await service.ImportAsync(
            gdsPath, analysis.TopCellCandidates[0], dialogOptions, null);

        _output.WriteLine($"registered: {string.Join(", ", outcome.RegisteredComponents.Select(r => $"{r.CellDraftName}->{r.ComponentName}"))}");
        foreach (var i in outcome.Instances)
            _output.WriteLine($"  inst {i.InstanceName} known={i.KnownComponentIdentifier} draft={i.CellDraftName} pos=({i.PositionXUm:F3},{i.PositionYUm:F3}) rot={i.RotationDegrees}");
        _output.WriteLine($"connections: {outcome.Connections.Count}");
        foreach (var c in outcome.Connections)
            _output.WriteLine($"  conn #{c.A.InstanceIndex}:{c.A.PinName} <-> #{c.B.InstanceIndex}:{c.B.PinName} route={c.IsRouteDerived} elec={c.IsElectrical}");
        _output.WriteLine($"frozen top-cell polygons: {outcome.TopCellWaveguidePolygons.Count}");
        foreach (var w in outcome.Warnings) _output.WriteLine($"  W: {w}");
        foreach (var i in outcome.Infos) _output.WriteLine($"  I: {i}");

        var canvas2 = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(canvas2, null, () => bundled.Concat(host.Templates).ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        _output.WriteLine($"placed={report.PlacedCount} connected={report.ConnectedCount} routeDerived={report.RouteDerivedCount} skipped={string.Join("|", report.SkippedPlacements)}");
        _output.WriteLine($"report warnings: {string.Join("|", report.Warnings)}");
        _output.WriteLine($"validation warnings: {string.Join("|", report.ValidationWarnings)}");
        var group = canvas2.Components.FirstOrDefault()?.Component as CAP_Core.Components.Core.ComponentGroup;
        if (group != null)
        {
            var paths = group.InternalPaths.ToList();
            _output.WriteLine($"group '{group.GroupName}' internalPaths={paths.Count} " +
                $"pinned={paths.Count(p => p.StartPin != null)} pinless={paths.Count(p => p.StartPin == null)}");
            foreach (var p in paths.Where(p => p.StartPin != null))
                _output.WriteLine($"  pinned path {p.StartPin.ParentComponent?.Identifier}.{p.StartPin.Name} <-> " +
                    $"{p.EndPin?.ParentComponent?.Identifier}.{p.EndPin?.Name} elec={p.StartPin.MatterType == CAP_Core.Components.Core.MatterType.Electricity}");
        }

        using var hostBlackBox = new GdsDesignScopeTestHost();
        var service2 = hostBlackBox.CreateService(() => bundled.Concat(hostBlackBox.Templates).ToList());
        var bbOutcome = await service2.ImportAsync(
            gdsPath, analysis.TopCellCandidates[0],
            dialogOptions with { Mode = GdsHierarchyImportMode.BlackBox }, null);
        _output.WriteLine($"── BLACKBOX {label} ──");
        _output.WriteLine($"registered: {string.Join(", ", bbOutcome.RegisteredComponents.Select(r => r.ComponentName))}");
        _output.WriteLine($"warnings: {string.Join("|", bbOutcome.Warnings)}");
        var bbTemplate = hostBlackBox.Templates.FirstOrDefault();
        if (bbTemplate != null)
        {
            _output.WriteLine($"bb '{bbTemplate.Name}' {bbTemplate.WidthMicrometers:F1}x{bbTemplate.HeightMicrometers:F1} pins={bbTemplate.PinDefinitions.Length}");
            foreach (var p in bbTemplate.PinDefinitions)
                _output.WriteLine($"  pin {p.Name} @({p.OffsetX:F2},{p.OffsetY:F2}) a{p.AngleDegrees} kind={p.Kind}");
        }
    }
}
