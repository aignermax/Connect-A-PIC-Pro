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
    /// wins — deterministic.
    /// </summary>
    public static Func<string, KnownComponent?> BuildKnownComponentResolver(
        IEnumerable<ComponentTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var byName = templates
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return cellName =>
            cellName is not null && byName.TryGetValue(cellName, out var template)
                ? ToKnownComponent(template)
                : null;
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
