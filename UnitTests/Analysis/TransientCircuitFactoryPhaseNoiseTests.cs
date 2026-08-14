using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.ExternalPorts.LaserSpectrum;
using CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Issue #834: the transient engine derives per-source Wiener phase noise from the
/// configured laser linewidth — ideal sources stay noiseless, non-ideal ones get
/// Δν = c·Δλ/λ², and all pins of one coupler share one lock group (one laser).
/// </summary>
public class TransientCircuitFactoryPhaseNoiseTests
{
    [Fact]
    public void IdealSources_YieldNoPhaseNoise()
    {
        var canvas = new DesignCanvasViewModel();
        AnalysisOutputTestBed.AddCoupler(canvas);

        var settings = TransientCircuitFactory.BuildPhaseNoiseSettings(canvas);

        settings.HasAnyNoise.ShouldBeFalse();
        settings.Sources.Values.ShouldAllBe(s => s.LinewidthHz == 0);
    }

    [Fact]
    public void SpectralShapeSource_GetsLinewidthFromConfig()
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        coupler.LaserConfig!.LineShape = LaserLineShape.Lorentzian;
        coupler.LaserConfig.LinewidthFwhmNm = 0.1;

        var settings = TransientCircuitFactory.BuildPhaseNoiseSettings(canvas);

        settings.HasAnyNoise.ShouldBeTrue();
        double expectedHz = PhaseNoiseSettings.LinewidthFwhmNmToHz(
            0.1, coupler.LaserConfig.WavelengthNm);
        settings.Sources.Values.ShouldAllBe(s => Math.Abs(s.LinewidthHz - expectedHz) < 1);
    }

    [Fact]
    public void PinsOfOneCoupler_ShareOneLockGroup()
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        coupler.LaserConfig!.LineShape = LaserLineShape.Lorentzian;

        var settings = TransientCircuitFactory.BuildPhaseNoiseSettings(canvas);

        settings.Sources.Count.ShouldBeGreaterThan(0);
        settings.Sources.Values.Select(s => s.LockGroupId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void DistinctCouplers_GetIndependentLockGroups()
    {
        var canvas = new DesignCanvasViewModel();
        var a = AnalysisOutputTestBed.AddCoupler(canvas, 0, 0);
        var b = AnalysisOutputTestBed.AddCoupler(canvas, 0, 20);
        a.LaserConfig!.LineShape = LaserLineShape.Lorentzian;
        b.LaserConfig!.LineShape = LaserLineShape.Lorentzian;

        var settings = TransientCircuitFactory.BuildPhaseNoiseSettings(canvas);

        settings.Sources.Values.Select(s => s.LockGroupId).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void DisabledLaser_ContributesNoSource()
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        coupler.LaserConfig!.LineShape = LaserLineShape.Lorentzian;
        coupler.LaserConfig.IsEnabled = false;

        var settings = TransientCircuitFactory.BuildPhaseNoiseSettings(canvas);

        settings.Sources.ShouldBeEmpty();
        settings.HasAnyNoise.ShouldBeFalse();
    }
}
