using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis.EyeDiagram;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Analysis.EyeDiagram;

/// <summary>
/// Integration guard for the receiver-noise-in-plot fix: a full eye run
/// through <see cref="EyeDiagramViewModel"/> on a minimal link (GC → 100 µm
/// straight → output GC with the laser off) must show a visibly wider
/// histogram when the laser RIN is extreme — the RIN field used to look dead
/// because only the BER metrics consumed it, never the plotted eye.
/// </summary>
public class EyeReceiverNoiseIntegrationTests
{
    [Fact]
    public async Task HighRin_VisiblyWidensThePlottedEyeHistogram()
    {
        var quiet = await RunEye(rinDbPerHz: -200);
        var noisy = await RunEye(rinDbPerHz: -80);

        double quietSpan = quiet.MaxAmplitude - quiet.MinAmplitude;
        double noisySpan = noisy.MaxAmplitude - noisy.MinAmplitude;
        noisySpan.ShouldBeGreaterThan(quietSpan * 2,
            "RIN −80 dB/Hz must visibly widen the plotted eye — receiver noise reaches the histogram now");
    }

    private static async Task<EyeHistogram> RunEye(double rinDbPerHz)
    {
        var templates = TestPdkLoader.LoadAllTemplates();
        var canvas = new DesignCanvasViewModel();
        Component Place(string name, string pdk, double x, double y)
        {
            var t = templates.First(t => t.Name == name && t.PdkSource == pdk);
            var c = ComponentTemplates.CreateFromTemplate(t, x, y);
            canvas.AddComponent(c, name, pdk);
            return c;
        }

        var gcIn = Place("Grating Coupler TE 1550", "SiEPIC EBeam PDK", 0, 0);
        var wg = Place("Straight Waveguide 100µm", "Demo PDK", 300, 0);
        var gcOut = Place("Grating Coupler TE 1550", "SiEPIC EBeam PDK", 700, 0);

        canvas.Components.First(c => c.Component == gcIn).LaserConfig.RinDbPerHz = rinDbPerHz;
        canvas.Components.First(c => c.Component == gcOut).LaserConfig.IsEnabled = false;

        canvas.ConnectPins(
            gcIn.PhysicalPins.Last(p => p.MatterType == MatterType.Light),
            wg.PhysicalPins.First(p => p.Name == "a0"));
        canvas.ConnectPins(
            wg.PhysicalPins.First(p => p.Name == "b0"),
            gcOut.PhysicalPins.First(p => p.MatterType == MatterType.Light));
        await canvas.RecalculateRoutesAsync();

        var vm = new EyeDiagramViewModel();
        vm.Configure(canvas);
        await vm.RunEyeAnalysisCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue($"eye run failed: {vm.StatusText}");
        return vm.LastHistogram!;
    }
}
