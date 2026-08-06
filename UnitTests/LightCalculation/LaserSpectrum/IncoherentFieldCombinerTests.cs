using System.Numerics;
using CAP_Core.LightCalculation.LaserSpectrum;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation.LaserSpectrum;

public class IncoherentFieldCombinerTests
{
    [Fact]
    public void SingleRun_IsReturnedUnchanged()
    {
        var pin = Guid.NewGuid();
        var fields = new Dictionary<Guid, Complex> { [pin] = new Complex(0.3, 0.4) };

        var combined = IncoherentFieldCombiner.Combine(new[] { fields });

        combined.ShouldBeSameAs(fields);
    }

    [Fact]
    public void PowersAddIncoherently_NotAmplitudes()
    {
        var pin = Guid.NewGuid();
        // Two equal fields with opposite phase: coherent addition would cancel to
        // zero, incoherent addition must yield twice the power.
        var runA = new Dictionary<Guid, Complex> { [pin] = new Complex(1, 0) };
        var runB = new Dictionary<Guid, Complex> { [pin] = new Complex(-1, 0) };

        var combined = IncoherentFieldCombiner.Combine(new[] { runA, runB });

        double power = combined[pin].Magnitude * combined[pin].Magnitude;
        power.ShouldBe(2.0, tolerance: 1e-12);
    }

    [Fact]
    public void PhaseOfStrongestContribution_IsKept()
    {
        var pin = Guid.NewGuid();
        var weak = new Dictionary<Guid, Complex>
        {
            [pin] = Complex.FromPolarCoordinates(0.1, 1.0),
        };
        var strong = new Dictionary<Guid, Complex>
        {
            [pin] = Complex.FromPolarCoordinates(0.9, -2.0),
        };

        var combined = IncoherentFieldCombiner.Combine(new[] { weak, strong });

        combined[pin].Phase.ShouldBe(-2.0, tolerance: 1e-12);
    }

    [Fact]
    public void PinsMissingInSomeRuns_AreStillCombined()
    {
        var pinA = Guid.NewGuid();
        var pinB = Guid.NewGuid();
        var runA = new Dictionary<Guid, Complex> { [pinA] = new Complex(1, 0) };
        var runB = new Dictionary<Guid, Complex> { [pinB] = new Complex(0, 2) };

        var combined = IncoherentFieldCombiner.Combine(new[] { runA, runB });

        combined[pinA].Magnitude.ShouldBe(1.0, tolerance: 1e-12);
        combined[pinB].Magnitude.ShouldBe(2.0, tolerance: 1e-12);
    }
}
