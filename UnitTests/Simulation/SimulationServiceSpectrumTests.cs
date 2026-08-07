using CAP.Avalonia.Services;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts.LaserSpectrum;
using CAP_Core.Grid;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using Xunit;

namespace UnitTests.Simulation;

/// <summary>
/// Integration tests for Issue #819: <see cref="SimulationService.ConfigureLightSources"/>
/// must keep today's single-input behaviour for ideal sources (acceptance criterion 3)
/// and expand spectral sources into weighted per-wavelength inputs.
/// </summary>
public class SimulationServiceSpectrumTests
{
    private static (List<SourceConfigInfo> Configs, PhysicalExternalPortManager Ports, ComponentViewModel Coupler)
        Configure(Action<ComponentViewModel>? setup = null)
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        // The core-side classifier keys on the identifier (no template name on Component).
        coupler.Component.Identifier = "grating_coupler_test";
        setup?.Invoke(coupler);

        var portManager = new PhysicalExternalPortManager();
        var configs = new SimulationService().ConfigureLightSources(canvas, portManager);
        return (configs, portManager, coupler);
    }

    [Fact]
    public void IdealSource_KeepsLegacySingleInputPerPin()
    {
        var (configs, ports, coupler) = Configure();

        var inputs = ports.GetAllExternalInputs().ToList();
        var lightPinCount = coupler.Component.PhysicalPins.Count;

        inputs.Count.ShouldBe(lightPinCount);
        foreach (var input in inputs)
        {
            input.PinName.ShouldStartWith($"src_{coupler.Component.Identifier}_");
            input.PinName.ShouldNotContain("nm");
            input.InFlowPower.Real.ShouldBe(1.0, tolerance: 1e-12);
            input.LaserType.WaveLengthInNm.ShouldBe(StandardWaveLengths.RedNM);
        }
        configs.ShouldAllBe(c => !c.HasSpectralLinewidth);
        configs.ShouldAllBe(c => c.SampleWavelengthsNm.Count == 1);
    }

    [Fact]
    public void SpectralSource_ExpandsIntoWeightedInputsSummingToPower()
    {
        const double power = 0.8;
        var (configs, ports, coupler) = Configure(vm =>
        {
            vm.LaserConfig!.InputPower = power;
            vm.LaserConfig.LineShape = LaserLineShape.Gaussian;
            vm.LaserConfig.LinewidthFwhmNm = 4;
        });

        var inputs = ports.GetAllExternalInputs().ToList();
        int center = coupler.LaserConfig!.WavelengthNm;
        string legacyPrefix = $"src_{coupler.Component.Identifier}_";

        // Several samples per pin; the total injected power per pin equals InputPower.
        int pinCount = coupler.Component.PhysicalPins.Count;
        inputs.Count.ShouldBeGreaterThan(pinCount);
        double totalPower = inputs.Sum(i => i.InFlowPower.Real);
        totalPower.ShouldBe(power * pinCount, tolerance: 1e-9);

        // The center sample keeps the legacy pin name (no wavelength suffix).
        inputs.Count(i => i.LaserType.WaveLengthInNm == center && !i.PinName.EndsWith("nm"))
            .ShouldBe(pinCount);
        inputs.Where(i => i.LaserType.WaveLengthInNm != center)
            .ShouldAllBe(i => i.PinName.StartsWith(legacyPrefix) && i.PinName.EndsWith("nm"));

        configs.ShouldAllBe(c => c.HasSpectralLinewidth);
        configs.ShouldAllBe(c => c.SampleWavelengthsNm.Count > 1);
        configs.ShouldAllBe(c => c.WavelengthNm == center);
    }

    [Fact]
    public void DisabledLaser_ContributesNoInputs()
    {
        var (configs, ports, _) = Configure(vm => vm.LaserConfig!.IsEnabled = false);

        configs.ShouldBeEmpty();
        ports.GetAllExternalInputs().ShouldBeEmpty();
    }
}
