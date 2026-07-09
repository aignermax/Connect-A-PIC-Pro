namespace CAP_Core.LightCalculation.TimeDomainSimulation.Sources;

/// <summary>
/// Physical domain a signal source emits into. Optical sources drive input
/// pins of the passive network; electrical sources are reserved for the
/// electrical-pin plumbing of issue #519 (e.g. a modulator drive voltage).
/// </summary>
public enum SignalDomain
{
    /// <summary>Optical complex-envelope amplitude (√W units).</summary>
    Optical,

    /// <summary>Electrical drive signal (model-specific units, issue #519).</summary>
    Electrical,
}

/// <summary>
/// A time-domain signal source (issue #600). Implementations produce the
/// real envelope samples of the drive waveform on a given time grid; the
/// grid itself is derived from the most demanding source via
/// <see cref="Sampling.SamplingPolicy"/>.
/// </summary>
public interface ISignalSource
{
    /// <summary>Domain this source emits into.</summary>
    SignalDomain Domain { get; }

    /// <summary>
    /// Produces the envelope amplitude samples of this source on the given grid.
    /// </summary>
    /// <param name="grid">Time grid (sample rate and duration) to generate on.</param>
    /// <returns>One amplitude sample per grid point (length = grid.NSamples).</returns>
    double[] Generate(TimeSignalDefinition grid);
}
