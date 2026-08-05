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
    /// The GDS cell-name form of a template/display name: GDS cell names cannot
    /// carry spaces and similar characters (writers variously fold them to '$'
    /// or '_'), so our exporter sanitizes to <c>[A-Za-z0-9_.-]</c> with
    /// everything else mapped to '_' — and the import resolver folds template
    /// names with the same rule, making the round trip
    /// "Phase Shifter" ↔ "Phase_Shifter" exact. Shared by
    /// <c>NazcaPinLabelWrapperWriter</c> (export) and
    /// <see cref="BuildKnownComponentResolver"/> (import) so the two sides can
    /// never drift apart.
    /// </summary>
    internal static string SanitizeGdsCellName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9_.\-]", "_");

    /// <summary>
    /// Returns a resolver mapping GDS cell names to loaded templates. Two exact
    /// match kinds are tried, in order:
    /// <list type="number">
    /// <item>cell name == <see cref="ComponentTemplate.Name"/> (gdsfactory-style
    /// files name cells after their component);</item>
    /// <item>cell name == <see cref="ComponentTemplate.NazcaFunctionName"/> of a
    /// PARAMETERLESS template — our own nazca export names its stub cells
    /// exactly that (<c>ebeam_BondPad</c>), and the klayout upgrade keeps the
    /// name when it swaps in foundry geometry, so the binding round-trips.
    /// Parameterized templates are excluded on purpose: the cell name cannot
    /// prove which parameter set the geometry carries, so binding it to the
    /// template's default parameters could silently mis-place — such cells
    /// become new drafts instead. Several parameterless templates sharing one
    /// function name are ambiguous and likewise resolve to nothing.</item>
    /// </list>
    /// Gdsfactory hash-suffix stripping itself is the importer's job
    /// (<c>GdsHierarchyImportSession.ResolveKnown</c>) — this resolver only
    /// answers exact-name lookups. Template pins are converted to the app-space
    /// <see cref="DetectedPin"/> shape the importer consumes (offsets are
    /// already µm, Y-down, relative to the component bbox top-left — the same
    /// convention; the pin KIND rides along as the authoritative
    /// <see cref="DetectedPin.IsElectrical"/>). When several templates share a
    /// name (different PDKs), the first in enumeration order wins —
    /// deterministic; the pick is recorded in <paramref name="resolutionNotes"/>
    /// (once per duplicated name) so the cross-PDK collision stays visible to
    /// the user instead of hiding behind the first-wins rule.
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

        // Function-name fallback (parameterless templates only; ambiguous
        // function names — several distinct templates sharing one — are left
        // out entirely, never guessed).
        var byFunctionName = templates
            .Where(t => !string.IsNullOrEmpty(t.NazcaFunctionName)
                        && string.IsNullOrWhiteSpace(t.NazcaParameters))
            .GroupBy(t => t.NazcaFunctionName!, StringComparer.Ordinal)
            .Where(g => g.Select(t => (t.Name, t.PdkSource)).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Sanitized-name fallback for our own pin-label wrapper cells (the GDS
        // namespace cannot hold spaces); names that collide only AFTER folding
        // ("A B" vs "A_B") are ambiguous and left out, never guessed.
        var bySanitizedName = templates
            .GroupBy(t => SanitizeGdsCellName(t.Name), StringComparer.Ordinal)
            .Where(g => g.Select(t => (t.Name, t.PdkSource)).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var noted = new HashSet<string>(StringComparer.Ordinal);

        return cellName =>
        {
            if (cellName is null)
                return null;
            if (!byName.TryGetValue(cellName, out var template)
                && !byFunctionName.TryGetValue(cellName, out template)
                && !bySanitizedName.TryGetValue(cellName, out template))
                return null;

            if (resolutionNotes is not null
                && duplicatedPdkSources.TryGetValue(template.Name, out var pdkSources)
                && noted.Add(template.Name))
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
                IsElectrical = p.Kind == CAP_Core.Components.Core.MatterType.Electricity,
            }).ToList());
}
