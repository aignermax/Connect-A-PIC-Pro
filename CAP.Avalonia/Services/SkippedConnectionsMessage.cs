using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.Services;

/// <summary>
/// Formats the shared "N connections skipped" export warning from a list of already-collected
/// skip descriptions. What counts as skipped is decided exactly once, by the exporters
/// themselves while they write the script (<c>SimpleNazcaExporter.Export</c> /
/// <c>GdsFactoryExporter.Export</c>'s <c>skippedConnections</c> out-parameter) — this class only
/// turns that list into a localized message, so the report can never diverge from what actually
/// landed in the GDS.
/// </summary>
public static class SkippedConnectionsMessage
{
    /// <summary>Named individually before the message falls back to "… and N more".</summary>
    private const int MaxNamedConnections = 5;

    /// <summary>
    /// Builds the localized warning, or null when nothing was skipped. Caps the named
    /// connections so a design with many broken routes still produces a readable message.
    /// </summary>
    public static string? Build(IReadOnlyList<string> skippedConnections)
    {
        if (skippedConnections.Count == 0)
            return null;

        var shown = skippedConnections.Take(MaxNamedConnections);
        var remaining = skippedConnections.Count - MaxNamedConnections;
        var names = remaining > 0
            ? string.Join("; ", shown) + "; " + string.Format(
                LocalizationService.Instance.Translate("Export.SkippedConnections.AndMore"), remaining)
            : string.Join("; ", shown);

        return string.Format(
            LocalizationService.Instance.Translate("Export.SkippedConnections.Warning"),
            skippedConnections.Count, names);
    }
}
