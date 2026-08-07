using System.Numerics;
using CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;

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
    /// <param name="phaseNoise">
    /// Optional laser phase-noise model (issue #834). When any source has a finite
    /// linewidth, each input is modulated by its Wiener phase factor e^{iφ(t)} and
    /// contributions per output pin are summed as complex FIELDS (coherent), so
    /// interferometric dephasing and multi-laser beat noise emerge naturally.
    /// Null (or all-zero linewidths) preserves today's behaviour exactly.
    /// </param>
    /// <returns>
    /// A <see cref="TimeDomainResult"/> with per-output-pin intensity traces.
    /// Only output pins that receive signal from at least one active input are included.
    /// </returns>
    public TimeDomainResult Run(
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef,
        double centerWavelengthNm = DefaultCenterWavelengthNm,
        double spanNm = DefaultSpanNm,
        int nFreqPoints = DefaultNPoints,
        PhaseNoiseSettings? phaseNoise = null)
    {
        if (inputSignals == null) throw new ArgumentNullException(nameof(inputSignals));
        if (timeDef == null) throw new ArgumentNullException(nameof(timeDef));

        // Build impulse responses (also validates: no nonlinear connections). The active
        // inputs restrict the multi-hop closure to the reachable subgraph — exactly the
        // (source → output) pairs convolved below.
        var impulseResponses = _irBuilder.Build(
            centerWavelengthNm, spanNm, nFreqPoints, inputSignals.Keys, _circuitContext);

        var outputTraces = phaseNoise?.HasAnyNoise == true
            ? ConvolveCoherent(impulseResponses, inputSignals, timeDef, phaseNoise)
            : ConvolveIncoherent(impulseResponses, inputSignals, timeDef);

        return new TimeDomainResult(timeDef.TimeAxis, outputTraces);
    }

    /// <summary>
    /// Legacy path (no phase noise): per output pin, sums the intensities of each
    /// input's convolved contribution.
    /// </summary>
    private static Dictionary<Guid, double[]> ConvolveIncoherent(
        IReadOnlyList<ImpulseResponse> impulseResponses,
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef)
    {
        var outputTraces = new Dictionary<Guid, double[]>();
        foreach (var outputPin in impulseResponses.Select(ir => ir.OutputPinId).Distinct())
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
        return outputTraces;
    }

    /// <summary>
    /// Phase-noise path (issue #834): inputs carry their Wiener phase factor and are
    /// summed per output pin as complex fields before taking |y(t)|², so the beat of
    /// independent lasers and delay-induced dephasing appear in the intensity trace.
    /// </summary>
    private static Dictionary<Guid, double[]> ConvolveCoherent(
        IReadOnlyList<ImpulseResponse> impulseResponses,
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef,
        PhaseNoiseSettings phaseNoise)
    {
        var noisyInputs = BuildNoisyInputs(inputSignals, timeDef, phaseNoise);
        var outputTraces = new Dictionary<Guid, double[]>();
        foreach (var outputPin in impulseResponses.Select(ir => ir.OutputPinId).Distinct())
        {
            Complex[]? combinedField = null;
            foreach (var ir in impulseResponses.Where(r => r.OutputPinId == outputPin))
            {
                if (!noisyInputs.TryGetValue(ir.InputPinId, out var inputField))
                    continue;

                var field = TimeDomainConvolver.Convolve(inputField, ir.Samples);
                combinedField = combinedField == null
                    ? TrimToLength(field, timeDef.NSamples)
                    : SumFields(combinedField, field, timeDef.NSamples);
            }
            if (combinedField != null)
                outputTraces[outputPin] = combinedField
                    .Select(c => c.Real * c.Real + c.Imaginary * c.Imaginary)
                    .ToArray();
        }
        return outputTraces;
    }

    /// <summary>Multiplies each real input envelope by its source's unit phase factor e^{iφ(t)}.</summary>
    private static Dictionary<Guid, Complex[]> BuildNoisyInputs(
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef,
        PhaseNoiseSettings phaseNoise)
    {
        var phaseFactors = phaseNoise.BuildPhaseFactors(timeDef.TimeStepSeconds, timeDef.NSamples);
        var noisyInputs = new Dictionary<Guid, Complex[]>();
        foreach (var (pinId, signal) in inputSignals)
        {
            var field = new Complex[signal.Length];
            var factors = phaseFactors.TryGetValue(pinId, out var f) ? f : null;
            for (int n = 0; n < signal.Length; n++)
                field[n] = factors != null && n < factors.Length
                    ? signal[n] * factors[n]
                    : new Complex(signal[n], 0);
            noisyInputs[pinId] = field;
        }
        return noisyInputs;
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

    /// <summary>Trims or zero-pads a complex field to exactly <paramref name="length"/> samples.</summary>
    private static Complex[] TrimToLength(Complex[] source, int length)
    {
        if (source.Length == length) return source;
        var result = new Complex[length];
        Array.Copy(source, result, Math.Min(length, source.Length));
        return result;
    }

    /// <summary>Adds field <paramref name="b"/> onto <paramref name="a"/>, trimmed to <paramref name="length"/>.</summary>
    private static Complex[] SumFields(Complex[] a, Complex[] b, int length)
    {
        for (int i = 0; i < length && i < b.Length; i++)
            a[i] += b[i];
        return a;
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
