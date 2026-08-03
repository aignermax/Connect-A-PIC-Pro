using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Builds the <see cref="GdsHierarchyImportOptions.ResolveKnownComponent"/>
/// callback from the currently loaded component library: a GDS cell whose name
/// matches a <see cref="ComponentTemplate"/> resolves to that template, so the
/// hierarchy importer places the existing PDK component instead of importing a
/// duplicate draft. Gdsfactory hash-suffix stripping itself is the importer's
/// job (<c>GdsHierarchyImportSession.ResolveKnown</c>) — this resolver only
/// answers exact-name lookups.
/// </summary>
public static class GdsTemplateResolver
{
    /// <summary>
    /// Returns a resolver mapping exact GDS cell names to loaded templates.
    /// Template pins are converted to the app-space <see cref="DetectedPin"/>
    /// shape the importer consumes (offsets are already µm, Y-down, relative to
    /// the component bbox top-left — the same convention). When several
    /// templates share a name (different PDKs), the first in enumeration order
    /// wins — deterministic; the pick is recorded in <paramref name="resolutionNotes"/>
    /// (once per duplicated name) so the cross-PDK collision stays visible to the
    /// user instead of hiding behind the first-wins rule.
    /// </summary>
    /// <param name="templates">The currently loaded component templates.</param>
    /// <param name="resolutionNotes">
    /// Optional sink (e.g. the import's info-notes list) receiving a note whenever a
    /// cell name resolves to one of several same-named templates.
    /// </param>
    public static Func<string, KnownComponent?> BuildKnownComponentResolver(
        IEnumerable<ComponentTemplate> templates,
        IList<string>? resolutionNotes = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var groups = templates.GroupBy(t => t.Name, StringComparer.Ordinal).ToList();
        var byName = groups.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var duplicatedPdkSources = groups
            .Where(g => g.Count() > 1)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => t.PdkSource).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var noted = new HashSet<string>(StringComparer.Ordinal);

        return cellName =>
        {
            if (cellName is null || !byName.TryGetValue(cellName, out var template))
                return null;

            if (resolutionNotes is not null
                && duplicatedPdkSources.TryGetValue(cellName, out var pdkSources)
                && noted.Add(cellName))
            {
                resolutionNotes.Add(
                    $"Cell name '{cellName}' is provided by {pdkSources.Count} PDKs " +
                    $"({string.Join(", ", pdkSources.Select(s => $"'{s}'"))}); resolved to the first " +
                    $"in library order (PDK '{template.PdkSource}').");
            }
            return ToKnownComponent(template);
        };
    }

    /// <summary>Converts one library template to the importer's known-component shape.</summary>
    internal static KnownComponent ToKnownComponent(ComponentTemplate template) =>
        new(
            template.Name,
            template.PdkSource,
            template.WidthMicrometers,
            template.HeightMicrometers,
            template.PinDefinitions.Select(p => new DetectedPin
            {
                Name = p.Name,
                XUm = p.OffsetX,
                YUm = p.OffsetY,
                AngleDegrees = p.AngleDegrees,
                WidthUm = 0,
                Source = DetectedPinSource.Label,
            }).ToList());
}
