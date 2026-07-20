using System.Numerics;
using MathNet.Numerics.LinearAlgebra;

namespace CAP_Core.LightCalculation;

/// <summary>
/// Computes the transitive (multi-hop) S-matrix from a merged single-hop matrix.
/// <see cref="SMatrix.CreateSystemSMatrix"/> only merges one-hop transfers (component
/// matrices + connection transfers); light traversing a chain of k elements needs k
/// matrix steps. Consumers that read transfer values directly — the group S-matrix
/// builder and the impulse-response extraction of the transient simulation — must sum
/// the Neumann series (M + M² + … + Mᵏ), the same series the steady-state field
/// calculation iterates in <see cref="SMatrix.CalcFieldAtPinsAfterStepsAsync"/>.
/// The series is iterated to residual-based convergence; a topology whose series
/// diverges or does not converge (resonant feedback loop) or whose converged closure
/// fabricates energy (|H| &gt; 1) aborts with <see cref="NonConvergentCircuitException"/>
/// instead of returning wrong physics (field round 4, finding [0]).
/// </summary>
public static class TransitiveSMatrixCalculator
{
    /// <summary>
    /// Residual threshold: the series has converged when the infinity norm of the next
    /// matrix power (the largest term still to be added) falls below this value.
    /// </summary>
    public const double ConvergenceEpsilon = 1e-12;

    /// <summary>
    /// Hard safety cap on the number of accumulated hops. Aligned with
    /// <see cref="SMatrix.DefaultMaxIterations"/> (the steady-state field iteration).
    /// Reaching the cap without convergence aborts the closure — a partial sum of a
    /// slowly-contracting series is a silently wrong result, not an approximation.
    /// </summary>
    public const int DefaultMaxIterations = SMatrix.DefaultMaxIterations;

    /// <summary>
    /// Tolerance above unit magnitude before a transfer counts as fabricated energy.
    /// Covers floating-point rounding of legitimately lossless (|H| = 1) paths.
    /// </summary>
    public const double EnergyTolerance = 1e-6;

    /// <summary>
    /// Sums the Neumann series (M + M² + … + Mᵏ) of <paramref name="singleHopMatrix"/>
    /// until the residual (the next matrix power) falls below
    /// <see cref="ConvergenceEpsilon"/>. A feed-forward circuit is nilpotent, so the
    /// loop terminates after its longest path length; a lossy feedback loop contracts
    /// geometrically. The result never depends on how many unrelated pins exist.
    /// </summary>
    /// <param name="singleHopMatrix">Merged single-hop S-matrix.</param>
    /// <param name="maxIterations">Hard iteration cap (see <see cref="DefaultMaxIterations"/>).</param>
    /// <exception cref="NonConvergentCircuitException">
    /// The series diverges (round-trip gain ≥ 1), does not converge within
    /// <paramref name="maxIterations"/> hops, or the converged closure contains a
    /// transfer with |H| &gt; 1 + <see cref="EnergyTolerance"/>.
    /// </exception>
    public static SMatrix Compute(SMatrix singleHopMatrix, int maxIterations = DefaultMaxIterations)
    {
        var pinIds = singleHopMatrix.PinReference.Keys.ToList();
        int n = pinIds.Count;

        if (n == 0 || maxIterations <= 0)
            return singleHopMatrix;

        var transitive = SumNeumannSeries(singleHopMatrix.SMat, n, maxIterations);

        var reversePinRef = singleHopMatrix.PinReference.ToDictionary(kv => kv.Value, kv => kv.Key);
        var transfers = new Dictionary<(Guid, Guid), Complex>();
        for (int iOut = 0; iOut < n; iOut++)
        {
            for (int iIn = 0; iIn < n; iIn++)
            {
                var val = transitive[iOut, iIn];
                if (val == Complex.Zero)
                    continue;
                ThrowIfEnergyFabricated(val);
                transfers[(reversePinRef[iIn], reversePinRef[iOut])] = val;
            }
        }

        var result = new SMatrix(pinIds, new());
        result.SetValues(transfers);
        return result;
    }

    /// <summary>
    /// Iterates the series with residual-based convergence. Divergence is detected via
    /// checkpoints every <paramref name="n"/> hops: after n hops every feed-forward path
    /// is exhausted (a nilpotent matrix satisfies Mᵏ = 0 for k ≥ n), so a residual that
    /// has not shrunk since the previous checkpoint can only come from a feedback loop
    /// with round-trip gain ≥ 1 — the series will never converge.
    /// </summary>
    private static Matrix<Complex> SumNeumannSeries(Matrix<Complex> singleHop, int n, int maxIterations)
    {
        var transitive = singleHop.Clone();
        var power = singleHop.Clone();
        int checkpointInterval = Math.Max(n, 1);
        double residualAtCheckpoint = double.PositiveInfinity;

        for (int k = 1; k < maxIterations; k++)
        {
            power = power.Multiply(singleHop);
            double residual = power.InfinityNorm();
            if (residual < ConvergenceEpsilon)
                return transitive;

            if (k % checkpointInterval == 0)
            {
                if (residual >= residualAtCheckpoint)
                    throw new NonConvergentCircuitException(NonConvergenceMessage);
                residualAtCheckpoint = residual;
            }

            transitive = transitive.Add(power);
        }

        throw new NonConvergentCircuitException(NonConvergenceMessage);
    }

    /// <summary>Rejects a converged closure that outputs more light than was injected.</summary>
    private static void ThrowIfEnergyFabricated(Complex transfer)
    {
        if (transfer.Magnitude <= 1.0 + EnergyTolerance)
            return;
        throw new NonConvergentCircuitException(
            $"The multi-hop closure produced a transfer with |H| = {transfer.Magnitude:F3} > 1 — " +
            "a passive circuit cannot output more light than was injected. The topology is " +
            "resonant or a component S-matrix is non-physical; refusing to show fabricated " +
            "energy. Use a CW/ONA sweep to analyse this circuit instead.");
    }

    private const string NonConvergenceMessage =
        "Resonant feedback loop detected — the multi-hop light-propagation series does not " +
        "converge for this topology, so any truncated result would show fabricated energy. " +
        "Transient/eye analysis cannot handle this circuit; use a CW/ONA sweep instead.";
}
