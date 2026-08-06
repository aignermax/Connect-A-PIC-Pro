using System;
using System.Collections.Generic;
using CAP_Core.Analysis.WavelengthSpectrum;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.WavelengthSpectrum;

public class SpectrumAxisScalerTests
{
    [Theory]
    [InlineData(1500, 1600, 20)]   // 100 nm range → 20 nm ticks (1500, 1520, …)
    [InlineData(1500, 1510, 2)]    // 10 nm range → 2 nm ticks
    [InlineData(1000, 2000, 200)]  // 1000 nm range → 200 nm ticks
    [InlineData(0, 1.05, 0.2)]     // transmission axis 0…1.05 → 0.2 ticks
    [InlineData(0, 0.01, 0.002)]   // very low transmission still gets round ticks
    public void NiceTickStep_ProducesRoundSteps(double min, double max, double expectedStep)
    {
        SpectrumAxisScaler.NiceTickStep(min, max).ShouldBe(expectedStep, 1e-12);
    }

    [Fact]
    public void NiceTickStep_RespectsTargetTickCount()
    {
        // 100 nm with only 4 ticks → 25 raw → nice 50.
        SpectrumAxisScaler.NiceTickStep(1500, 1600, targetTickCount: 4).ShouldBe(50, 1e-12);
    }

    [Fact]
    public void NiceTickStep_InvalidRange_Throws()
    {
        Should.Throw<ArgumentException>(() => SpectrumAxisScaler.NiceTickStep(1600, 1500));
        Should.Throw<ArgumentOutOfRangeException>(() => SpectrumAxisScaler.NiceTickStep(0, 1, targetTickCount: 1));
    }

    [Fact]
    public void TransmissionAxisMax_PadsAboveHighestValue()
    {
        var curve = new TransmissionCurve(
            Guid.NewGuid(),
            new double[] { 1500, 1600 },
            new[] { 0.3, 0.8 },
            isAtNoiseFloor: false);

        double max = SpectrumAxisScaler.TransmissionAxisMax(new List<TransmissionCurve> { curve });

        max.ShouldBe(0.8 * 1.05, 1e-12);
    }

    [Fact]
    public void TransmissionAxisMax_AllDark_ReturnsVisibleMinimum()
    {
        var curve = new TransmissionCurve(
            Guid.NewGuid(),
            new double[] { 1500, 1600 },
            new[] { 0.0, 0.0 },
            isAtNoiseFloor: true);

        SpectrumAxisScaler.TransmissionAxisMax(new[] { curve }).ShouldBe(0.01);
    }
}
