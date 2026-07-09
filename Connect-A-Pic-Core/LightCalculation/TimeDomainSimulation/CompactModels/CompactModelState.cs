namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;

/// <summary>
/// Opaque per-instance state container for an <see cref="ICompactModel"/>.
/// Stores named scalar state variables (e.g. carrier density, photocurrent)
/// that persist across timesteps within one simulation run. Each active
/// component instance in a design owns its own state object.
/// </summary>
public class CompactModelState
{
    private readonly Dictionary<string, double> _values = new();

    /// <summary>
    /// Returns the value stored under <paramref name="key"/>, or
    /// <paramref name="defaultValue"/> if the key has never been set.
    /// </summary>
    /// <param name="key">Name of the state variable.</param>
    /// <param name="defaultValue">Value returned when the key is absent.</param>
    public double Get(string key, double defaultValue = 0.0)
        => _values.TryGetValue(key, out var value) ? value : defaultValue;

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>.</summary>
    /// <param name="key">Name of the state variable.</param>
    /// <param name="value">New value.</param>
    public void Set(string key, double value) => _values[key] = value;

    /// <summary>Creates an independent copy of this state.</summary>
    public CompactModelState Clone()
    {
        var clone = new CompactModelState();
        foreach (var (key, value) in _values)
            clone._values[key] = value;
        return clone;
    }
}
