using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using Moq;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Regression tests for the field-round-4 findings B/C: the transient simulation must
/// propagate light across component boundaries. <c>SMatrix.CreateSystemSMatrix</c> only
/// merges SINGLE-hop transfers (component matrices + connection transfers); the impulse
/// responses therefore must include the transitive (multi-hop) closure — otherwise a
/// signal injected at an input coupler never produces a trace at a connected output
/// coupler, and the Eye/BER gate wrongly reports "no light arrives".
/// </summary>
public class TimeDomainMultiHopTests
{
    private const double CenterWavelengthNm = 1550;
    private const double SpanNm = 100;
    private const int NPoints = 64;

    /// <summary>
    /// Merged one-hop system matrix of the chain
    /// aIn ─(component A: 1)→ aOut ─(waveguide: 0.8)→ bIn ─(component B: 1)→ bOut,
    /// exactly what <c>SystemMatrixBuilder.GetSystemSMatrix</c> returns for
    /// "input coupler → connection → output coupler".
    /// </summary>
    private static SMatrix CreateChainMatrix(Guid aIn, Guid aOut, Guid bIn, Guid bOut, Complex waveguide)
    {
        var matrix = new SMatrix(new List<Guid> { aIn, aOut, bIn, bOut }, new());
        matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            { (aIn, aOut), Complex.One },   // component A (e.g. input grating coupler)
            { (aOut, bIn), waveguide },     // inter-component waveguide connection
            { (bIn, bOut), Complex.One },   // component B (e.g. output grating coupler)
        });
        return matrix;
    }

    [Fact]
    public void Run_ChainOfTwoComponents_ProducesTraceAtTheFarOutputPin()
    {
        var (aIn, aOut, bIn, bOut) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) => CreateChainMatrix(aIn, aOut, bIn, bOut, new Complex(0.8, 0)));

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var inputSignal = timeDef.CreateGaussianPulse(20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);
        var simulator = new TimeDomainSimulator(mockBuilder.Object);

        var result = simulator.Run(
            new Dictionary<Guid, double[]> { { aIn, inputSignal } },
            timeDef, CenterWavelengthNm, SpanNm, NPoints);

        // The far end of the chain must receive the signal (this is what the Eye/BER
        // analysis evaluates at the designated output coupler).
        result.PinTraces.ShouldContainKey(bOut);

        // |0.8|² of the input power must arrive (lossless components, 0.8 amplitude link).
        double inputPeak = inputSignal[20] * inputSignal[20];
        double outputPeak = result.PinTraces[bOut].Max();
        outputPeak.ShouldBe(0.64 * inputPeak, 0.64 * inputPeak * 0.02);
    }

    [Fact]
    public void Run_ChainOfTwoComponents_IntermediatePinAlsoCarriesTheSignal()
    {
        var (aIn, aOut, bIn, bOut) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) => CreateChainMatrix(aIn, aOut, bIn, bOut, Complex.One));

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var inputSignal = timeDef.CreateGaussianPulse(20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);
        var simulator = new TimeDomainSimulator(mockBuilder.Object);

        var result = simulator.Run(
            new Dictionary<Guid, double[]> { { aIn, inputSignal } },
            timeDef, CenterWavelengthNm, SpanNm, NPoints);

        // Both flow ids downstream of the input must carry the (lossless) signal.
        result.PinTraces.ShouldContainKey(bIn);
        result.PinTraces.ShouldContainKey(bOut);
        double inputPeak = inputSignal[20] * inputSignal[20];
        result.PinTraces[bIn].Max().ShouldBe(inputPeak, inputPeak * 0.02);
    }
}
