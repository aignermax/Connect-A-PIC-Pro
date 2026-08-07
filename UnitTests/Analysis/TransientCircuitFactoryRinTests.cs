using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.ExternalPorts.LaserSpectrum;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Issue #819: the eye-diagram receiver noise uses the worst (largest) RIN among
/// the enabled laser sources on the canvas.
/// </summary>
public class TransientCircuitFactoryRinTests
{
    [Fact]
    public void NoSources_FallsBackToDefaultRin()
    {
        var canvas = new DesignCanvasViewModel();
        AnalysisOutputTestBed.AddPlainComponent(canvas);

        TransientCircuitFactory.ResolveRinDbPerHz(canvas)
            .ShouldBe(LaserSpectrumModel.DefaultRinDbPerHz);
    }

    [Fact]
    public void WorstRinAmongEnabledSources_Wins()
    {
        var canvas = new DesignCanvasViewModel();
        var quiet = AnalysisOutputTestBed.AddCoupler(canvas, 0, 0);
        var noisy = AnalysisOutputTestBed.AddCoupler(canvas, 0, 20);
        quiet.LaserConfig!.RinDbPerHz = -160;
        noisy.LaserConfig!.RinDbPerHz = -120;

        TransientCircuitFactory.ResolveRinDbPerHz(canvas).ShouldBe(-120);
    }

    [Fact]
    public void DisabledSource_IsIgnored()
    {
        var canvas = new DesignCanvasViewModel();
        var enabled = AnalysisOutputTestBed.AddCoupler(canvas, 0, 0);
        var disabled = AnalysisOutputTestBed.AddCoupler(canvas, 0, 20);
        enabled.LaserConfig!.RinDbPerHz = -150;
        disabled.LaserConfig!.RinDbPerHz = -100;
        disabled.LaserConfig.IsEnabled = false;

        TransientCircuitFactory.ResolveRinDbPerHz(canvas).ShouldBe(-150);
    }
}
