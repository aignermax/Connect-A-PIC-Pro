using CAP_Core.LightCalculation;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Field round 4, final review batch, findings [1]–[3]: the runtime passivity pre-check
/// must TOLERATE the measurement noise Lunima knowingly ships in bundled measured PDK
/// datasets (≤ +0.5 %, e.g. Broadband DC TE 1550 at +0.45 %) — warn and continue instead
/// of hard-failing every transient/eye/group run — while anything above the band still
/// aborts naming the component. Cross-component connection entries and unowned pins are
/// checked per entry so a gain weight can never slip past both the block check and the
/// externally-scoped energy guard.
/// </summary>
public class SingleHopPassivityCheckerTests
{
    private static SMatrix CreateMatrix(
        IReadOnlyList<Guid> pins, params (Guid From, Guid To, Complex Value)[] transfers)
    {
        var matrix = new SMatrix(pins.ToList(), new());
        matrix.SetValues(transfers.ToDictionary(t => (t.From, t.To), t => t.Value));
        return matrix;
    }

    [Fact]
    public void Compute_MeasuredNoiseExcessInLossyChain_RunsAndWarnsNamingComponent()
    {
        // A bundled measured component (σ_max = 1.0045, within the shipped noise band)
        // in an otherwise lossy chain: the run must complete — with a warning that names
        // component, wavelength and excess — instead of aborting a shipped component.
        var (aIn, aOut, bIn, bOut) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { aIn, aOut, bIn, bOut },
            (aIn, aOut, new Complex(1.0045, 0)),
            (aOut, bIn, new Complex(0.8, 0)),
            (bIn, bOut, new Complex(0.9, 0)));
        var warnings = new List<PassivityWarning>();
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string>
            {
                [aIn] = "Broadband DC TE 1550",
                [aOut] = "Broadband DC TE 1550",
                [bIn] = "Sink",
                [bOut] = "Sink",
            },
            ExternallyObservablePinIds = new[] { aIn, bOut },
            WavelengthNm = 1550,
            PassivityWarningSink = warnings.Add,
        };

        var closure = TransitiveSMatrixCalculator.Compute(matrix, context);

        closure.GetNonNullValues()[(aIn, bOut)].Magnitude.ShouldBe(1.0045 * 0.8 * 0.9, 1e-12);
        var warning = warnings.ShouldHaveSingleItem();
        warning.ComponentName.ShouldBe("Broadband DC TE 1550");
        warning.WavelengthNm.ShouldBe(1550);
        warning.ExcessPercent.ShouldBe(0.45, 0.01);
        warning.ToMessage().ShouldContain("measurement noise");
        warning.ToMessage().ShouldContain("overestimate");
    }

    [Fact]
    public void Compute_ExcessAboveTheNoiseBand_StillAbortsNamingComponent()
    {
        var (pIn, pOut) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { pIn, pOut },
            (pIn, pOut, new Complex(1.03, 0)));
        var warnings = new List<PassivityWarning>();
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string> { [pIn] = "Bad_DC", [pOut] = "Bad_DC" },
            WavelengthNm = 1550,
            PassivityWarningSink = warnings.Add,
        };

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, context));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.NonPassiveComponent);
        ex.ComponentName.ShouldBe("Bad_DC");
        warnings.ShouldBeEmpty("above the band is a hard abort, not a warning");
    }

    [Fact]
    public void Compute_ToleratedNoiseSurvivingToAnExternalPort_IsStillRejectedByTheEnergyGuard()
    {
        // The safety net that makes the tolerance honest: if the tolerated noise ever
        // survives to an externally observable transfer > 1, the energy guard still
        // aborts — tolerated noise can never be shown as a > 1 end result.
        var (pIn, pOut) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { pIn, pOut },
            (pIn, pOut, new Complex(1.0045, 0)));
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string> { [pIn] = "Noisy_DC", [pOut] = "Noisy_DC" },
            ExternallyObservablePinIds = new[] { pIn, pOut },
            WavelengthNm = 1550,
            PassivityWarningSink = _ => { },
        };

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, context));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.EnergyFabricated);
    }

    [Fact]
    public void Compute_ConnectionEntryWithGain_ThrowsNamingBothEnds()
    {
        // Finding [2]: a cross-component connection weight > 1 (negative loss from a bad
        // PDK value/override) is checked by NEITHER the per-owner block SVD NOR the
        // externally scoped energy guard — the per-entry check must catch it.
        var (aIn, aOut, bIn, bOut) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { aIn, aOut, bIn, bOut },
            (aIn, aOut, new Complex(0.9, 0)),
            (aOut, bIn, new Complex(1.02, 0)), // amplifying "connection"
            (bIn, bOut, new Complex(0.5, 0)));
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string>
            {
                [aIn] = "Splitter_A",
                [aOut] = "Splitter_A",
                [bIn] = "Coupler_B",
                [bOut] = "Coupler_B",
            },
            // Every closure transfer stays ≤ 1 between these ports — the energy guard
            // alone would let the fabricated energy through.
            ExternallyObservablePinIds = new[] { aIn, bOut },
            WavelengthNm = 1550,
        };

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, context));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.ConnectionGain);
        ex.Message.ShouldContain("connection");
        ex.Message.ShouldContain("Splitter_A");
        ex.Message.ShouldContain("Coupler_B");
        ex.Message.ShouldContain("1550");
        ex.ExcessPercent!.Value.ShouldBe(2.0, 0.05);
    }

    [Fact]
    public void Compute_AmplifyingFeedbackLoop_IsRejectedBeforeTheSolve()
    {
        // Finding [3]: a loop with round-trip gain −2 (|g| = 2, phase π) makes (I − M)
        // perfectly well-conditioned — the solve would return the finite, non-causal
        // analytic continuation m/(1−g) with all magnitudes < 1, so neither the
        // singularity check nor the energy guard would fire. Such a gain can only
        // enter through a non-passive entry, which the per-entry check rejects.
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { p0, p1 },
            (p0, p1, new Complex(-2.0, 0)),
            (p1, p0, Complex.One));
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string> { [p0] = "A", [p1] = "B" },
            ExternallyObservablePinIds = new[] { p0 },
            WavelengthNm = 1550,
        };

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, context));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.ConnectionGain);
    }

    [Fact]
    public void Compute_UnownedPinEntryWithGain_IsCheckedAsAConnectionEntry()
    {
        // Finding [2], PinOwnerNames gap: pins that never made it into the owner map
        // (LogicalPin == null when the context was built) previously escaped both
        // checks. Their entries must be validated like connection entries.
        var (ghostIn, ghostOut, sinkIn) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { ghostIn, ghostOut, sinkIn },
            (ghostIn, ghostOut, new Complex(1.5, 0)), // unowned gain entry
            (ghostOut, sinkIn, new Complex(0.1, 0)));
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string> { [sinkIn] = "Sink" },
            ExternallyObservablePinIds = new[] { sinkIn },
            WavelengthNm = 1310,
        };

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(matrix, context));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.ConnectionGain);
        ex.WavelengthNm.ShouldBe(1310);
    }

    [Fact]
    public void Compute_LossyLoopThroughToleratedNoiseBlock_StaysExactAndPassiveAtPorts()
    {
        // A lossy feedback loop containing the tolerated-noise block must still produce
        // the exact finite response (cavity buildup INSIDE the loop is legitimate; the
        // tap to the outside stays below unity).
        var (pIn, pOut, sinkIn) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var matrix = CreateMatrix(new[] { pIn, pOut, sinkIn },
            (pIn, pOut, new Complex(1.0045, 0)),
            (pOut, pIn, new Complex(0.5, 0)),   // lossy feedback inside the block
            (pOut, sinkIn, new Complex(0.3, 0))); // tap towards the external port
        var warnings = new List<PassivityWarning>();
        var context = new TransitiveClosureContext
        {
            PinOwnerNames = new Dictionary<Guid, string>
            {
                [pIn] = "Noisy_DC",
                [pOut] = "Noisy_DC",
                [sinkIn] = "Sink",
            },
            ExternallyObservablePinIds = new[] { sinkIn },
            WavelengthNm = 1550,
            PassivityWarningSink = warnings.Add,
        };

        var closure = TransitiveSMatrixCalculator.Compute(matrix, context);

        // Geometric series with round trip 0.50225: 1.0045 / (1 − 0.50225) · 0.3.
        closure.GetNonNullValues()[(pIn, sinkIn)].Magnitude
            .ShouldBe(1.0045 / (1 - 1.0045 * 0.5) * 0.3, 1e-12);
        warnings.ShouldHaveSingleItem();
    }
}
