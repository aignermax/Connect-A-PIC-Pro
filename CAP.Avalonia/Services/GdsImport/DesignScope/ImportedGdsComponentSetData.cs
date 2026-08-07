using System.Text.Json.Serialization;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.GdsImport.DesignScope;

/// <summary>
/// .lun persistence shape of one <see cref="DesignScopedGdsSet"/>: the imported
/// components AND the source .gds (base64) travel inside the design file, so a
/// .lun that uses GDS-imported components stays self-contained — no global
/// user-PDK file, no sidecar next to the design (issue #830).
/// </summary>
public sealed class ImportedGdsComponentSetData
{
    /// <summary>Import PDK name the design's placements reference as <c>PdkSource</c>.</summary>
    [JsonPropertyName("pdkName")]
    public string PdkName { get; set; } = string.Empty;

    /// <summary>Original .gds file name (informational).</summary>
    [JsonPropertyName("gdsFileName")]
    public string GdsFileName { get; set; } = string.Empty;

    /// <summary>The imported .gds file, base64-encoded.</summary>
    [JsonPropertyName("gdsBase64")]
    public string GdsBase64 { get; set; } = string.Empty;

    /// <summary>Component drafts with token-form raw code (same shape as a PDK file's components).</summary>
    [JsonPropertyName("components")]
    public List<PdkComponentDraft> Components { get; set; } = new();
}
