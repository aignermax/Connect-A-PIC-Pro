using System.Diagnostics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using UnitTests.Helpers;
using UnitTests.Services.GdsImport;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.LightCalculation;

public class ScratchGroupSimTimingProbe
{
    private readonly ITestOutputHelper _out;
    public ScratchGroupSimTimingProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Probe()
    {
        var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "curves_nazca_partial.gds");
        if (!File.Exists(src)) { _out.WriteLine("SKIP"); return; }

        var host = new GdsDesignScopeTestHost();
        var allTemplates = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .Concat(TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json")).ToList();
        var service = host.CreateService(() => allTemplates);
        var outcome = await service.ImportAsync(src, "ConnectAPIC_Design", null, null);
        var canvas = new DesignCanvasViewModel();
        await new GdsPlacementExecutor(canvas, new CommandManager(), () => allTemplates)
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        var group = (ComponentGroup)canvas.Components.First().Component;

        var sw = Stopwatch.StartNew();
        group.EnsureSMatrixComputed();
        _out.WriteLine($"EnsureSMatrixComputed (all wavelengths): {sw.ElapsedMilliseconds} ms");

        host.Dispose();
    }
}
