using CAP_Core.LightCalculation;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Tests for <see cref="TransitiveSMatrixCalculator"/> (field round 4 review, finding [0]):
/// the Neumann series must iterate to residual-based convergence instead of an arbitrary
/// pin-count cutoff, must abort loudly on resonant (non-convergent) topologies instead of
/// returning a truncated partial sum, and must never return a transfer with |H| &gt; 1
/// (fabricated energy) for any topology.
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
    public void Compute_ConvergentLossyLoop_SumsTheGeometricSeries()
    {
        // Loop with round-trip amplitude 0.25: the series converges to 0.5/(1-0.25) = 2/3.
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, new Complex(0.5, 0)),
            (p1, p0, new Complex(0.5, 0)));

        var closure = TransitiveSMatrixCalculator.Compute(matrix);

        var values = closure.GetNonNullValues();
        values[(p0, p1)].Magnitude.ShouldBe(0.5 / 0.75, 1e-9);
    }

    [Fact]
    public void Compute_LosslessFeedbackLoop_ThrowsInsteadOfTruncating()
    {
        // Round-trip amplitude exactly 1 (lossless cavity): the series diverges — the old
        // pin-count truncation returned an arbitrary partial sum (|H| grows with pin count).
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, Complex.One),
            (p1, p0, Complex.One));

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix));
        ex.Message.ShouldContain("feedback loop");
        ex.Message.ShouldContain("CW/ONA");
    }

    [Fact]
    public void Compute_ConvergentButAmplifyingLoop_ThrowsEnergyGuard()
    {
        // Round-trip 0.99 with unit forward transfer: the series converges — to |H| = 100.
        // A passive circuit cannot output more light than was injected; the result must be
        // rejected, not plotted.
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, Complex.One),
            (p1, p0, new Complex(0.99, 0)));

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix));
        ex.Message.ShouldContain("|H|");
    }

    [Fact]
    public void Compute_IterationCapWithoutConvergence_Throws()
    {
        // A convergent loop needs ~40 iterations to reach the residual threshold; a hard cap
        // below that must abort loudly instead of returning the partial sum.
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, new Complex(0.5, 0)),
            (p1, p0, new Complex(0.5, 0)));

        Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, maxIterations: 10));
    }

    [Fact]
    public void Compute_UnrelatedIsolatedPins_DoNotChangeTheResult()
    {
        // The old cap (maxSteps = pin count) made the closure of a resonant-ish loop depend
        // on how many unrelated pins exist. Convergence-based iteration must not.
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
}
