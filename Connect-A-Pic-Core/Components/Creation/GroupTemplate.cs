using CAP_Core.Components.Core;

namespace CAP_Core.Components.Creation;

/// <summary>
/// Represents a saved ComponentGroup template that can be instantiated from the library.
/// Contains metadata and serialized group data for preview and instantiation.
/// </summary>
public class GroupTemplate
{
    /// <summary>
    /// Human-readable name for this group template.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Optional description of what this group does.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Category for organizing templates (e.g., "User Groups", "PDK Macros").
    /// </summary>
    public string Category { get; set; } = "User Groups";

    /// <summary>
    /// File path to the group JSON file (null for unsaved templates).
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Number of child components in this group.
    /// </summary>
    public int ComponentCount { get; set; }

    /// <summary>
    /// Width of the group in micrometers (for preview sizing).
    /// </summary>
    public double WidthMicrometers { get; set; }

    /// <summary>
    /// Height of the group in micrometers (for preview sizing).
    /// </summary>
    public double HeightMicrometers { get; set; }

    /// <summary>
    /// Source of this template (e.g., "User", "PDK").
    /// </summary>
    public string Source { get; set; } = "User";

    /// <summary>
    /// The actual ComponentGroup instance that serves as the template.
    /// This is used for instantiation via deep copy.
    /// </summary>
    public ComponentGroup? TemplateGroup { get; set; }

    /// <summary>
    /// Per-instance Nazca override JSON keyed by template-child identifier (issue #720).
    /// Captured from the source design's override store when the template is saved, so
    /// raw-code/override components keep their geometry when the template is re-placed
    /// in another design. The JSON is opaque to the Core layer.
    /// </summary>
    public Dictionary<string, string> NazcaOverridesJson { get; set; } = new();

    /// <summary>
    /// Timestamp when this template was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Preview thumbnail data (base64-encoded PNG, or null if not generated).
    /// </summary>
    public string? PreviewThumbnailBase64 { get; set; }
}
