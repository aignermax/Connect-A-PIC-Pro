using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.GdsImport.DesignScope;

/// <summary>
/// One GDS import's design-scoped component set: the imported PDK name, the
/// original .gds bytes, and the component drafts. The drafts' raw code keeps
/// the portable <c>{GdsFileName}</c> token (see
/// <c>GdsHierarchyImporter.GdsFileNameToken</c>) — the absolute path of the
/// materialized .gds copy is substituted only into the runtime registration
/// copies, never into the stored drafts, so a .lun file stays machine-portable.
/// </summary>
public sealed record DesignScopedGdsSet
{
    /// <summary>Import PDK name the placements reference as <c>PdkSource</c> ("GDS Import - …").</summary>
    public required string PdkName { get; init; }

    /// <summary>Original .gds file name (display/export only — never used as a path).</summary>
    public required string GdsFileName { get; init; }

    /// <summary>The raw bytes of the imported .gds file, embedded so the design stays self-contained.</summary>
    public required byte[] GdsBytes { get; init; }

    /// <summary>Component drafts with token-form raw code, in registration order.</summary>
    public required List<PdkComponentDraft> Drafts { get; init; }
}
