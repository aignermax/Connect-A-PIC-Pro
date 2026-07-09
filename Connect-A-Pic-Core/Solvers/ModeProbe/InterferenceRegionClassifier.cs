namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// Decides whether a probed component is a multimode/interference region (MMI,
/// star coupler, …) where a single-mode FDE slice is physically meaningless —
/// the probe must show an "interference region — use FDTD" notice instead of a
/// misleading single mode.
/// </summary>
public static class InterferenceRegionClassifier
{
    private static readonly string[] InterferenceKeywords =
    {
        "MMI",
        "multimode",
        "multi-mode",
        "interference",
        "star coupler",
    };

    /// <summary>
    /// Returns true when the component name denotes a multimode/interference
    /// component (e.g. "MMI 1x2", "Multimode Interference Coupler", "Star Coupler").
    /// Matching is case-insensitive.
    /// </summary>
    /// <param name="componentName">Component or template name, may be null.</param>
    public static bool IsInterferenceRegion(string? componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName)) return false;
        return InterferenceKeywords.Any(k =>
            componentName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
