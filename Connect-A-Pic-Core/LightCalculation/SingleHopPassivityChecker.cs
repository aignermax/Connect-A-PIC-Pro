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
    /// Passivity excess (σ_max − 1) up to which a component block is TOLERATED with a
    /// warning instead of aborting the run. Lunima ships converted measurement datasets
    /// whose genuine measurement/fit noise overshoots passivity slightly (worst bundled
    /// case: Broadband DC TE 1550, +0.45 %) — the data is deliberately NOT normalized,
    /// so the runtime must accept exactly this band (single source of truth with
    /// <c>BundledPdkPassivityTests</c>). Tolerating it is honest ONLY because the
    /// energy guard at the externally observable pins remains the last defense: a
    /// tolerated block can never surface as a &gt; 1 end result — any transfer between
    /// circuit ports above 1 + <see cref="TransitiveSMatrixCalculator.EnergyTolerance"/>
    /// still aborts the run (<see cref="NonConvergentCircuitKind.EnergyFabricated"/>).
    /// </summary>
    public const double MeasuredDataNoiseBand = 0.005;

    /// <summary>
    /// Checks each component block of <paramref name="singleHopMatrix"/> (pins grouped
    /// by owner via <see cref="TransitiveClosureContext.PinOwnerNames"/>) for passivity,
    /// and every remaining CROSS-owner/unowned entry (connection weights) for gain.
    /// Within the merged matrix a component's rows (out-flows) and the connection rows
    /// (in-flows) are disjoint, so the block's largest singular value equals the maximum
    /// of the component matrix's and its self-connections' — the check is exact.
    /// A block excess within <see cref="MeasuredDataNoiseBand"/> warns through
    /// <see cref="TransitiveClosureContext.PassivityWarningSink"/> and continues.
    /// Without owner names the check is skipped (no attribution possible).
    /// </summary>
    /// <param name="singleHopMatrix">Merged single-hop S-matrix.</param>
    /// <param name="context">Circuit knowledge with pin owner names and wavelength.</param>
    /// <exception cref="NonConvergentCircuitException">
    /// A component block exceeds passivity by more than <see cref="MeasuredDataNoiseBand"/>,
    /// or a connection entry exceeds unit magnitude by more than
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
            if (sigmaMax <= 1.0 + MeasuredDataNoiseBand)
            {
                // Shipped measurement noise: warn (component + λ + excess) and continue.
                // The external energy guard stays armed as the last defense (see the
                // MeasuredDataNoiseBand doc for why this cannot fabricate a > 1 result).
                context.PassivityWarningSink?.Invoke(
                    new PassivityWarning(name, context.WavelengthNm, excessPercent));
                continue;
            }

            throw new NonConvergentCircuitException(
                $"Component '{name}' S-matrix exceeds passivity by {excessPercent.ToString("F1", CultureInfo.InvariantCulture)}%{WavelengthClause(context)} — " +
                "it would output more light than it receives. Check its S-parameter data/interpolation.",
                NonConvergentCircuitKind.NonPassiveComponent,
                componentName: name,
                wavelengthNm: context.WavelengthNm,
                excessPercent: excessPercent);
        }

        ThrowIfConnectionEntryHasGain(singleHopMatrix, owners, context);
    }

    /// <summary>
    /// Validates every single-hop entry the per-owner block check does NOT cover:
    /// connection weights between two components and entries touching a pin without an
    /// owner (finding [2] — e.g. a negative effective loss from a bad PDK value or
    /// instance override would otherwise slip past every guard). A passive connection
    /// can delay and attenuate but never amplify, so |t| ≤ 1 is exact physics here.
    /// </summary>
    private static void ThrowIfConnectionEntryHasGain(
        SMatrix singleHopMatrix, IReadOnlyDictionary<Guid, string> owners,
        TransitiveClosureContext context)
    {
        var reversePinRef = singleHopMatrix.PinReference.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var (row, col, value) in singleHopMatrix.SMat.EnumerateIndexed(Zeros.AllowSkip))
        {
            if (value.Magnitude <= 1.0 + TransitiveSMatrixCalculator.PassivityTolerance)
                continue;

            bool fromOwned = owners.TryGetValue(reversePinRef[col], out var fromName);
            bool toOwned = owners.TryGetValue(reversePinRef[row], out var toName);
            if (fromOwned && toOwned && fromName == toName)
                continue; // same-owner entry: covered (more strictly) by the block SVD.

            double excessPercent = (value.Magnitude - 1.0) * 100.0;
            string connection = $"'{fromName ?? "?"}' → '{toName ?? "?"}'";
            throw new NonConvergentCircuitException(
                $"The connection {connection} carries |t| = {value.Magnitude.ToString("F3", CultureInfo.InvariantCulture)} > 1{WavelengthClause(context)} — " +
                "a passive connection cannot amplify. Check its loss value, the PDK data or an instance override.",
                NonConvergentCircuitKind.ConnectionGain,
                componentName: connection,
                wavelengthNm: context.WavelengthNm,
                excessPercent: excessPercent);
        }
    }

    /// <summary>Formats " at N nm" for messages when the wavelength is known.</summary>
    private static string WavelengthClause(TransitiveClosureContext context) =>
        context.WavelengthNm is int nm
            ? $" at {nm.ToString(CultureInfo.InvariantCulture)} nm"
            : "";

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
