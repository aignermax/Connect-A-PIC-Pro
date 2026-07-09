using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Sample-mode transient driver (issue #600): advances the passive network
/// sample-by-sample as stateful FIR filters over the #527 impulse responses,
/// exposing a per-step <see cref="ITimeSteppable"/> seam for active elements.
///
/// For purely passive networks this driver reproduces
/// <see cref="TimeDomainSimulator.Run"/> within numerical tolerance (the
/// keystone regression guard of design #600, §5.1). One deliberate
/// difference: contributions from several active inputs to the same output
/// pin are summed as complex fields (coherent) rather than as intensities;
/// with a single active input — the guarded case — both are identical.
/// </summary>
public class SampleModeTransientDriver
{
    private readonly ImpulseResponseBuilder _irBuilder;

    /// <summary>Initializes a new instance of <see cref="SampleModeTransientDriver"/>.</summary>
    /// <param name="matrixBuilder">System S-matrix builder for the passive network.</param>
    public SampleModeTransientDriver(ISystemMatrixBuilder matrixBuilder)
    {
        if (matrixBuilder == null) throw new ArgumentNullException(nameof(matrixBuilder));
        _irBuilder = new ImpulseResponseBuilder(matrixBuilder);
    }

    /// <summary>
    /// Runs the stepped time-domain simulation.
    /// </summary>
    /// <param name="inputSignals">Input envelope per active inflow pin.</param>
    /// <param name="timeDef">
    /// Time grid; derive it from the data signal via
    /// <see cref="Sampling.SamplingPolicy.CreateGrid"/> (with a guard tail ≥ the
    /// impulse-response length so the convolution tail is not truncated).
    /// </param>
    /// <param name="outputSteppables">
    /// Optional active elements per output pin, stepped once per sample on the
    /// field arriving at that pin (the #529 seam). Null/empty = purely passive.
    /// </param>
    /// <param name="centerWavelengthNm">Centre wavelength for the IFFT sweep (nm).</param>
    /// <param name="spanNm">Wavelength span for the IFFT sweep (nm).</param>
    /// <param name="nFreqPoints">Number of frequency sweep points (= IR length).</param>
    /// <returns>Per-output-pin intensity traces on the grid's time axis.</returns>
    public TimeDomainResult Run(
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef,
        IReadOnlyDictionary<Guid, ITimeSteppable>? outputSteppables = null,
        double centerWavelengthNm = TimeDomainSimulator.DefaultCenterWavelengthNm,
        double spanNm = TimeDomainSimulator.DefaultSpanNm,
        int nFreqPoints = TimeDomainSimulator.DefaultNPoints)
    {
        if (inputSignals == null) throw new ArgumentNullException(nameof(inputSignals));
        if (timeDef == null) throw new ArgumentNullException(nameof(timeDef));

        var activeLinks = _irBuilder.Build(centerWavelengthNm, spanNm, nFreqPoints)
            .Where(ir => inputSignals.ContainsKey(ir.InputPinId))
            .ToList();

        var fields = StepNetwork(activeLinks, inputSignals, timeDef, outputSteppables);

        var traces = fields.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(c => c.Real * c.Real + c.Imaginary * c.Imaginary).ToArray());
        return new TimeDomainResult(timeDef.TimeAxis, traces);
    }

    /// <summary>
    /// The sample loop: per step, every passive link advances its FIR state
    /// (direct-form convolution over the input history), contributions are
    /// summed per output pin, then any registered steppable transforms the
    /// field sample at its pin.
    /// </summary>
    private static Dictionary<Guid, Complex[]> StepNetwork(
        IReadOnlyList<ImpulseResponse> links,
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef,
        IReadOnlyDictionary<Guid, ITimeSteppable>? outputSteppables)
    {
        int nSamples = timeDef.NSamples;
        double dt = timeDef.TimeStepSeconds;
        var fields = links
            .Select(l => l.OutputPinId).Distinct()
            .ToDictionary(pin => pin, _ => new Complex[nSamples]);

        Span<Complex> stepIo = stackalloc Complex[1];
        for (int n = 0; n < nSamples; n++)
        {
            foreach (var link in links)
                fields[link.OutputPinId][n] += FirSample(link, inputSignals[link.InputPinId], n);

            if (outputSteppables == null) continue;
            foreach (var (pin, steppable) in outputSteppables)
            {
                if (!fields.TryGetValue(pin, out var field)) continue;
                stepIo[0] = field[n];
                steppable.Step(stepIo, stepIo, dt);
                field[n] = stepIo[0];
            }
        }
        return fields;
    }

    /// <summary>One FIR output sample: y[n] = Σₖ h[k] · x[n−k].</summary>
    private static Complex FirSample(ImpulseResponse link, double[] input, int n)
    {
        var taps = link.Samples;
        int kMax = Math.Min(n, taps.Length - 1);
        Complex acc = Complex.Zero;
        for (int k = 0; k <= kMax; k++)
            acc += taps[k] * input[n - k];
        return acc;
    }
}
