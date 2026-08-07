using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// The draft filtering/naming rules of <see cref="GdsImportService"/>, split
/// out to keep the orchestration file under the project's 500-line gate.
/// </summary>
public sealed partial class GdsImportService
{
    /// <summary>
    /// The PDK loader's hard rules a draft must satisfy to round-trip: positive
    /// size (pins within bounds are guaranteed by the importer; a pin-LESS draft
    /// is fine — the loader accepts geometry-only components that carry
    /// outlines). Zero-size drafts are skipped with a warning — persisting them
    /// would make every later save of the same set fail validation. A
    /// draft WITHOUT pins (foundry marker/pad/logo cells carry no pin labels)
    /// registers as a geometry-only component with one warning: it places with
    /// its outlines but can never be connected.
    /// </summary>
    private static bool IsPersistable(GdsCellDraft draft, List<string> warnings)
    {
        if (draft.WidthUm <= 0 || draft.HeightUm <= 0)
        {
            warnings.Add($"Cell '{draft.CellName}' was not registered: zero size " +
                         "(the GDS cell has an empty bounding box).");
            return false;
        }
        if (draft.Pins.Count == 0)
        {
            warnings.Add($"Cell '{draft.CellName}' has no pins — registered as a " +
                         "geometry-only component (outlines only); it cannot be connected.");
        }
        return true;
    }

    /// <summary>
    /// Two different GDS cells can sanitize to the same component name;
    /// components resolve by name within their set, so later duplicates get a
    /// deterministic <c>_2</c>, <c>_3</c>, … suffix.
    /// </summary>
    private static string DeduplicateName(
        string sanitizedName, string cellName, HashSet<string> usedNames, List<string> warnings)
    {
        var candidate = sanitizedName;
        for (var n = 2; !usedNames.Add(candidate); n++)
            candidate = $"{sanitizedName}_{n}";

        if (!string.Equals(candidate, sanitizedName, StringComparison.Ordinal))
        {
            warnings.Add($"Cell '{cellName}' collides with another imported cell after name " +
                         $"sanitization; registered as '{candidate}' instead of '{sanitizedName}'.");
        }
        return candidate;
    }
}
