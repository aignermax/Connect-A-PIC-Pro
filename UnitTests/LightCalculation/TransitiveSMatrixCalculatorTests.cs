using CAP_Core.LightCalculation;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Tests for <see cref="TransitiveSMatrixCalculator"/> (field round 4, final batch):
/// the multi-hop closure is the EXACT solution of (I − M)·X = I (linear solve, not a
/// truncated series), so ring resonators produce their true resonance response; only a
/// genuinely singular system (lossless loop exactly on resonance) aborts — naming the
/// loop — and fabricated energy at externally observable pins is always rejected.
/// </summary>
public class TransitiveSMatrixCalculatorTests
{
    private static SMatrix CreateMatrix(
        IReadOnlyList<Guid> pins, params (Guid From, Guid To, Complex Value)[] transfers)
    {
        var matrix = new SMatrix(pins.ToList(), new());
        matrix.SetValues(transfers.ToDictionary(t => (t.From, t.To), t => t.Value));
        return matrix;
    }

    [Fact]
    public void Compute_FeedForwardChain_ProducesMultiHopTransfer()
    {
        var (aIn, aOut, bIn, bOut) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { aIn, aOut, bIn, bOut },
            (aIn, aOut, Complex.One),
            (aOut, bIn, new Complex(0.8, 0)),
            (bIn, bOut, Complex.One));

        var closure = TransitiveSMatrixCalculator.Compute(matrix);

        var values = closure.GetNonNullValues();
        values[(aIn, bOut)].Magnitude.ShouldBe(0.8, 1e-12);
        values[(aIn, bIn)].Magnitude.ShouldBe(0.8, 1e-12);
        values[(aIn, aOut)].Magnitude.ShouldBe(1.0, 1e-12);
    }

    [Fact]
    public void Compute_ConvergentLossyLoop_MatchesTheGeometricSeriesExactly()
    {
        // Loop with round-trip amplitude 0.25: the closure is 0.5/(1-0.25) = 2/3 — the
        // linear solve returns this exactly (no truncation error at all).
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, new Complex(0.5, 0)),
            (p1, p0, new Complex(0.5, 0)));

        var closure = TransitiveSMatrixCalculator.Compute(matrix);

        var values = closure.GetNonNullValues();
        values[(p0, p1)].Magnitude.ShouldBe(0.5 / 0.75, 1e-12);
    }

    [Fact]
    public void Compute_LosslessFeedbackLoopOnResonance_ThrowsResonantLoop()
    {
        // Round-trip amplitude exactly 1 with zero phase: (I − M) is singular — the
        // circuit has no steady state, so no result may be fabricated.
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, Complex.One),
            (p1, p0, Complex.One));

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix));
        ex.Kind.ShouldBe(NonConvergentCircuitKind.ResonantLoop);
        ex.Message.ShouldContain("feedback loop");
    }

    [Fact]
    public void Compute_SingularLoop_NamesTheLoopComponents()
    {
        // The user-facing message must say WHICH components form the loop (field wish):
        // "feedback loop: Adiabatic_Coupler_1 ↔ Adiabatic_Coupler_2".
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, Complex.One),
            (p1, p0, Complex.One));
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string>
            {
                [p0] = "Adiabatic_Coupler_1",
                [p1] = "Adiabatic_Coupler_2",
            },
            WavelengthNm = 1550,
        };

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, context));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.ResonantLoop);
        ex.LoopComponentNames.ShouldNotBeNull();
        ex.LoopComponentNames.ShouldContain("Adiabatic_Coupler_1");
        ex.LoopComponentNames.ShouldContain("Adiabatic_Coupler_2");
        ex.WavelengthNm.ShouldBe(1550);
        ex.Message.ShouldContain("Adiabatic_Coupler_1");
        ex.Message.ShouldContain("Adiabatic_Coupler_2");
        ex.Message.ShouldContain("1550");
    }

    [Fact]
    public void Compute_ConvergentButAmplifyingLoop_ThrowsEnergyGuard()
    {
        // Round-trip 0.99 with unit forward transfer: the solve yields |H| = 100 between
        // the (default: all guarded) pins. A passive circuit cannot output more light
        // than was injected; the result must be rejected, not plotted.
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, Complex.One),
            (p1, p0, new Complex(0.99, 0)));

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix));
        ex.Kind.ShouldBe(NonConvergentCircuitKind.EnergyFabricated);
        ex.Message.ShouldContain("|H|");
    }

    [Fact]
    public void Compute_UnrelatedIsolatedPins_DoNotChangeTheResult()
    {
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var transfers = new (Guid, Guid, Complex)[]
        {
            (p0, p1, new Complex(0.5, 0)),
            (p1, p0, new Complex(0.5, 0)),
        };
        var small = CreateMatrix(new[] { p0, p1 }, transfers);
        var extraPins = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToArray();
        var large = CreateMatrix(new[] { p0, p1 }.Concat(extraPins).ToList(), transfers);

        var smallValue = TransitiveSMatrixCalculator.Compute(small).GetNonNullValues()[(p0, p1)];
        var largeValue = TransitiveSMatrixCalculator.Compute(large).GetNonNullValues()[(p0, p1)];

        largeValue.Magnitude.ShouldBe(smallValue.Magnitude, 1e-12);
    }

    [Fact]
    public void Compute_NonPassiveComponentBlock_NamesComponentWavelengthAndExcess()
    {
        // A single component transfer with |S| = 1.03 fabricates 3% energy on every
        // pass. The pre-check must name the component and the wavelength BEFORE the
        // closure can turn this into a misleading "resonant" downstream symptom.
        var (pIn, pOut) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { pIn, pOut },
            (pIn, pOut, new Complex(1.03, 0)));
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string>
            {
                [pIn] = "Bad_Coupler",
                [pOut] = "Bad_Coupler",
            },
            WavelengthNm = 1550,
        };

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, context));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.NonPassiveComponent);
        ex.ComponentName.ShouldBe("Bad_Coupler");
        ex.WavelengthNm.ShouldBe(1550);
        ex.ExcessPercent!.Value.ShouldBe(3.0, 0.05);
        ex.Message.ShouldContain("Bad_Coupler");
        ex.Message.ShouldContain("passivity");
        ex.Message.ShouldContain("1550");
    }
}
