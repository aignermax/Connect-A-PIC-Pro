using System.Numerics;

namespace CAP_Core.LightCalculation;

/// <summary>
/// Computes the transitive (multi-hop) S-matrix from a merged single-hop matrix.
/// <see cref="SMatrix.CreateSystemSMatrix"/> only merges one-hop transfers (component
/// matrices + connection transfers); light traversing a chain of k elements needs k
/// matrix steps. Consumers that read transfer values directly — the group S-matrix
/// builder and the impulse-response extraction of the transient simulation — must sum
/// the Neumann series (M + M² + … + Mᵏ), the same series the steady-state field
/// calculation iterates in <see cref="SMatrix.CalcFieldAtPinsAfterStepsAsync"/>.
/// </summary>
public static class TransitiveSMatrixCalculator
{
    /// <summary>Infinity-norm below which a matrix power counts as numerical noise.</summary>
    private const double NoiseFloor = 1e-15;

    /// <summary>
    /// Sums the Neumann series (M + M² + … + Mᵏ) of <paramref name="singleHopMatrix"/>
    /// until the matrix power falls below numerical noise or <paramref name="maxSteps"/>
    /// hops are accumulated (safety cap for near-lossless resonators whose series
    /// contracts slowly). A feed-forward circuit is nilpotent, so the loop terminates
    /// after its longest path length.
    /// </summary>
    /// <param name="singleHopMatrix">Merged single-hop S-matrix.</param>
    /// <param name="maxSteps">Upper bound on the number of hops (typically the pin-id count).</param>
    public static SMatrix Compute(SMatrix singleHopMatrix, int maxSteps)
    {
        var pinIds = singleHopMatrix.PinReference.Keys.ToList();
        int n = pinIds.Count;

        if (n == 0 || maxSteps <= 0)
            return singleHopMatrix;

        var singleHop = singleHopMatrix.SMat;
        var transitive = singleHop.Clone();
        var power = singleHop.Clone();

        for (int k = 1; k < maxSteps; k++)
        {
            power = power.Multiply(singleHop);
            if (power.InfinityNorm() < NoiseFloor)
                break;
            transitive = transitive.Add(power);
        }

        var reversePinRef = singleHopMatrix.PinReference.ToDictionary(kv => kv.Value, kv => kv.Key);
        var transfers = new Dictionary<(Guid, Guid), Complex>();
        for (int iOut = 0; iOut < n; iOut++)
        {
            for (int iIn = 0; iIn < n; iIn++)
            {
                var val = transitive[iOut, iIn];
                if (val != Complex.Zero)
                    transfers[(reversePinRef[iIn], reversePinRef[iOut])] = val;
            }
        }

        var result = new SMatrix(pinIds, new());
        result.SetValues(transfers);
        return result;
    }
}
