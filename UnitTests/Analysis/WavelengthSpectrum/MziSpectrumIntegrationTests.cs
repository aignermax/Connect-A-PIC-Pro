using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.Analysis.WavelengthSpectrum;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.WavelengthSpectrum;

/// <summary>
/// End-to-end spectrum test with an MZI-like transfer function: sweeps a circuit
/// whose bar-port amplitude is cos(π·(λ−1500)/50) — the textbook Mach-Zehnder
/// response — and verifies the transmission curve shows the expected periodic
/// pass/dip pattern (maxima at 1500/1550/1600 nm, nulls at 1525/1575 nm).
/// </summary>
public class MziSpectrumIntegrationTests
{
    private const int StartNm = 1500;
    private const int EndNm = 1600;
    private const double FringePeriodNm = 50.0;

    private readonly Guid _inputPinId = Guid.NewGuid();
    private readonly Guid _outputPinId = Guid.NewGuid();

    /// <summary>Bar-port field amplitude of an ideal MZI at the given wavelength.</summary>
    private static double MziAmplitude(int wavelengthNm) =>
        Math.Cos(Math.PI * (wavelengthNm - StartNm) / FringePeriodNm);

    private Mock<ISystemMatrixBuilder> CreateMziBuilder()
    {
        var builder = new Mock<ISystemMatrixBuilder>();
        builder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns<int>(wl =>
            {
                var matrix = new SMatrix(
                    new List<Guid> { _inputPinId, _outputPinId }, new List<(Guid, double)>());
                matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
                {
                    { (_inputPinId, _outputPinId), new Complex(MziAmplitude(wl), 0) },
                });
                return matrix;
            });
        return builder;
    }

    private Mock<IExternalPortManager> CreatePortManager()
    {
        var input = new ExternalInput("mzi-in", LaserType.Red, 0, new Complex(1.0, 0));
        var ports = new Mock<IExternalPortManager>();
        ports.Setup(p => p.GetAllExternalInputs())
            .Returns(new ConcurrentBag<ExternalInput> { input });
        ports.Setup(p => p.GetUsedExternalInputs())
            .Returns(new ConcurrentBag<UsedInput> { new(input, _inputPinId) });
        return ports;
    }

    private async Task<TransmissionCurve> SweepMziAndGetOutputCurve()
    {
        var sweeper = new WavelengthSweeper(CreateMziBuilder().Object, CreatePortManager().Object);
        var config = new WavelengthSweepConfiguration(StartNm, EndNm, 101);

        var result = await sweeper.RunSweepAsync(config, new GridManager(4, 4));

        var curves = TransmissionSpectrumBuilder.Build(
            result, new HashSet<Guid> { _outputPinId });
        return curves.Single(c => c.PinId == _outputPinId);
    }

    [Fact]
    public async Task MziSweep_ShowsFullTransmissionAtFringeMaxima()
    {
        var curve = await SweepMziAndGetOutputCurve();

        foreach (double peakNm in new[] { 1500.0, 1550.0, 1600.0 })
        {
            int index = curve.WavelengthsNm.ToList().IndexOf(peakNm);
            index.ShouldBeGreaterThanOrEqualTo(0);
            curve.Transmission[index].ShouldBeGreaterThan(0.95);
        }
    }

    [Fact]
    public async Task MziSweep_ShowsDeepDipsAtFringeNulls()
    {
        var curve = await SweepMziAndGetOutputCurve();

        foreach (double dipNm in new[] { 1525.0, 1575.0 })
        {
            int index = curve.WavelengthsNm.ToList().IndexOf(dipNm);
            index.ShouldBeGreaterThanOrEqualTo(0);
            curve.Transmission[index].ShouldBeLessThan(0.01);
        }
    }

    [Fact]
    public async Task MziSweep_IsPeriodic_WithExactlyTwoDipsInRange()
    {
        var curve = await SweepMziAndGetOutputCurve();

        int dipCount = 0;
        for (int i = 1; i < curve.Transmission.Count - 1; i++)
        {
            bool isLocalMinimum = curve.Transmission[i] < curve.Transmission[i - 1]
                && curve.Transmission[i] < curve.Transmission[i + 1];
            if (isLocalMinimum && curve.Transmission[i] < 0.1)
                dipCount++;
        }

        dipCount.ShouldBe(2);
        curve.IsAtNoiseFloor.ShouldBeFalse();
    }
}
