using System.Globalization;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra;

namespace CAP_Core.LightCalculation;

/// <summary>
/// Validates that every component's single-hop S-matrix block is passive (largest
/// singular value ≤ 1) BEFORE the multi-hop closure is solved (field round 4, final
/// batch). A non-passive block fabricates energy on every pass, so the closure of a
/// plain feed-forward chain can exceed |H| = 1 — this check names the actual culprit
/// (component + wavelength + excess) instead of letting the energy guard report a
/// misleading "resonant or non-physical" downstream symptom.
/// </summary>
public static class SingleHopPassivityChecker
{
    /// <summary>
    /// Checks each component block of <paramref name="singleHopMatrix"/> (pins grouped
    /// by owner via <see cref="TransitiveClosureContext.PinOwnerNames"/>) for passivity.
    /// Within the merged matrix a component's rows (out-flows) and the connection rows
    /// (in-flows) are disjoint, so the block's largest singular value equals the maximum
    /// of the component matrix's and its self-connections' — the check is exact.
    /// Without owner names the check is skipped (no attribution possible).
    /// </summary>
    /// <param name="singleHopMatrix">Merged single-hop S-matrix.</param>
    /// <param name="context">Circuit knowledge with pin owner names and wavelength.</param>
    /// <exception cref="NonConvergentCircuitException">
    /// A component block exceeds passivity by more than
    /// <see cref="TransitiveSMatrixCalculator.PassivityTolerance"/>.
    /// </exception>
    public static void ThrowIfNonPassive(SMatrix singleHopMatrix, TransitiveClosureContext? context)
    {
        if (context?.PinOwnerNames is not { } owners)
            return;

        foreach (var (name, indices) in GroupPinIndicesByOwner(singleHopMatrix, owners))
        {
            double sigmaMax = LargestSingularValue(singleHopMatrix.SMat, indices);
            if (sigmaMax <= 1.0 + TransitiveSMatrixCalculator.PassivityTolerance)
                continue;

            double excessPercent = (sigmaMax - 1.0) * 100.0;
            string wavelengthClause = context.WavelengthNm is int nm
                ? $" at {nm.ToString(CultureInfo.InvariantCulture)} nm"
                : "";
            throw new NonConvergentCircuitException(
                $"Component '{name}' S-matrix exceeds passivity by {excessPercent.ToString("F1", CultureInfo.InvariantCulture)}%{wavelengthClause} — " +
                "it would output more light than it receives. Check its S-parameter data/interpolation.",
                NonConvergentCircuitKind.NonPassiveComponent,
                componentName: name,
                wavelengthNm: context.WavelengthNm,
                excessPercent: excessPercent);
        }
    }

    /// <summary>Groups the matrix indices of all pins sharing an owner name.</summary>
    private static Dictionary<string, List<int>> GroupPinIndicesByOwner(
        SMatrix singleHopMatrix, IReadOnlyDictionary<Guid, string> owners)
    {
        var groups = new Dictionary<string, List<int>>();
        foreach (var (pinId, index) in singleHopMatrix.PinReference)
        {
            if (!owners.TryGetValue(pinId, out var name))
                continue;
            if (!groups.TryGetValue(name, out var indices))
                groups[name] = indices = new List<int>();
            indices.Add(index);
        }
        return groups;
    }

    /// <summary>
    /// Largest singular value of the sub-block spanned by <paramref name="indices"/>.
    /// Blocks are small (2 × pin count of one component), so a dense SVD is cheap.
    /// </summary>
    private static double LargestSingularValue(Matrix<Complex> sMat, IReadOnlyList<int> indices)
    {
        int size = indices.Count;
        var block = Matrix<Complex>.Build.Dense(size, size);
        bool anyNonZero = false;
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                var value = sMat[indices[r], indices[c]];
                if (value == Complex.Zero)
                    continue;
                block[r, c] = value;
                anyNonZero = true;
            }
        }
        return anyNonZero ? block.L2Norm() : 0.0;
    }
}
