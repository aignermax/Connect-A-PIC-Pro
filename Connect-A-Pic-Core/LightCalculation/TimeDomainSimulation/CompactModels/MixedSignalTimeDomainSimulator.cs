using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;

/// <summary>
/// Time-domain simulation of mixed passive/active photonic networks.
///
/// Phase 1 coupling scheme (feed-forward, unconditionally stable):
/// (1) active <b>source</b> models (laser diodes) are stepped over their
/// electrical drive to produce the optical input envelopes, (2) the passive
/// network response is obtained by convolving those envelopes with the
/// impulse responses from <see cref="ImpulseResponseBuilder"/> (issue #527),
/// (3) active <b>sink</b> models (photodiodes) are stepped over the resulting
/// complex fields to produce electrical traces. Feedback from a sink back
/// into the network (closed-loop) is out of scope — see issue #529 Phase 3.
///
/// Sources emit a real envelope (the source phase is not yet propagated);
/// electrically driven in-line components (modulators) need electrical pins
/// (issue #519) to be routed inside a design and are currently supported via
/// <see cref="ActiveComponentStepper"/> for standalone/chained analysis.
/// </summary>
public class MixedSignalTimeDomainSimulator
{
    private readonly ImpulseResponseBuilder _irBuilder;

    /// <summary>Initializes a new instance of <see cref="MixedSignalTimeDomainSimulator"/>.</summary>
    /// <param name="matrixBuilder">System S-matrix builder for the passive network.</param>
    public MixedSignalTimeDomainSimulator(ISystemMatrixBuilder matrixBuilder)
    {
        if (matrixBuilder == null) throw new ArgumentNullException(nameof(matrixBuilder));
        _irBuilder = new ImpulseResponseBuilder(matrixBuilder);
    }

    /// <summary>
    /// Runs a mixed passive/active time-domain simulation.
    /// </summary>
    /// <param name="opticalInputSignals">
    /// Passive optical input envelopes per inflow pin (may be empty).
    /// </param>
    /// <param name="sourceModels">
    /// Active sources per inflow pin: the model's outgoing field magnitude is
    /// injected into the passive network at that pin (may be empty).
    /// </param>
    /// <param name="sinkModels">
    /// Active sinks per outflow pin: the passive field arriving at that pin is
    /// fed into the model; its electrical output is recorded (may be empty).
    /// </param>
    /// <param name="timeDef">Sample rate and duration of the run.</param>
    /// <param name="centerWavelengthNm">Centre wavelength for the IFFT sweep (nm).</param>
    /// <param name="spanNm">Wavelength span for the IFFT sweep (nm).</param>
    /// <param name="nFreqPoints">Number of frequency sweep points.</param>
    public MixedSignalResult Run(
        Dictionary<Guid, double[]> opticalInputSignals,
        Dictionary<Guid, ActiveSource> sourceModels,
        Dictionary<Guid, ICompactModel> sinkModels,
        TimeSignalDefinition timeDef,
        double centerWavelengthNm = TimeDomainSimulator.DefaultCenterWavelengthNm,
        double spanNm = TimeDomainSimulator.DefaultSpanNm,
        int nFreqPoints = TimeDomainSimulator.DefaultNPoints)
    {
        if (opticalInputSignals == null) throw new ArgumentNullException(nameof(opticalInputSignals));
        if (sourceModels == null) throw new ArgumentNullException(nameof(sourceModels));
        if (sinkModels == null) throw new ArgumentNullException(nameof(sinkModels));
        if (timeDef == null) throw new ArgumentNullException(nameof(timeDef));

        double dt = timeDef.TimeStepSeconds;
        var electricalTraces = new Dictionary<Guid, double[]>();

        // (1) Step source models → optical input envelopes at their pins.
        var inputSignals = new Dictionary<Guid, double[]>(opticalInputSignals);
        foreach (var (pinId, source) in sourceModels)
        {
            var trace = ActiveComponentStepper.StepOverTrace(
                source.Model, dt, timeDef.NSamples, electricalInput: source.ElectricalDrive);
            inputSignals[pinId] = trace.OutgoingField.Select(f => f.Magnitude).ToArray();
            electricalTraces[pinId] = trace.ElectricalOutput;
        }

        // (2) Passive network: convolve, summing complex fields per output pin.
        var fieldsByOutputPin = ConvolvePassiveNetwork(
            inputSignals, timeDef, centerWavelengthNm, spanNm, nFreqPoints);

        // (3) Step sink models over the arriving fields; passive pins keep |E|².
        var opticalTraces = new Dictionary<Guid, double[]>();
        foreach (var (pinId, field) in fieldsByOutputPin)
        {
            if (sinkModels.TryGetValue(pinId, out var sink))
            {
                var trace = ActiveComponentStepper.StepOverTrace(
                    sink, dt, timeDef.NSamples, incidentField: field);
                electricalTraces[pinId] = trace.ElectricalOutput;
                opticalTraces[pinId] = ToIntensity(trace.OutgoingField);
            }
            else
            {
                opticalTraces[pinId] = ToIntensity(field);
            }
        }

        return new MixedSignalResult(timeDef.TimeAxis, opticalTraces, electricalTraces);
    }

    /// <summary>
    /// Convolves every input envelope with the passive impulse responses and
    /// returns the complex field per output pin (contributions summed as fields).
    /// </summary>
    private Dictionary<Guid, Complex[]> ConvolvePassiveNetwork(
        Dictionary<Guid, double[]> inputSignals,
        TimeSignalDefinition timeDef,
        double centerWavelengthNm,
        double spanNm,
        int nFreqPoints)
    {
        var impulseResponses = _irBuilder.Build(
            centerWavelengthNm, spanNm, nFreqPoints, inputSignals.Keys);
        var fields = new Dictionary<Guid, Complex[]>();

        foreach (var ir in impulseResponses)
        {
            if (!inputSignals.TryGetValue(ir.InputPinId, out var signal))
                continue;

            var contribution = TimeDomainConvolver.Convolve(signal, ir.Samples);
            if (!fields.TryGetValue(ir.OutputPinId, out var sum))
            {
                fields[ir.OutputPinId] = TrimToLength(contribution, timeDef.NSamples);
                continue;
            }
            for (int i = 0; i < sum.Length; i++)
                sum[i] += i < contribution.Length ? contribution[i] : Complex.Zero;
        }

        return fields;
    }

    private static double[] ToIntensity(Complex[] field)
        => field.Select(c => c.Real * c.Real + c.Imaginary * c.Imaginary).ToArray();

    private static Complex[] TrimToLength(Complex[] source, int length)
    {
        if (source.Length == length) return source;
        var result = new Complex[length];
        Array.Copy(source, result, Math.Min(length, source.Length));
        return result;
    }
}
