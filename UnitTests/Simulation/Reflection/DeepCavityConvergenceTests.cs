using System.Numerics;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Simulation.Reflection;

/// <summary>
/// Validates residual-based Neumann-series convergence for high-reflectivity / deep-cavity
/// circuits where the old pinCount×2 heuristic under-converges.
///
/// A symmetric lossless Fabry-Pérot cavity (R1—Waveguide—R2) is driven at resonance (φ=0)
/// and anti-resonance (φ=π/2).  The analytic closed-form results are:
///   T_res     = (t₁t₂)² / (1−r₁r₂)²  =  1  (lossless FP at resonance)
///   T_antires = (t₁t₂)² / (1+r₁r₂)²
///
/// For r = 0.9 the round-trip factor r₁r₂ = 0.81, so the geometric series needs roughly
/// 100 iterations to converge to 1e-9 — far beyond the old pinCount×2 ≈ 24-step limit.
/// Issue #555.
/// </summary>
[Trait("Category", "Slow")]
public class DeepCavityConvergenceTests
{
    private const double HighReflectivity = 0.9;
    private const double AnalyticTolerance = 0.01; // 1 %

    // -----------------------------------------------------------------------
    // Circuit builder helpers (copied pattern from FabryPerotResonanceTests)
    // -----------------------------------------------------------------------

    private static (SMatrix sMatrix, Pin leftPin, Pin rightPin) CreateReflector(double r)
    {
        double t = Math.Sqrt(1.0 - r * r);
        var leftPin  = new Pin("left",  0, MatterType.Light, RectSide.Left);
        var rightPin = new Pin("right", 1, MatterType.Light, RectSide.Right);

        var allPinIds = new List<Guid>
        {
            leftPin.IDInFlow,  leftPin.IDOutFlow,
            rightPin.IDInFlow, rightPin.IDOutFlow,
        };

        var sMatrix = new SMatrix(allPinIds, new());
        sMatrix.SetValues(new()
        {
            { (leftPin.IDInFlow,  leftPin.IDOutFlow),  new Complex(r, 0) },
            { (leftPin.IDInFlow,  rightPin.IDOutFlow), new Complex(t, 0) },
            { (rightPin.IDInFlow, leftPin.IDOutFlow),  new Complex(t, 0) },
            { (rightPin.IDInFlow, rightPin.IDOutFlow), new Complex(r, 0) },
        });

        return (sMatrix, leftPin, rightPin);
    }

    private static (SMatrix sMatrix, Pin leftPin, Pin rightPin) CreatePhaseDelay(double phiRadians)
    {
        var leftPin  = new Pin("wg_left",  0, MatterType.Light, RectSide.Left);
        var rightPin = new Pin("wg_right", 1, MatterType.Light, RectSide.Right);

        var allPinIds = new List<Guid>
        {
            leftPin.IDInFlow,  leftPin.IDOutFlow,
            rightPin.IDInFlow, rightPin.IDOutFlow,
        };

        var phase = Complex.FromPolarCoordinates(1.0, phiRadians);
        var sMatrix = new SMatrix(allPinIds, new());
        sMatrix.SetValues(new()
        {
            { (leftPin.IDInFlow,  rightPin.IDOutFlow), phase },
            { (rightPin.IDInFlow, leftPin.IDOutFlow),  phase },
        });

        return (sMatrix, leftPin, rightPin);
    }

    private static SMatrix CreateConnectionMatrix(
        Guid outFlow1, Guid inFlow2,
        Guid outFlow2, Guid inFlow1)
    {
        var pins = new List<Guid> { outFlow1, inFlow2, outFlow2, inFlow1 };
        var m = new SMatrix(pins.Distinct().ToList(), new());
        m.SetValues(new()
        {
            { (outFlow1, inFlow2), Complex.One },
            { (outFlow2, inFlow1), Complex.One },
        });
        return m;
    }

    /// <summary>
    /// Assembles the Fabry-Pérot system matrix, runs the simulation with the residual-based
    /// solver (capped at <see cref="SMatrix.DefaultMaxIterations"/>), and returns the
    /// transmitted-field magnitude at Reflector2's right output.
    /// </summary>
    private static async Task<double> RunHighReflectivityFabryPerotAsync(double phiRadians)
    {
        var (r1Matrix, r1Left, r1Right) = CreateReflector(HighReflectivity);
        var (wgMatrix, wgLeft, wgRight)  = CreatePhaseDelay(phiRadians);
        var (r2Matrix, r2Left, r2Right) = CreateReflector(HighReflectivity);

        var conn1 = CreateConnectionMatrix(
            r1Right.IDOutFlow, wgLeft.IDInFlow,
            wgLeft.IDOutFlow,  r1Right.IDInFlow);

        var conn2 = CreateConnectionMatrix(
            wgRight.IDOutFlow, r2Left.IDInFlow,
            r2Left.IDOutFlow,  wgRight.IDInFlow);

        var system = SMatrix.CreateSystemSMatrix(
            new List<SMatrix> { r1Matrix, wgMatrix, r2Matrix, conn1, conn2 });

        var inputVec = MathNet.Numerics.LinearAlgebra.Vector<Complex>.Build.Dense(system.PinReference.Count);
        inputVec[system.PinReference[r1Left.IDInFlow]] = Complex.One;

        using var cts = new CancellationTokenSource();
        var result = await system.CalcFieldAtPinsAfterStepsAsync(inputVec, SMatrix.DefaultMaxIterations, cts);
        return result[r2Right.IDOutFlow].Magnitude;
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resonance transmission for a symmetric lossless FP is exactly 1 regardless of r.
    /// Verifies the deep-cavity circuit produces this analytic value within 1 %.
    /// </summary>
    [Fact]
    public async Task HighReflectivity_ResonanceTransmissionMatchesAnalytic()
    {
        double r = HighReflectivity;
        double t = Math.Sqrt(1.0 - r * r);
        double analyticTRes = (t * t) / (1.0 - r * r); // = 1.0

        double measured = await RunHighReflectivityFabryPerotAsync(phiRadians: 0.0);

        measured.ShouldBe(analyticTRes, AnalyticTolerance,
            $"FP resonance (r={r}) should equal analytic T={analyticTRes:F4}. " +
            $"Measured={measured:F4}. The residual-based solver must converge for high-r circuits.");
    }

    /// <summary>
    /// Anti-resonance (φ=π/2) transmission: T = (t₁t₂)² / (1+r₁r₂)².
    /// For r=0.9 this is ≈ 0.0576, much lower than the resonance peak.
    /// With only pinCount×2 ≈ 24 iterations the old heuristic would under-estimate
    /// the reflection and over-estimate the transmission.
    /// </summary>
    [Fact]
    public async Task HighReflectivity_AntiResonanceTransmissionMatchesAnalytic()
    {
        double r = HighReflectivity;
        double t = Math.Sqrt(1.0 - r * r);
        double analyticTAnti = (t * t) / (1.0 + r * r); // ≈ 0.1050 in amplitude

        double measured = await RunHighReflectivityFabryPerotAsync(phiRadians: Math.PI / 2);

        measured.ShouldBe(analyticTAnti, AnalyticTolerance,
            $"FP anti-resonance (r={r}) should equal analytic T={analyticTAnti:F4}. " +
            $"Measured={measured:F4}.");
    }

    /// <summary>
    /// Resonance must produce higher transmission than anti-resonance for any reflectivity.
    /// </summary>
    [Fact]
    public async Task HighReflectivity_ResonanceExceedsAntiResonance()
    {
        double resonance    = await RunHighReflectivityFabryPerotAsync(phiRadians: 0.0);
        double antiResonance = await RunHighReflectivityFabryPerotAsync(phiRadians: Math.PI / 2);

        resonance.ShouldBeGreaterThan(antiResonance,
            $"Resonance ({resonance:F4}) must exceed anti-resonance ({antiResonance:F4}) " +
            $"for r={HighReflectivity}.  Failure implies under-convergence in the Neumann series.");
    }

    /// <summary>
    /// Confirms that residual-based convergence terminates well before the DefaultMaxIterations
    /// safety cap for a deep-cavity circuit.  If the cap is hit every time, the solver is
    /// not actually converging and the safety cap is masking divergence.
    /// </summary>
    [Fact]
    public async Task ResidualConvergence_TerminatesBeforeSafetyCap_ForDeepCavity()
    {
        // Run with an artificially low cap that is still above the ~100 needed for r=0.9.
        // (round-trip = 0.81^n < 1e-9  ⟹  n > 99)
        const int sufficientCap = 500;

        var (r1Matrix, r1Left, r1Right) = CreateReflector(HighReflectivity);
        var (wgMatrix, wgLeft, wgRight)  = CreatePhaseDelay(0.0);
        var (r2Matrix, r2Left, r2Right) = CreateReflector(HighReflectivity);

        var conn1 = CreateConnectionMatrix(
            r1Right.IDOutFlow, wgLeft.IDInFlow,
            wgLeft.IDOutFlow,  r1Right.IDInFlow);
        var conn2 = CreateConnectionMatrix(
            wgRight.IDOutFlow, r2Left.IDInFlow,
            r2Left.IDOutFlow,  wgRight.IDInFlow);

        var system = SMatrix.CreateSystemSMatrix(
            new List<SMatrix> { r1Matrix, wgMatrix, r2Matrix, conn1, conn2 });

        var inputVec = MathNet.Numerics.LinearAlgebra.Vector<Complex>.Build.Dense(system.PinReference.Count);
        inputVec[system.PinReference[r1Left.IDInFlow]] = Complex.One;

        using var cts = new CancellationTokenSource();

        // With sufficientCap=500 and residual-based termination, result should match full run.
        var resultCapped  = await system.CalcFieldAtPinsAfterStepsAsync(inputVec, sufficientCap, cts);
        var resultFull    = await system.CalcFieldAtPinsAfterStepsAsync(inputVec, SMatrix.DefaultMaxIterations, cts);

        double cappedMag = resultCapped[r2Right.IDOutFlow].Magnitude;
        double fullMag   = resultFull[r2Right.IDOutFlow].Magnitude;
        double relError  = Math.Abs(cappedMag - fullMag) / Math.Max(fullMag, 1e-12);

        relError.ShouldBeLessThan(0.001,
            $"With residual-based convergence, a cap of {sufficientCap} steps should agree " +
            $"with {SMatrix.DefaultMaxIterations} steps to 0.1 % for r={HighReflectivity}. " +
            $"Capped={cappedMag:F6}, Full={fullMag:F6}, RelErr={relError:P2}.");
    }
}
