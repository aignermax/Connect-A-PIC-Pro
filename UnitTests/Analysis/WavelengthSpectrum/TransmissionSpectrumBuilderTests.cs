using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.Analysis.WavelengthSpectrum;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.WavelengthSpectrum;

public class TransmissionSpectrumBuilderTests
{
    private static WavelengthSweepResult CreateResult(
        int[] wavelengths, Dictionary<Guid, double>[] amplitudesPerStep, double inputPower = 1.0)
    {
        var dataPoints = new List<WavelengthDataPoint>();
        for (int i = 0; i < wavelengths.Length; i++)
        {
            var fields = amplitudesPerStep[i].ToDictionary(
                kv => kv.Key, kv => new Complex(kv.Value, 0));
            dataPoints.Add(new WavelengthDataPoint(wavelengths[i], fields, inputPower));
        }
        var config = new WavelengthSweepConfiguration(
            wavelengths[0], wavelengths[^1], wavelengths.Length);
        return new WavelengthSweepResult(
            config, dataPoints, amplitudesPerStep[0].Keys.ToList());
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-10.0, 0.1)]
    [InlineData(-3.0, 0.5011872336272722)]
    public void DbToLinear_ConvertsKnownValues(double db, double expected)
    {
        TransmissionSpectrumBuilder.DbToLinear(db).ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void Build_MapsFieldAmplitudeToLinearPowerTransmission()
    {
        // |field| = 0.5 → power 0.25 → IL ≈ −6.02 dB → T back to 0.25.
        var pin = Guid.NewGuid();
        var result = CreateResult(
            new[] { 1500, 1600 },
            new[]
            {
                new Dictionary<Guid, double> { { pin, 0.5 } },
                new Dictionary<Guid, double> { { pin, 1.0 } },
            });

        var curves = TransmissionSpectrumBuilder.Build(result);

        curves.Count.ShouldBe(1);
        curves[0].Transmission[0].ShouldBe(0.25, 1e-9);
        curves[0].Transmission[1].ShouldBe(1.0, 1e-9);
        curves[0].IsAtNoiseFloor.ShouldBeFalse();
    }

    [Fact]
    public void Build_WavelengthsMatchTheSweep()
    {
        var pin = Guid.NewGuid();
        var result = CreateResult(
            new[] { 1500, 1550, 1600 },
            Enumerable.Repeat(new Dictionary<Guid, double> { { pin, 1.0 } }, 3).ToArray());

        var curve = TransmissionSpectrumBuilder.Build(result).Single();

        curve.WavelengthsNm.ShouldBe(new double[] { 1500, 1550, 1600 });
    }

    [Fact]
    public void Build_DarkPin_IsFlaggedAtNoiseFloor()
    {
        var pin = Guid.NewGuid();
        var result = CreateResult(
            new[] { 1500, 1600 },
            new[]
            {
                new Dictionary<Guid, double> { { pin, 0.0 } },
                new Dictionary<Guid, double> { { pin, 0.0 } },
            });

        var curve = TransmissionSpectrumBuilder.Build(result).Single();

        curve.IsAtNoiseFloor.ShouldBeTrue();
        curve.Transmission.ShouldAllBe(t => t < 1e-11);
    }

    [Fact]
    public void Build_OutputPinFilter_RestrictsCurvesToMatchingPins()
    {
        var outputPin = Guid.NewGuid();
        var internalPin = Guid.NewGuid();
        var step = new Dictionary<Guid, double> { { outputPin, 1.0 }, { internalPin, 0.7 } };
        var result = CreateResult(new[] { 1500, 1600 }, new[] { step, step });

        var curves = TransmissionSpectrumBuilder.Build(result, new HashSet<Guid> { outputPin });

        curves.Single().PinId.ShouldBe(outputPin);
    }

    [Fact]
    public void Build_FilterWithoutMatch_FallsBackToAllMonitoredPins()
    {
        var pin = Guid.NewGuid();
        var step = new Dictionary<Guid, double> { { pin, 1.0 } };
        var result = CreateResult(new[] { 1500, 1600 }, new[] { step, step });

        var curves = TransmissionSpectrumBuilder.Build(result, new HashSet<Guid> { Guid.NewGuid() });

        curves.Single().PinId.ShouldBe(pin);
    }

    [Fact]
    public void Build_NullResult_Throws()
    {
        Should.Throw<ArgumentNullException>(() => TransmissionSpectrumBuilder.Build(null!));
    }
}
