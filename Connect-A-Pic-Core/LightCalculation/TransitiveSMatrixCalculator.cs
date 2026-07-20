using System.Globalization;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra;

namespace CAP_Core.LightCalculation;

/// <summary>
/// Computes the transitive (multi-hop) S-matrix from a merged single-hop matrix.
/// <see cref="SMatrix.CreateSystemSMatrix"/> only merges one-hop transfers (component
/// matrices + connection transfers); light traversing a chain of k elements needs k
/// matrix steps. The closure Σ Mᵏ (k ≥ 1) is the exact solution of the linear system
/// (I − M)·X = I, namely X − I — so instead of truncating a Neumann series this solves
/// the system directly via LU factorization (field round 4, final batch). Feedback
/// loops (ring resonators) are therefore computed EXACTLY, including resonance peaks;
/// only a genuinely singular system (lossless loop exactly on resonance — no steady
/// state exists) aborts with <see cref="NonConvergentCircuitException"/>, naming the
/// loop. A passivity pre-check per component block catches non-physical S-parameter
/// data before it can masquerade as a resonance problem.
/// </summary>
public static class TransitiveSMatrixCalculator
{
    /// <summary>
    /// Tolerance above unit magnitude before a transfer between externally observable
    /// pins counts as fabricated energy. Covers floating-point rounding of legitimately
    /// lossless (|H| = 1) paths.
    /// </summary>
    public const double EnergyTolerance = 1e-6;

    /// <summary>
    /// Tolerance above 1 for the largest singular value of a component's single-hop
    /// S-matrix block before the component counts as non-passive (data error).
    /// </summary>
    public const double PassivityTolerance = 1e-6;

    /// <summary>
    /// Condition-number estimate above which (I − M) is treated as singular: the
    /// circuit contains a lossless feedback loop exactly on resonance, so any solve
    /// result would be numerical noise instead of a steady state.
    /// </summary>
    public const double SingularityConditionLimit = 1e12;

    /// <summary>
    /// Relative residual ‖(I − M)·X − B‖ / max(1, ‖X‖) above which the solve is
    /// rejected as unreliable (near-singular system).
    /// </summary>
    public const double ResidualTolerance = 1e-8;

    /// <summary>
    /// Solve results below this magnitude are structural zeros polluted by rounding
    /// (−280 dB is far below any physical transfer) and are dropped from the closure.
    /// </summary>
    public const double NumericalZeroThreshold = 1e-14;

    /// <summary>
    /// Computes the exact multi-hop closure Σ Mᵏ (k ≥ 1) of
    /// <paramref name="singleHopMatrix"/> by solving (I − M)·X = B via LU
    /// factorization. B holds the unit vectors of <see cref="TransitiveClosureContext.SourcePinIds"/>
    /// when given (one O(n²) substitution per source), otherwise the full identity
    /// (complete closure). Feedback loops are solved exactly — ring resonators produce
    /// their true resonance response instead of a truncated series.
    /// </summary>
    /// <param name="singleHopMatrix">Merged single-hop S-matrix.</param>
    /// <param name="context">Optional circuit knowledge (see <see cref="TransitiveClosureContext"/>).</param>
    /// <exception cref="NonConvergentCircuitException">
    /// A component block is non-passive (<see cref="NonConvergentCircuitKind.NonPassiveComponent"/>),
    /// (I − M) is singular — lossless loop exactly on resonance
    /// (<see cref="NonConvergentCircuitKind.ResonantLoop"/>), or the closure contains a
    /// transfer between externally observable pins with |H| &gt; 1 + <see cref="EnergyTolerance"/>
    /// (<see cref="NonConvergentCircuitKind.EnergyFabricated"/>).
    /// </exception>
    public static SMatrix Compute(SMatrix singleHopMatrix, TransitiveClosureContext? context = null)
    {
        var pinIds = singleHopMatrix.PinReference.Keys.ToList();
        int n = pinIds.Count;
        if (n == 0)
            return singleHopMatrix;

        SingleHopPassivityChecker.ThrowIfNonPassive(singleHopMatrix, context);

        var reversePinRef = singleHopMatrix.PinReference.ToDictionary(kv => kv.Value, kv => kv.Key);
        var sourceIndices = ResolveSourceIndices(singleHopMatrix, context);

        var closure = SolveClosure(singleHopMatrix, sourceIndices, context);

        var guarded = context?.ExternallyObservablePinIds is { } observable
            ? new HashSet<Guid>(observable)
            : null;

        var transfers = new Dictionary<(Guid, Guid), Complex>();
        for (int c = 0; c < sourceIndices.Count; c++)
        {
            var inPin = reversePinRef[sourceIndices[c]];
            for (int row = 0; row < n; row++)
            {
                var val = closure[row, c];
                if (val.Magnitude < NumericalZeroThreshold)
                    continue;
                var outPin = reversePinRef[row];
                ThrowIfEnergyFabricated(val, inPin, outPin, guarded, context);
                transfers[(inPin, outPin)] = val;
            }
        }

        var result = new SMatrix(pinIds, new());
        result.SetValues(transfers);
        return result;
    }

    /// <summary>
    /// Solves (I − M)·X = B and returns X − B (the closure columns without the k = 0
    /// identity term). Throws when the system is singular or near-singular — that is a
    /// lossless feedback loop exactly on resonance, which has no steady state.
    /// </summary>
    private static Matrix<Complex> SolveClosure(
        SMatrix singleHopMatrix, IReadOnlyList<int> sourceIndices, TransitiveClosureContext? context)
    {
        int n = singleHopMatrix.PinReference.Count;
        var m = singleHopMatrix.SMat;
        var a = Matrix<Complex>.Build.DenseIdentity(n).Subtract(m);

        var b = Matrix<Complex>.Build.Dense(n, sourceIndices.Count);
        for (int c = 0; c < sourceIndices.Count; c++)
            b[sourceIndices[c], c] = Complex.One;

        var x = a.LU().Solve(b);

        ThrowIfSingular(a, b, x, singleHopMatrix, context);

        return x.Subtract(b);
    }

    /// <summary>
    /// Rejects a solve whose result is non-finite, whose residual exceeds
    /// <see cref="ResidualTolerance"/>, or whose condition estimate exceeds
    /// <see cref="SingularityConditionLimit"/> — all three signatures of a lossless
    /// feedback loop exactly on resonance. The thrown message names the loop.
    /// </summary>
    private static void ThrowIfSingular(
        Matrix<Complex> a, Matrix<Complex> b, Matrix<Complex> x,
        SMatrix singleHopMatrix, TransitiveClosureContext? context)
    {
        double maxAbsX = 0;
        bool finite = true;
        foreach (var value in x.Enumerate())
        {
            double magnitude = value.Magnitude;
            if (double.IsNaN(magnitude) || double.IsInfinity(magnitude))
            {
                finite = false;
                break;
            }
            maxAbsX = Math.Max(maxAbsX, magnitude);
        }

        if (finite)
        {
            double conditionEstimate = a.InfinityNorm() * maxAbsX;
            double residual = (a.Multiply(x) - b).Enumerate().Max(v => v.Magnitude);
            if (conditionEstimate <= SingularityConditionLimit
                && residual <= ResidualTolerance * Math.Max(1.0, maxAbsX))
            {
                return;
            }
        }

        throw new NonConvergentCircuitException(
            BuildResonantLoopMessage(singleHopMatrix, context, out var loopNames),
            NonConvergentCircuitKind.ResonantLoop,
            loopComponentNames: loopNames,
            wavelengthNm: context?.WavelengthNm);
    }

    /// <summary>Builds the English resonant-loop message, naming the loop when possible.</summary>
    private static string BuildResonantLoopMessage(
        SMatrix singleHopMatrix, TransitiveClosureContext? context, out IReadOnlyList<string> loopNames)
    {
        loopNames = FeedbackLoopFinder.FindLoopComponentNames(singleHopMatrix, context?.PinOwnerNames);
        string loopClause = loopNames.Count > 0
            ? $" — feedback loop: {FeedbackLoopFinder.Describe(loopNames)}"
            : " — a lossless feedback loop sits exactly on resonance";
        string wavelengthClause = context?.WavelengthNm is int nm
            ? $" at {nm.ToString(CultureInfo.InvariantCulture)} nm"
            : "";
        return "This circuit has no steady state" + wavelengthClause + loopClause +
               ": its circulating light never settles, so no physical result can be shown. " +
               "Add loss to the loop or detune the wavelength.";
    }

    /// <summary>
    /// Rejects a closure transfer between two externally observable pins that outputs
    /// more light than was injected. Field enhancement at pins inside a resonator is
    /// legitimate physics and is only guarded when no observable-pin set is given.
    /// </summary>
    private static void ThrowIfEnergyFabricated(
        Complex transfer, Guid inPin, Guid outPin,
        HashSet<Guid>? guardedPins, TransitiveClosureContext? context)
    {
        if (transfer.Magnitude <= 1.0 + EnergyTolerance)
            return;
        if (guardedPins != null && (!guardedPins.Contains(inPin) || !guardedPins.Contains(outPin)))
            return;

        string path = DescribeTransferPath(inPin, outPin, context);
        string wavelengthClause = context?.WavelengthNm is int nm
            ? $" at {nm.ToString(CultureInfo.InvariantCulture)} nm"
            : "";
        throw new NonConvergentCircuitException(
            $"The multi-hop closure produced a transfer with |H| = {transfer.Magnitude.ToString("F3", CultureInfo.InvariantCulture)} > 1{wavelengthClause}{path} — " +
            "a passive circuit cannot output more light than was injected. A component " +
            "S-matrix is non-physical; refusing to show fabricated energy. Check the " +
            "S-parameter data of the involved components.",
            NonConvergentCircuitKind.EnergyFabricated,
            wavelengthNm: context?.WavelengthNm,
            excessPercent: (transfer.Magnitude - 1.0) * 100.0);
    }

    /// <summary>Names the components owning the violating pin pair, when known.</summary>
    private static string DescribeTransferPath(Guid inPin, Guid outPin, TransitiveClosureContext? context)
    {
        if (context?.PinOwnerNames is not { } owners)
            return "";
        owners.TryGetValue(inPin, out var fromName);
        owners.TryGetValue(outPin, out var toName);
        if (fromName == null && toName == null)
            return "";
        return $" (from '{fromName ?? "?"}' to '{toName ?? "?"}')";
    }

    /// <summary>
    /// Maps the requested source pins to matrix column indices; null or no matches
    /// resolves to every column (full closure).
    /// </summary>
    private static IReadOnlyList<int> ResolveSourceIndices(
        SMatrix singleHopMatrix, TransitiveClosureContext? context)
    {
        if (context?.SourcePinIds is { } sources)
        {
            var indices = sources
                .Where(singleHopMatrix.PinReference.ContainsKey)
                .Select(id => singleHopMatrix.PinReference[id])
                .Distinct()
                .OrderBy(i => i)
                .ToList();
            if (indices.Count > 0)
                return indices;
        }
        return Enumerable.Range(0, singleHopMatrix.PinReference.Count).ToList();
    }
}
