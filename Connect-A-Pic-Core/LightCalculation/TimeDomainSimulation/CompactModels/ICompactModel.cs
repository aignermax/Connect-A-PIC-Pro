using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;

/// <summary>
/// Result of one compact-model timestep: the outgoing optical field and the
/// electrical output sample (e.g. photocurrent in A, phase in rad, power in W —
/// meaning is model-specific, see each model's documentation).
/// </summary>
/// <param name="OutgoingField">Complex optical field leaving the component.</param>
/// <param name="ElectricalOutput">Electrical output sample for trace plotting.</param>
public readonly record struct CompactModelStepResult(Complex OutgoingField, double ElectricalOutput);

/// <summary>
/// Compact model of an active photonic component whose behaviour cannot be
/// captured by a static S-matrix (laser diode, photodiode, modulator, …).
/// The transient simulator calls <see cref="Step"/> once per timestep with the
/// incident optical field; the model integrates its internal ODEs over dt and
/// returns the outgoing field plus its electrical output.
/// </summary>
/// <remarks>
/// Stepping is synchronous by design: it is invoked once per sample in a tight
/// inner loop (10⁴–10⁶ calls per run), where per-call Task allocation would
/// dominate the cost. State is mutated in place on the supplied
/// <see cref="CompactModelState"/>; use <see cref="CompactModelState.Clone"/>
/// if a caller needs to preserve history.
/// </remarks>
public interface ICompactModel
{
    /// <summary>Registry name of this model (e.g. "PhotodiodeRc").</summary>
    string Name { get; }

    /// <summary>
    /// Creates the initial integrator state for one component instance
    /// (all state variables at their t = 0 values).
    /// </summary>
    CompactModelState CreateInitialState();

    /// <summary>
    /// Advances the model by one timestep.
    /// </summary>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="incidentField">Incident complex optical field (√W units).</param>
    /// <param name="state">
    /// Per-instance state from the previous step; updated in place.
    /// </param>
    /// <param name="electricalInput">
    /// Electrical drive sample (model-specific: drive current in A for a laser,
    /// drive voltage in V for a modulator, unused for a photodiode).
    /// </param>
    /// <returns>Outgoing field and electrical output for this timestep.</returns>
    CompactModelStepResult Step(
        double dt, Complex incidentField, CompactModelState state, double electricalInput);
}
