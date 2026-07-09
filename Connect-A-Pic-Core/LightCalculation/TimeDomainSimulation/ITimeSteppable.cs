using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Per-sample seam for stateful/active elements in the stepped transient
/// driver (design #600, decision D3). Passive links stay FIR filters;
/// anything whose behaviour depends on its own state implements this and is
/// scheduled into the same sample loop by <see cref="SampleModeTransientDriver"/>.
///
/// #600 ships passive-only: this interface defines the coupling point for
/// the active compact models of issue #529 (see
/// <see cref="CompactModels.ICompactModel"/>, which can be adapted onto this
/// seam) without implementing any active physics itself. The signature is
/// delay-friendly on purpose so #529 can settle feedback/cycle handling
/// (one-sample decoupling or iterative solve) without changing this seam.
/// </summary>
public interface ITimeSteppable
{
    /// <summary>
    /// Advances the element by one timestep: given the complex field samples
    /// arriving this step (one per coupled port) and internal state, produces
    /// the outgoing field samples for this step.
    /// </summary>
    /// <param name="inputs">Incident field samples for this step.</param>
    /// <param name="outputs">Outgoing field samples for this step (same length).</param>
    /// <param name="dt">Timestep in seconds.</param>
    void Step(ReadOnlySpan<Complex> inputs, Span<Complex> outputs, double dt);
}
