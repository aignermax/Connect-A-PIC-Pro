namespace CAP_Core.LightCalculation.TimeDomainSimulation.Sources;

/// <summary>
/// Gaussian pulse source. Wraps the existing
/// <see cref="TimeSignalDefinition.CreateGaussianPulse"/> factory so the
/// pre-#600 single-pulse behaviour stays available through the
/// <see cref="ISignalSource"/> abstraction (back-compat).
/// </summary>
public class PulseSource : ISignalSource
{
    private readonly double _centerSeconds;
    private readonly double _sigmaSeconds;
    private readonly double _amplitude;

    /// <summary>Initializes a new Gaussian pulse source.</summary>
    /// <param name="centerSeconds">Pulse centre in seconds.</param>
    /// <param name="sigmaSeconds">1-σ width in seconds (must be positive).</param>
    /// <param name="amplitude">Peak envelope amplitude (√W units).</param>
    public PulseSource(double centerSeconds, double sigmaSeconds, double amplitude = 1.0)
    {
        if (sigmaSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(sigmaSeconds));
        _centerSeconds = centerSeconds;
        _sigmaSeconds = sigmaSeconds;
        _amplitude = amplitude;
    }

    /// <inheritdoc/>
    public SignalDomain Domain => SignalDomain.Optical;

    /// <inheritdoc/>
    public double[] Generate(TimeSignalDefinition grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return grid.CreateGaussianPulse(_centerSeconds, _sigmaSeconds, _amplitude);
    }
}
