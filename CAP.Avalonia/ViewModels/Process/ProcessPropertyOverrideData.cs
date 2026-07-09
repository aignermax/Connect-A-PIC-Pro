namespace CAP.Avalonia.ViewModels.Process;

/// <summary>
/// One design-specific override on top of a preset fabrication process (issue #696).
/// The design stores a reference to the preset PDK plus only the properties the user
/// changed, so the preset/PDK file on disk stays untouched and later preset updates
/// still flow through to un-overridden properties. Serialised into the .lun file's
/// <c>ActiveProcess</c> section; all fields are optional for backward compatibility.
/// </summary>
public class ProcessPropertyOverrideData
{
    /// <summary>Marker in <see cref="Property"/> for a row added on top of the preset; <see cref="Value"/> holds the row JSON.</summary>
    public const string RowAdded = "(row added)";

    /// <summary>Marker in <see cref="Property"/> for a preset row removed (or renamed away) by the design.</summary>
    public const string RowRemoved = "(row removed)";

    /// <summary>The <c>layers</c> section of a process definition.</summary>
    public const string LayersSection = "layers";

    /// <summary>The <c>xsections</c> section of a process definition.</summary>
    public const string XsectionsSection = "xsections";

    /// <summary>The <c>materials</c> section of a process definition.</summary>
    public const string MaterialsSection = "materials";

    /// <summary>Which process section the override applies to (see the section constants).</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Name of the row (layer / cross-section / material) the override applies to.</summary>
    public string RowName { get; set; } = string.Empty;

    /// <summary>Overridden property name (e.g. <c>WidthUm</c>), or a row marker.</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>New value formatted with the invariant culture (or the row JSON for additions).</summary>
    public string? Value { get; set; }
}
