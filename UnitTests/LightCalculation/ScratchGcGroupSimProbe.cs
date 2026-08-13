using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using UnitTests.Helpers;
using UnitTests.Services.GdsImport;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.LightCalculation;

public class ScratchGcGroupSimProbe
{
    private readonly ITestOutputHelper _out;
    public ScratchGcGroupSimProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Probe()
    {
        // curves.gds: one group WITH grating couplers + MMIs + phase shifters inside.
        var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "curves.gds");
        if (!File.Exists(src)) { _out.WriteLine("SKIP"); return; }

        var host = new GdsDesignScopeTestHost();
        var allTemplates = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .Concat(TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json")).ToList();
        var service = host.CreateService(() => allTemplates);
        var outcome = await service.ImportAsync(src, "ConnectAPIC_Design", null, null);
        _out.WriteLine($"instances={outcome.Instances.Count} connections={outcome.Connections.Count}");

        var canvas = new DesignCanvasViewModel();
        await new GdsPlacementExecutor(canvas, new CommandManager(), () => allTemplates)
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        var group = (ComponentGroup)canvas.Components.First().Component;

        var builder = new ComponentGroupSMatrixBuilder();
        var wavelengths = builder.GetSupportedWavelengths(group);
        _out.WriteLine($"supported wavelengths: {string.Join(",", wavelengths.OrderBy(w => w))}");
        foreach (var wl in wavelengths.OrderBy(w => w))
        {
            try
            {
                var m = builder.BuildFullInternalMatrix(group, wl);
                _out.WriteLine($"  {wl} nm: OK");
            }
            catch (Exception ex)
            {
                _out.WriteLine($"  {wl} nm: THREW {ex.Message.Split('—')[0].Trim()}");
            }
        }
        host.Dispose();
    }
}
