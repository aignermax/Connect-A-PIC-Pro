namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;

/// <summary>
/// An active optical source (e.g. laser diode) attached to a passive-network
/// input pin, together with its electrical drive waveform.
/// </summary>
/// <param name="Model">Compact model generating the optical field.</param>
/// <param name="ElectricalDrive">
/// Drive waveform (A or V, model-specific), one sample per timestep.
/// </param>
public sealed record ActiveSource(ICompactModel Model, double[] ElectricalDrive);

/// <summary>
/// Result of a mixed passive/active time-domain simulation.
/// Optical traces are intensities |E(t)|²; electrical traces are the
/// model-specific electrical outputs (photocurrent in A, emitted power in W, …)
/// and get their own plot axis in the UI.
/// </summary>
public class MixedSignalResult
{
    /// <summary>Shared time axis in seconds.</summary>
    public double[] TimeAxis { get; }

    /// <summary>Per-output-pin optical intensity traces |E(t)|².</summary>
    public Dictionary<Guid, double[]> OpticalTraces { get; }

    /// <summary>
    /// Per-pin electrical traces from active components
    /// (key = the pin the component is attached to).
    /// </summary>
    public Dictionary<Guid, double[]> ElectricalTraces { get; }

    /// <summary>Initializes a new instance of <see cref="MixedSignalResult"/>.</summary>
    /// <param name="timeAxis">Time axis shared by all traces (seconds).</param>
    /// <param name="opticalTraces">Per-pin optical intensity traces.</param>
    /// <param name="electricalTraces">Per-pin electrical traces.</param>
    public MixedSignalResult(
        double[] timeAxis,
        Dictionary<Guid, double[]> opticalTraces,
        Dictionary<Guid, double[]> electricalTraces)
    {
        TimeAxis = timeAxis ?? throw new ArgumentNullException(nameof(timeAxis));
        OpticalTraces = opticalTraces ?? throw new ArgumentNullException(nameof(opticalTraces));
        ElectricalTraces = electricalTraces ?? throw new ArgumentNullException(nameof(electricalTraces));
    }
}
