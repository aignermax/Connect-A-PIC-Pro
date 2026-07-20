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
    /// <returns>One <see cref="ImpulseResponse"/> per non-zero (input, output) pin pair.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any nonlinear connection is found (Phase 1 gate).
    /// </exception>
    public IReadOnlyList<ImpulseResponse> Build(
        double centerWavelengthNm, double spanNm, int nPoints)
    {
        if (nPoints < 2) throw new ArgumentOutOfRangeException(nameof(nPoints), "Need at least 2 frequency points.");
        if (spanNm <= 0) throw new ArgumentOutOfRangeException(nameof(spanNm));

        ThrowIfMemoryLimitExceeded(nPoints);

        var (freqGrid, dt) = BuildFrequencyGrid(centerWavelengthNm, spanNm, nPoints);

        // Check for nonlinear connections — Phase 1 only supports linear circuits.
        var referenceMatrix = _matrixBuilder.GetSystemSMatrix(
            FreqToWavelengthNmInt(freqGrid[nPoints / 2]));
        if (referenceMatrix.NonLinearConnections.Count > 0)
        {
            throw new InvalidOperationException(
                "Time-domain simulation (Phase 1) supports linear circuits only. " +
                "The design contains nonlinear connections. Remove or linearize them before running transient analysis.");
        }

        // Discover all connections from the first matrix evaluation
        var initialConnections = referenceMatrix.GetNonNullValues();
        var hFreq = new Dictionary<(Guid, Guid), Complex[]>(initialConnections.Count);
        foreach (var conn in initialConnections.Keys)
            hFreq[conn] = new Complex[nPoints];

        // Cache S-matrix results by rounded wavelength nm to avoid duplicate calls
        var matrixCache = new Dictionary<int, Dictionary<(Guid, Guid), Complex>>();

        // Fill H[k] for each frequency point
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
                var transitive = TransitiveSMatrixCalculator.Compute(
                    sMatrix, sMatrix.PinReference.Count);
                values = transitive.GetNonNullValues();
                matrixCache[wavelengthNm] = values;
            }

            foreach (var (conn, val) in values)
            {
                if (!hFreq.TryGetValue(conn, out var arr))
                {
                    arr = new Complex[nPoints];
                    hFreq[conn] = arr;
                }
                arr[k] = val;
            }
        }

        // IFFT each connection's frequency response to get h(t).
        // Use NoScaling (unnormalized IFFT) then divide by N so that
        // IFFT(constant A)[0] = A  (unit-delta identity convolution).
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

    private static void ThrowIfMemoryLimitExceeded(int nPoints)
    {
        // Rough estimate: 200 connections × N points × 16 bytes/complex × 2 (hFreq + ht)
        const int EstimatedConnections = 200;
        long estimatedBytes = (long)EstimatedConnections * nPoints * 16 * 2;
        long maxBytes = (long)MaxMemoryWarningMB * 1024 * 1024;
        if (estimatedBytes > maxBytes)
        {
            throw new InvalidOperationException(
                $"Estimated memory for time-domain simulation exceeds {MaxMemoryWarningMB} MB. " +
                $"Reduce nPoints (currently {nPoints}) or reduce the wavelength span.");
        }
    }
}
