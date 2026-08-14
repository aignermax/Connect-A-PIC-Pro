using System.Diagnostics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using UnitTests.Helpers;
using UnitTests.Services.GdsImport;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Services.GdsImport;

public class ScratchImportTimingProbe
{
    private readonly ITestOutputHelper _out;
    public ScratchImportTimingProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Probe()
    {
        var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "phridge_soa_v1_0_0 (1).gds");
        if (!File.Exists(src)) { _out.WriteLine("SKIP"); return; }

        var sw = Stopwatch.StartNew();
        var host = new GdsDesignScopeTestHost();
        var allTemplates = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .Concat(TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json")).ToList();
        var service = host.CreateService(() => allTemplates);
        var outcome = await service.ImportAsync(src, "cell", null, null);
        _out.WriteLine($"ImportAsync (analysis): {sw.ElapsedMilliseconds} ms  (instances={outcome.Instances.Count} connections={outcome.Connections.Count})");

        sw.Restart();
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => allTemplates);
        var report = await executor.ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        _out.WriteLine($"ExecuteAsync (place+connect+validate+group): {sw.ElapsedMilliseconds} ms  (rerouted={report.ReroutedCount})");
        host.Dispose();
    }
}
