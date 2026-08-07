using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Orchestrates circuit-level time-domain simulation via IFFT of S-parameters.
/// Phase 1: linear circuits only. Parameter-only (slider-driven) formula connections are
/// evaluated once into the linear matrix; only field-dependent (truly nonlinear)
/// connections cause an exception.
/// Implements <see cref="ILightCalculator"/> for polymorphic registration alongside
/// <see cref="GridLightCalculator"/>; steady-state field propagation is not applicable
/// for time-domain mode — use <see cref="Run"/> instead.
/// </summary>
public class TimeDomainSimulator : ILightCalculator
{
    /// <summary>Default centre wavelength in nm.</summary>
    public const double DefaultCenterWavelengthNm = 1550;

    /// <summary>Default wavelength span in nm.</summary>
    public const double DefaultSpanNm = 100;

    /// <summary>Default number of frequency/time points.</summary>
    public const int DefaultNPoints = 256;

    private readonly ImpulseResponseBuilder _irBuilder;
    private readonly TransitiveClosureContext? _circuitContext;

    /// <summary>Initializes a new instance of <see cref="TimeDomainSimulator"/>.</summary>
    /// <param name="matrixBuilder">System S-matrix builder.</param>
    /// <param name="circuitContext">
    /// Optional circuit knowledge (pin owner names, externally observable pins) so
    /// closure failures name the culprit component/loop and the energy guard is scoped
    /// to real circuit ports (field round 4, final batch).
    /// </param>
    public TimeDomainSimulator(
        ISystemMatrixBuilder matrixBuilder, TransitiveClosureContext? circuitContext = null)
    {
        if (matrixBuilder == null) throw new ArgumentNullException(nameof(matrixBuilder));
        _irBuilder = new ImpulseResponseBuilder(matrixBuilder);
        _circuitContext = circuitContext;
    }

    /// <summary>
    /// Runs a time-domain simulation.
    /// </summary>
    /// <param name="inputSignals">
    /// Dictionary mapping each active inflow pin Guid to its real-valued time signal.
    /// Signals must have the same length as <paramref name="timeDef"/>.NSamples.
    /// </param>
    /// <param name="timeDef">
    /// Defines sample rate and duration (use <see cref="TimeSignalDefinition.FromWavelengthSweep"/>
    /// to derive these from the same wavelength parameters passed below).
    /// </param>
    /// <param name="centerWavelengthNm">Centre wavelength for the IFFT sweep (nm).</param>
    /// <param name="spanNm">Wavelength span for the IFFT sweep (nm).</param>
    /// <param name="nFreqPoints">Number of frequency sweep points.</param>
    /// <returns>
    /// A <see cref="TimeDomainResult"/> with per-output-pin intensity traces.
    /// Only output pins that receive signal from at least one active input are included.
    /// </returns>
    public TimeDomainResult Run(
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef,
        double centerWavelengthNm = DefaultCenterWavelengthNm,
        double spanNm = DefaultSpanNm,
        int nFreqPoints = DefaultNPoints)
    {
        if (inputSignals == null) throw new ArgumentNullException(nameof(inputSignals));
        if (timeDef == null) throw new ArgumentNullException(nameof(timeDef));

        // Build impulse responses (also validates: no nonlinear connections). The active
        // inputs restrict the multi-hop closure to the reachable subgraph — exactly the
        // (source → output) pairs convolved below.
        var impulseResponses = _irBuilder.Build(
            centerWavelengthNm, spanNm, nFreqPoints, inputSignals.Keys, _circuitContext);

        var outputPinIds = impulseResponses
            .Select(ir => ir.OutputPinId)
            .Distinct()
            .ToList();

        var outputTraces = new Dictionary<Guid, double[]>();

        foreach (var outputPin in outputPinIds)
        {
            double[]? combinedIntensity = null;

            foreach (var ir in impulseResponses.Where(r => r.OutputPinId == outputPin))
            {
                if (!inputSignals.TryGetValue(ir.InputPinId, out var inputSignal))
                    continue;

                // Convolve input signal with impulse response → intensity |y(t)|² = Re²+Im²
                var intensity = TimeDomainConvolver.ConvolveToIntensity(inputSignal, ir.Samples);
                var trimmed = TrimToLength(intensity, timeDef.NSamples);

                combinedIntensity = combinedIntensity == null
                    ? trimmed
                    : SumArrays(combinedIntensity, trimmed);
            }

            if (combinedIntensity != null)
                outputTraces[outputPin] = combinedIntensity;
        }

        return new TimeDomainResult(timeDef.TimeAxis, outputTraces);
    }

    /// <summary>
    /// Not applicable for time-domain simulation. Returns an empty dictionary so that
    /// <see cref="TimeDomainSimulator"/> can be registered as <see cref="ILightCalculator"/>
    /// alongside <see cref="GridLightCalculator"/>. Use <see cref="Run"/> for transient analysis.
    /// </summary>
    public Task<Dictionary<Guid, Complex>> CalculateFieldPropagationAsync(
        CancellationTokenSource cancelToken, int LaserWaveLengthInNm)
        => Task.FromResult(new Dictionary<Guid, Complex>());

    /// <summary>Trims or zero-pads <paramref name="source"/> to exactly <paramref name="length"/> samples.</summary>
    private static double[] TrimToLength(double[] source, int length)
    {
        if (source.Length == length) return source;
        var result = new double[length];
        Array.Copy(source, result, Math.Min(length, source.Length));
        return result;
    }

    private static double[] SumArrays(double[] a, double[] b)
    {
        int len = Math.Max(a.Length, b.Length);
        var result = new double[len];
        for (int i = 0; i < len; i++)
            result[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        return result;
    }
}
