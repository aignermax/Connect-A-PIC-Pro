using CAP.Avalonia.ViewModels.Analysis;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.Sources;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

public class TransientSourceSettingsViewModelTests
{
    private const double CenterWavelengthNm = 1550;
    private const double SpanNm = 100;
    private const int FreqPoints = 256;

    [Fact]
    public void CreateGrid_Prbs_UsesSignalDrivenSamplingPolicy()
    {
        var settings = new TransientSourceSettingsViewModel
        {
            SourceType = TransientSourceType.PrbsNrz,
            BitrateGbps = 25,
            SamplesPerSymbol = 32,
            SymbolCount = 16,
        };

        var grid = settings.CreateGrid(CenterWavelengthNm, SpanNm, FreqPoints);

        grid.SampleRateHz.ShouldBe(25e9 * 32);
        grid.NSamples.ShouldBe(32 * 16 + FreqPoints, "guard tail must cover the IR length");
    }

    [Fact]
    public void CreateGrid_GaussianPulse_KeepsWavelengthSweepGrid()
    {
        var settings = new TransientSourceSettingsViewModel
        {
            SourceType = TransientSourceType.GaussianPulse,
        };

        var grid = settings.CreateGrid(CenterWavelengthNm, SpanNm, FreqPoints);

        var expected = TimeSignalDefinition.FromWavelengthSweep(
            CenterWavelengthNm, SpanNm, FreqPoints);
        grid.SampleRateHz.ShouldBe(expected.SampleRateHz);
        grid.NSamples.ShouldBe(expected.NSamples);
    }

    [Theory]
    [InlineData(TransientSourceType.GaussianPulse, typeof(PulseSource))]
    [InlineData(TransientSourceType.ContinuousWave, typeof(CwSource))]
    [InlineData(TransientSourceType.PrbsNrz, typeof(PrbsSource))]
    public void CreateSource_ReturnsMatchingSourceType(
        TransientSourceType sourceType, Type expectedType)
    {
        var settings = new TransientSourceSettingsViewModel { SourceType = sourceType };

        var source = settings.CreateSource(
            amplitude: 1.0, pulseCenterSeconds: 2e-12, pulseSigmaSeconds: 5e-13);

        source.ShouldBeOfType(expectedType);
    }

    [Fact]
    public void CreateSource_Prbs_HonoursAmplitude()
    {
        var settings = new TransientSourceSettingsViewModel
        {
            SourceType = TransientSourceType.PrbsNrz,
            BitrateGbps = 25,
            SamplesPerSymbol = 32,
            SymbolCount = 16,
        };
        var grid = settings.CreateGrid(CenterWavelengthNm, SpanNm, FreqPoints);

        var samples = settings
            .CreateSource(amplitude: 2.0, pulseCenterSeconds: 0, pulseSigmaSeconds: 1e-13)
            .Generate(grid);

        samples.Max().ShouldBe(2.0, 1e-9, "a '1' bit must carry the pin's amplitude");
    }

    [Fact]
    public void SwitchingSourceType_RaisesVisibilityHelperChanges()
    {
        var settings = new TransientSourceSettingsViewModel();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.SourceType = TransientSourceType.PrbsNrz;

        settings.IsPrbs.ShouldBeTrue();
        settings.IsGaussianPulse.ShouldBeFalse();
        changed.ShouldContain(nameof(TransientSourceSettingsViewModel.IsPrbs));
        changed.ShouldContain(nameof(TransientSourceSettingsViewModel.IsGaussianPulse));
    }

    [Fact]
    public void PrbsOrders_ExposesOnlySupportedLfsrOrders()
    {
        TransientSourceSettingsViewModel.PrbsOrders.ShouldBe(new[] { 7, 9, 11, 15, 23, 31 });
    }

    [Fact]
    public void SourceTypes_ExposesAllOptionsForTheComboBox()
    {
        TransientSourceSettingsViewModel.SourceTypes.ShouldBe(new[]
        {
            TransientSourceType.GaussianPulse,
            TransientSourceType.ContinuousWave,
            TransientSourceType.PrbsNrz,
        });
    }
}
