using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace CAP_Core.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Builds per-connection impulse responses h(t) by sweeping the system S-matrix
/// across a wavelength grid and computing the IFFT of each connection's frequency response.
/// </summary>
public class ImpulseResponseBuilder
{
    private const double SpeedOfLightNmPerS = 2.998e17;
    private const int MaxMemoryWarningMB = 10;

    /// <summary>Bytes per (input, output) pair: nPoints complex samples for hFreq plus the h(t) clone.</summary>
    private const int BytesPerPairPerPoint = 16 * 2;

    /// <summary>
    /// Coarse pair-count assumption for the pre-flight memory check that runs before any
    /// S-matrix exists. The REAL pair count of the transitive closure is enforced while
    /// the frequency responses are collected (field round 4, finding [3]).
    /// </summary>
    private const int CoarseConnectionEstimate = 200;

    private readonly ISystemMatrixBuilder _matrixBuilder;

    /// <summary>Initializes a new instance of <see cref="ImpulseResponseBuilder"/>.</summary>
    /// <param name="matrixBuilder">System S-matrix builder (provides S(λ) per wavelength).</param>
    public ImpulseResponseBuilder(ISystemMatrixBuilder matrixBuilder)
    {
        _matrixBuilder = matrixBuilder ?? throw new ArgumentNullException(nameof(matrixBuilder));
    }

    /// <summary>
    /// Sweeps the system S-matrix across a uniform frequency grid derived from the
    /// given wavelength range and computes the IFFT of each non-zero connection's
    /// frequency response to produce a list of impulse responses.
    /// </summary>
    /// <param name="centerWavelengthNm">Centre wavelength in nm (e.g. 1550).</param>
    /// <param name="spanNm">Full wavelength span in nm (e.g. 100).</param>
    /// <param name="nPoints">Number of frequency points (must be ≥ 2).</param>
    /// <param name="activeInputPinIds">
    /// Pins where light is injected. When given, the multi-hop closure is restricted to
    /// the subgraph reachable from these sources and only (source → reachable) pairs are
    /// returned — the pairs the simulators actually convolve. Null computes the closure
    /// of the full pin matrix (all pairs).
    /// </param>
    /// <returns>One <see cref="ImpulseResponse"/> per non-zero (input, output) pin pair.</returns>
    /// <param name="circuitContext">
    /// Optional circuit knowledge (pin owner names, externally observable pins) so the
    /// closure solve can name a non-passive component or a resonant feedback loop and
    /// scope the energy guard to real circuit ports. Wavelength and sources are filled
    /// in per closure call.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any nonlinear connection is found (Phase 1 gate) or the closure's
    /// pair count exceeds the memory gate.
    /// </exception>
    /// <exception cref="NonConvergentCircuitException">
    /// A component's S-matrix data is non-passive, a lossless feedback loop sits exactly
    /// on resonance (no steady state), or the closure fabricates energy at circuit ports.
    /// </exception>
    public IReadOnlyList<ImpulseResponse> Build(
        double centerWavelengthNm, double spanNm, int nPoints,
        IReadOnlyCollection<Guid>? activeInputPinIds = null,
        TransitiveClosureContext? circuitContext = null)
    {
        if (nPoints < 2) throw new ArgumentOutOfRangeException(nameof(nPoints), "Need at least 2 frequency points.");
        if (spanNm <= 0) throw new ArgumentOutOfRangeException(nameof(spanNm));

        ThrowIfMemoryLimitExceeded(CoarseConnectionEstimate, nPoints);

        var (freqGrid, dt) = BuildFrequencyGrid(centerWavelengthNm, spanNm, nPoints);

        // Check for TRULY nonlinear connections — Phase 1 only supports linear circuits.
        // Parametric (slider-only) formulas are constants during a run and stay eligible;
        // they are pre-evaluated into concrete weights in ComputeTransitiveValues.
        int referenceNm = FreqToWavelengthNmInt(freqGrid[nPoints / 2]);
        var referenceMatrix = _matrixBuilder.GetSystemSMatrix(referenceNm);
        if (ParametricConnectionEvaluator.CountTrulyNonLinear(referenceMatrix) > 0)
        {
            throw new InvalidOperationException(
                "Time-domain simulation (Phase 1) supports linear circuits only. " +
                "The design contains nonlinear connections. Remove or linearize them before running transient analysis.");
        }

        // Cache S-matrix results by rounded wavelength nm to avoid duplicate calls.
        // Seeding with the reference wavelength keeps its matrix from being built twice.
        var matrixCache = new Dictionary<int, Dictionary<(Guid, Guid), Complex>>
        {
            [referenceNm] = ComputeTransitiveValues(referenceMatrix, activeInputPinIds, circuitContext, referenceNm),
        };

        // Memory gate against the REAL pair count of the closure (finding [3]) — fail
        // fast with the actionable message instead of running into an OOM allocation.
        ThrowIfMemoryLimitExceeded(matrixCache[referenceNm].Count, nPoints);

        var hFreq = CollectFrequencyResponses(freqGrid, nPoints, matrixCache, activeInputPinIds, circuitContext);
        return InverseTransform(hFreq, nPoints, dt);
    }

    /// <summary>Fills H[k] for each frequency point; new pairs re-check the memory gate.</summary>
    private Dictionary<(Guid, Guid), Complex[]> CollectFrequencyResponses(
        double[] freqGrid, int nPoints,
        Dictionary<int, Dictionary<(Guid, Guid), Complex>> matrixCache,
        IReadOnlyCollection<Guid>? activeInputPinIds,
        TransitiveClosureContext? circuitContext)
    {
        var hFreq = new Dictionary<(Guid, Guid), Complex[]>();
        for (int k = 0; k < nPoints; k++)
        {
            int wavelengthNm = FreqToWavelengthNmInt(freqGrid[k]);
            if (!matrixCache.TryGetValue(wavelengthNm, out var values))
            {
                // The system matrix only carries SINGLE-hop transfers (component
                // matrices + connection transfers); the frequency response of a pin
                // pair must include the transitive multi-hop closure, otherwise light
                // never crosses a component boundary in the transient simulation.
                var sMatrix = _matrixBuilder.GetSystemSMatrix(wavelengthNm);
                values = ComputeTransitiveValues(sMatrix, activeInputPinIds, circuitContext, wavelengthNm);
                matrixCache[wavelengthNm] = values;
            }

            foreach (var (conn, val) in values)
            {
                if (!hFreq.TryGetValue(conn, out var arr))
                {
                    ThrowIfMemoryLimitExceeded(hFreq.Count + 1L, nPoints);
                    arr = new Complex[nPoints];
                    hFreq[conn] = arr;
                }
                arr[k] = val;
            }
        }
        return hFreq;
    }

    /// <summary>
    /// Multi-hop transfer values for one wavelength. With active sources given, the
    /// closure runs on the reachable subgraph only and solves just the source columns
    /// of (I − M)·X = B — exactly the pairs the simulators convolve (finding [4]: no
    /// dense full-matrix closure per wavelength).
    /// </summary>
    private static Dictionary<(Guid, Guid), Complex> ComputeTransitiveValues(
        SMatrix sMatrix, IReadOnlyCollection<Guid>? activeInputPinIds,
        TransitiveClosureContext? circuitContext, int wavelengthNm)
    {
        // Slider-bound formulas are constants at simulation time: bake them into
        // the S-matrix so the closure (and reachability) sees their transfers.
        ParametricConnectionEvaluator.EvaluateParametricConnections(sMatrix);
        var scoped = activeInputPinIds == null
            ? sMatrix
            : ReachableSubMatrixExtractor.ExtractReachable(sMatrix, activeInputPinIds);
        var context = (circuitContext ?? new TransitiveClosureContext()) with
        {
            SourcePinIds = activeInputPinIds,
            WavelengthNm = wavelengthNm,
        };
        var values = TransitiveSMatrixCalculator.Compute(scoped, context).GetNonNullValues();
        if (activeInputPinIds == null)
            return values;

        var sources = activeInputPinIds as ISet<Guid> ?? new HashSet<Guid>(activeInputPinIds);
        return values
            .Where(kv => sources.Contains(kv.Key.PinIdStart))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// IFFT each connection's frequency response to get h(t). Uses NoScaling
    /// (unnormalized IFFT) then divides by N so that IFFT(constant A)[0] = A
    /// (unit-delta identity convolution).
    /// </summary>
    private static IReadOnlyList<ImpulseResponse> InverseTransform(
        Dictionary<(Guid, Guid), Complex[]> hFreq, int nPoints, double dt)
    {
        var results = new List<ImpulseResponse>(hFreq.Count);
        double invN = 1.0 / nPoints;
        foreach (var (conn, hf) in hFreq)
        {
            var ht = (Complex[])hf.Clone();
            Fourier.Inverse(ht, FourierOptions.NoScaling);
            for (int i = 0; i < ht.Length; i++)
                ht[i] *= invN;
            results.Add(new ImpulseResponse(conn.Item1, conn.Item2, ht, dt));
        }
        return results;
    }

    /// <summary>
    /// Builds a uniformly-spaced frequency grid from <paramref name="centerWavelengthNm"/>
    /// ± <paramref name="spanNm"/>/2 and returns the grid plus the time step dt = 1/Δf.
    /// </summary>
    private static (double[] freqGrid, double dt) BuildFrequencyGrid(
        double centerWavelengthNm, double spanNm, int nPoints)
    {
        double fMin = SpeedOfLightNmPerS / (centerWavelengthNm + spanNm / 2.0);
        double fMax = SpeedOfLightNmPerS / (centerWavelengthNm - spanNm / 2.0);
        double bandwidth = fMax - fMin;
        double df = bandwidth / (nPoints - 1);
        double dt = 1.0 / bandwidth;

        var grid = new double[nPoints];
        for (int i = 0; i < nPoints; i++)
            grid[i] = fMin + i * df;

        return (grid, dt);
    }

    private static int FreqToWavelengthNmInt(double freqHz) =>
        (int)Math.Round(SpeedOfLightNmPerS / freqHz);

    /// <summary>Rejects a run whose per-pair arrays would exceed the memory gate.</summary>
    /// <param name="connectionPairs">Number of (input, output) pin pairs to allocate for.</param>
    /// <param name="nPoints">Number of frequency points.</param>
    private static void ThrowIfMemoryLimitExceeded(long connectionPairs, int nPoints)
    {
        long estimatedBytes = connectionPairs * nPoints * BytesPerPairPerPoint;
        long maxBytes = (long)MaxMemoryWarningMB * 1024 * 1024;
        if (estimatedBytes > maxBytes)
        {
            throw new InvalidOperationException(
                $"Estimated memory for time-domain simulation exceeds {MaxMemoryWarningMB} MB " +
                $"({connectionPairs} connection pairs × {nPoints} points). " +
                $"Reduce nPoints (currently {nPoints}) or reduce the wavelength span.");
        }
    }
}
