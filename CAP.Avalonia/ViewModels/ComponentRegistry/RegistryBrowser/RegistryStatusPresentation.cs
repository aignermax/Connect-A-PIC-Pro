namespace CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

/// <summary>
/// Maps registry trust statuses (<c>demo</c>, <c>unverified</c>, <c>verified</c>,
/// <c>disputed</c>, <c>withdrawn</c>) to the chip colors used by the registry
/// browser panel. Shared by the component list and the artifact detail rows so
/// the same status always renders in the same color.
/// </summary>
public static class RegistryStatusPresentation
{
    private const string DemoColor = "#8a6d3b";
    private const string VerifiedColor = "#3d6d3d";
    private const string UnverifiedColor = "#555555";
    private const string DisputedColor = "#8a3d3d";
    private const string WithdrawnColor = "#5d3d5d";

    /// <summary>Chip text shown when an artifact tier is available.</summary>
    public const string TierAvailableMark = "\u2713";

    /// <summary>Chip text shown when an artifact tier is missing.</summary>
    public const string TierMissingMark = "\u2717";

    /// <summary>Returns the chip background color (hex) for a registry status.</summary>
    public static string ToColor(string status) => status.ToLowerInvariant() switch
    {
        "demo" => DemoColor,
        "verified" => VerifiedColor,
        "disputed" => DisputedColor,
        "withdrawn" => WithdrawnColor,
        _ => UnverifiedColor,
    };

    /// <summary>
    /// Builds the tier badge line, e.g. <c>geometry ✗ · simulated ✓ · measured ✗</c>.
    /// </summary>
    public static string BuildTierText(bool geometry, bool simulated, bool measured) =>
        $"geometry {Mark(geometry)} \u00b7 simulated {Mark(simulated)} \u00b7 measured {Mark(measured)}";

    private static string Mark(bool available) =>
        available ? TierAvailableMark : TierMissingMark;
}
