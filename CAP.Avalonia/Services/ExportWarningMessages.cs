using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.Services;

/// <summary>
/// Formats the export flow's post-write warnings from lists of already-collected
/// descriptions. What counts as skipped or unresolved is decided exactly once, by the
/// exporters themselves while they write the script (<c>SimpleNazcaExporter.Export</c> /
/// <c>GdsFactoryExporter.Export</c>'s <c>skippedConnections</c>/<c>unresolvedCrossings</c>
/// out-parameters) — this class only turns those lists into localized messages, so a report
/// can never diverge from what actually landed in the GDS.
/// </summary>
public static class ExportWarningMessages
{
    /// <summary>Named individually before a message falls back to "… and N more".</summary>
    private const int MaxNamedConnections = 5;

    /// <summary>
    /// Builds the "N connections skipped" warning for connections/frozen paths left out of
    /// the geometry (placeholder or invalid route), or null when nothing was skipped.
    /// </summary>
    public static string? BuildSkipped(IReadOnlyList<string> skippedConnections) =>
        Build(skippedConnections, "Export.SkippedConnections.Warning");

    /// <summary>
    /// Builds the "N connections with unresolved crossings were exported" warning for
    /// connections that render but whose sibling-crossing flag no bridge marker resolves,
    /// or null when there are none.
    /// </summary>
    public static string? BuildUnresolvedCrossings(IReadOnlyList<string> unresolvedCrossings) =>
        Build(unresolvedCrossings, "Export.UnresolvedCrossings.Warning");

    /// <summary>
    /// Caps the named connections so a design with many flagged routes still produces a
    /// readable message, then formats it under the given localization key.
    /// </summary>
    private static string? Build(IReadOnlyList<string> connections, string localizationKey)
    {
        if (connections.Count == 0)
            return null;

        var shown = connections.Take(MaxNamedConnections);
        var remaining = connections.Count - MaxNamedConnections;
        var names = remaining > 0
            ? string.Join("; ", shown) + "; " + string.Format(
                LocalizationService.Instance.Translate("Export.SkippedConnections.AndMore"), remaining)
            : string.Join("; ", shown);

        return string.Format(LocalizationService.Instance.Translate(localizationKey), connections.Count, names);
    }
}
