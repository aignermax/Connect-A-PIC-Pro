using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// One newly registered library component of a GDS import: which imported cell
/// draft it came from and the (sanitized, deduplicated) component name it was
/// registered under.
/// </summary>
public sealed record GdsRegisteredComponent(string CellDraftName, string ComponentName);

/// <summary>
/// Result of <see cref="GdsImportService.ImportAsync"/>: what was registered
/// into the user component library plus the pure-data circuit description
/// (instances, connections, warnings) the caller turns into canvas placements
/// (see <see cref="GdsPlacementPlan"/>).
/// </summary>
public sealed record GdsImportOutcome
{
    /// <summary>The imported top cell.</summary>
    public string TopCellName { get; init; } = string.Empty;

    /// <summary>The hierarchy mode the import ran in.</summary>
    public GdsHierarchyImportMode Mode { get; init; }

    /// <summary>
    /// Components newly registered in the user library (empty when every cell
    /// resolved to an existing PDK component). Instances referencing one of
    /// these carry <see cref="GdsPlacedInstance.CellDraftName"/> —
    /// <see cref="GdsRegisteredComponent.CellDraftName"/> maps it to the
    /// registered <see cref="GdsRegisteredComponent.ComponentName"/>.
    /// </summary>
    public IReadOnlyList<GdsRegisteredComponent> RegisteredComponents { get; init; } =
        Array.Empty<GdsRegisteredComponent>();

    /// <summary>Placed instances in GDS placement order (empty in black-box mode).</summary>
    public IReadOnlyList<GdsPlacedInstance> Instances { get; init; } = Array.Empty<GdsPlacedInstance>();

    /// <summary>Reconstructed abutment connections (empty in black-box mode).</summary>
    public IReadOnlyList<GdsPinPair> Connections { get; init; } = Array.Empty<GdsPinPair>();

    /// <summary>Import warnings plus any persistence/registration warnings, in order.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Name of the user PDK the drafts were persisted to and registered from
    /// ("GDS Import - &lt;file stem&gt;"), even when nothing was registered.
    /// </summary>
    public string UserPdkName { get; init; } = string.Empty;

    /// <summary>
    /// Path of the user-PDK JSON file, or null when no drafts were registered
    /// (all cells known) and no PDK file was written.
    /// </summary>
    public string? UserPdkPath { get; init; }

    /// <summary>
    /// Final file name of the .gds copy next to the user-PDK JSON (possibly
    /// suffixed <c>-2</c>, <c>-3</c>, … on a name collision with different
    /// content), or null when no copy was needed (no drafts).
    /// </summary>
    public string? GdsFileName { get; init; }
}
