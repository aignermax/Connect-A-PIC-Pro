using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;

/// <summary>
/// Maps PDK <c>"compactModel"</c> names to <see cref="ICompactModel"/> factories.
/// A PDK component JSON declares e.g. <c>"compactModel": "LaserDiodeRateEquation"</c>
/// plus model-specific parameters; the loader resolves the string here.
/// Unknown names throw — a PDK must never silently fall back to passive behaviour.
/// </summary>
public static class CompactModelRegistry
{
    private static readonly Dictionary<string, Func<IReadOnlyDictionary<string, double>?, ICompactModel>>
        Factories = new(StringComparer.Ordinal)
        {
            [LaserDiodeRateEquationModel.ModelName] = p => new LaserDiodeRateEquationModel(p),
            [PhotodiodeRcModel.ModelName] = p => new PhotodiodeRcModel(p),
            [ElectroOpticPhaseModulatorModel.ModelName] = p => new ElectroOpticPhaseModulatorModel(p),
        };

    /// <summary>All registered compact-model names.</summary>
    public static IReadOnlyCollection<string> RegisteredNames => Factories.Keys;

    /// <summary>Returns true if <paramref name="modelName"/> is a known compact model.</summary>
    /// <param name="modelName">PDK compact-model name.</param>
    public static bool IsRegistered(string modelName) => Factories.ContainsKey(modelName);

    /// <summary>
    /// Creates an <see cref="ICompactModel"/> instance for <paramref name="modelName"/>.
    /// </summary>
    /// <param name="modelName">PDK compact-model name (case-sensitive).</param>
    /// <param name="parameters">Optional model-specific parameters from the PDK JSON.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown for unknown model names — no silent fallback to passive.
    /// </exception>
    public static ICompactModel Create(
        string modelName, IReadOnlyDictionary<string, double>? parameters = null)
    {
        if (!Factories.TryGetValue(modelName, out var factory))
        {
            throw new InvalidOperationException(
                $"Unknown compact model '{modelName}'. " +
                $"Known models: {string.Join(", ", Factories.Keys)}.");
        }
        return factory(parameters);
    }
}
