using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Builds the <see cref="GdsHierarchyImportOptions.ResolveKnownComponent"/>
/// callback from the currently loaded component library: a GDS cell whose name
/// matches a <see cref="ComponentTemplate"/> resolves to that template, so the
/// hierarchy importer places the existing PDK component instead of importing a
/// duplicate draft. Several cell-name shapes are recognized (display name,
/// sanitized name, nazca function name and its last segment, the synthesized
/// <c>nazca_&lt;name&gt;</c> fallback — see
/// <see cref="BuildKnownComponentResolver"/>), and competing matches follow PDK
/// precedence (bundled PDKs, then user PDKs, then prior GDS-import PDKs).
/// Gdsfactory hash-suffix stripping itself is the importer's job
/// (<c>GdsHierarchyImportSession.ResolveKnown</c>).
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
    /// Returns a resolver mapping GDS cell names to loaded templates. Every
    /// template registers several lookup keys — the cell-name shapes our own
    /// and foreign exports produce:
    /// <list type="number">
    /// <item><see cref="ComponentTemplate.Name"/> verbatim (gdsfactory-style
    /// files name cells after their component);</item>
    /// <item>the sanitized display name (<see cref="SanitizeGdsCellName"/>) —
    /// our own pin-label wrapper cells ("Phase Shifter" →
    /// <c>Phase_Shifter</c>);</item>
    /// <item>the <see cref="ComponentTemplate.NazcaFunctionName"/> of a
    /// PARAMETERLESS template — our own nazca export names its stub cells
    /// exactly that (<c>ebeam_BondPad</c>), and the klayout upgrade keeps the
    /// name when it swaps in foundry geometry, so the binding round-trips;</item>
    /// <item>the function name's LAST dot segment (<c>demo.mmi2x2_dp</c> →
    /// <c>mmi2x2_dp</c>) — demofab module calls land in the GDS under their
    /// bare function name;</item>
    /// <item>the synthesized <c>nazca_&lt;name&gt;</c> of a function-less
    /// parameterless template — the fallback function name
    /// <c>ComponentTemplates.CreateFromTemplate</c> computes, under which such
    /// components export.</item>
    /// </list>
    /// Function-derived keys (3–5) are registered for parameterless templates
    /// only: the cell name cannot prove which parameter set the geometry
    /// carries, so binding it to the template's default parameters could
    /// silently mis-place. Keys of shapes 2–5 shared by several distinct
    /// templates are ambiguous and left out entirely — never guessed.
    /// <para>
    /// When several templates match one cell name, the pick follows PDK
    /// precedence: bundled PDKs first, then user PDKs, then prior
    /// "GDS Import - *" PDKs LAST (a re-import must prefer the real PDK over
    /// yesterday's black-box import of the same file — e.g. cell
    /// <c>mmi2x2_dp</c> resolves to the bundled demofab template, not to the
    /// stale same-named import draft). Within one tier the first in library
    /// order wins — deterministic; the pick is recorded in
    /// <paramref name="resolutionNotes"/> (once per cell name) so the cross-PDK
    /// collision stays visible to the user.
    /// </para>
    /// Gdsfactory hash-suffix stripping itself is the importer's job
    /// (<c>GdsHierarchyImportSession.ResolveKnown</c>) — this resolver only
    /// answers exact-key lookups. Template pins are converted to the app-space
    /// <see cref="DetectedPin"/> shape the importer consumes (offsets are
    /// already µm, Y-down, relative to the component bbox top-left — the same
    /// convention; the pin KIND rides along as the authoritative
    /// <see cref="DetectedPin.IsElectrical"/>).
    /// </summary>
    /// <param name="templates">The currently loaded component templates.</param>
    /// <param name="resolutionNotes">
    /// Optional sink (e.g. the import's info-notes list) receiving a note whenever a
    /// cell name matches templates from several PDKs.
    /// </param>
    public static Func<string, KnownComponent?> BuildKnownComponentResolver(
        IEnumerable<ComponentTemplate> templates,
        IList<string>? resolutionNotes = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var templateList = templates.ToList();

        // Exact display names are never excluded: several PDKs providing the
        // same component name resolve deterministically (tier + library order)
        // with a note, mirroring the long-standing first-wins behavior.
        var byName = GroupByKey(templateList, t => t.Name);

        // Function-derived keys: parameterless templates only (see above). The
        // last dot segment covers demofab module calls (demo.mmi2x2_dp lands in
        // the GDS as mmi2x2_dp); single-segment names register no segment key
        // (it would duplicate the full-name key).
        var parameterless = templateList
            .Where(t => string.IsNullOrWhiteSpace(t.NazcaParameters))
            .ToList();
        var withFunction = parameterless
            .Where(t => !string.IsNullOrEmpty(t.NazcaFunctionName))
            .ToList();
        var byFunctionName = UnambiguousGroupByKey(withFunction, t => t.NazcaFunctionName!);
        var byFunctionSegment = UnambiguousGroupByKey(
            withFunction.Where(t => t.NazcaFunctionName!.Contains('.', StringComparison.Ordinal)),
            t => LastFunctionSegment(t.NazcaFunctionName!));
        var bySynthesizedName = UnambiguousGroupByKey(
            parameterless.Where(t => string.IsNullOrEmpty(t.NazcaFunctionName)),
            t => $"nazca_{t.Name.ToLower().Replace(" ", "_")}");

        // Sanitized display names: keys that collide only AFTER folding
        // ("A B" vs "A_B") are ambiguous — left out, never guessed.
        var bySanitizedName = UnambiguousGroupByKey(templateList, t => SanitizeGdsCellName(t.Name));

        // Lookup order = evidence strength: exact display name first, then the
        // full function name, the sanitized name, and the derived shapes last.
        // DistinctBy keeps each template's STRONGEST shape when several hit.
        var keySources = new[]
        {
            byName, byFunctionName, bySanitizedName, byFunctionSegment, bySynthesizedName,
        };

        var noted = new HashSet<string>(StringComparer.Ordinal);

        return cellName =>
        {
            if (cellName is null)
                return null;

            var candidates = new List<(ComponentTemplate Template, int Shape)>();
            for (var shape = 0; shape < keySources.Length; shape++)
            {
                if (keySources[shape].TryGetValue(cellName, out var hits))
                    candidates.AddRange(hits.Select(t => (t, shape)));
            }
            if (candidates.Count == 0)
                return null;

            var pick = candidates
                .DistinctBy(c => (c.Template.Name, c.Template.PdkSource))
                .OrderBy(c => PrecedenceTier(c.Template))
                .ThenBy(c => c.Shape)
                .First().Template;

            if (resolutionNotes is not null && noted.Add(cellName))
            {
                var pdkSources = candidates
                    .Select(c => c.Template.PdkSource)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (pdkSources.Count > 1)
                {
                    resolutionNotes.Add(
                        $"Cell name '{cellName}' is provided by {pdkSources.Count} PDKs " +
                        $"({string.Join(", ", pdkSources.Select(s => $"'{s}'"))}); resolved to " +
                        $"'{pick.Name}' (PDK '{pick.PdkSource}') — precedence: bundled PDKs, then " +
                        "user PDKs, then earlier GDS imports; library order breaks ties within a tier.");
                }
            }
            return ToKnownComponent(pick);
        };
    }

    /// <summary>
    /// The PDK precedence tier of a template: 0 = bundled PDK, 1 = user PDK,
    /// 2 = a prior "GDS Import - *" PDK (a re-import prefers the real PDK over
    /// yesterday's black-box import of the same file).
    /// </summary>
    private static int PrecedenceTier(ComponentTemplate template) =>
        template.PdkSource.StartsWith(GdsImportService.ImportPdkNamePrefix, StringComparison.Ordinal) ? 2
        : template.IsCustom ? 1
        : 0;

    /// <summary>The last dot segment of a dotted nazca function name (<c>demo.mmi2x2_dp</c> → <c>mmi2x2_dp</c>).</summary>
    private static string LastFunctionSegment(string functionName) =>
        functionName[(functionName.LastIndexOf('.') + 1)..];

    /// <summary>Groups templates by a key; every key survives (collisions resolve by precedence at lookup).</summary>
    private static Dictionary<string, List<ComponentTemplate>> GroupByKey(
        IEnumerable<ComponentTemplate> templates, Func<ComponentTemplate, string> keyOf) =>
        templates
            .GroupBy(keyOf, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>
    /// Groups templates by a key, dropping keys shared by several DISTINCT
    /// (name, PDK) templates — an ambiguous key resolves to nothing, never a
    /// guess. Duplicate registrations of the SAME template collapse to one.
    /// </summary>
    private static Dictionary<string, List<ComponentTemplate>> UnambiguousGroupByKey(
        IEnumerable<ComponentTemplate> templates, Func<ComponentTemplate, string> keyOf) =>
        templates
            .GroupBy(keyOf, StringComparer.Ordinal)
            .Where(g => g.Select(t => (t.Name, t.PdkSource)).Distinct().Count() == 1)
            .ToDictionary(
                g => g.Key,
                g => g.DistinctBy(t => (t.Name, t.PdkSource)).ToList(),
                StringComparer.Ordinal);

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
