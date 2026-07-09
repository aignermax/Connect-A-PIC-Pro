namespace CAP_Core.LightCalculation.TimeDomainSimulation.Sources;

/// <summary>
/// Continuous-wave source: a constant envelope amplitude over the whole run.
/// </summary>
public class CwSource : ISignalSource
{
    private readonly double _amplitude;

    /// <summary>Initializes a new CW source.</summary>
    /// <param name="amplitude">Constant envelope amplitude (√W units).</param>
    public CwSource(double amplitude)
    {
        if (amplitude < 0) throw new ArgumentOutOfRangeException(nameof(amplitude));
        _amplitude = amplitude;
    }

    /// <inheritdoc/>
    public SignalDomain Domain => SignalDomain.Optical;

    /// <inheritdoc/>
    public double[] Generate(TimeSignalDefinition grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var samples = new double[grid.NSamples];
        Array.Fill(samples, _amplitude);
        return samples;
    }
}
