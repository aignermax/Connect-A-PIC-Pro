using System.Text.Json.Serialization;

namespace CAP_Core.Components.Core;

/// <summary>
/// Parametric-component metadata: named physical parameters exposed by the
/// PDK (e.g. insertion loss, splitting ratio) that the properties panel
/// renders as labeled, unit-aware editors.
/// </summary>
public partial class Component
{
    /// <summary>
    /// Physical parameter metadata (labels, units, ranges, slider bindings) for
    /// parametric components. Flows from the PDK template on placement; empty
    /// for non-parametric components. The live values themselves are the bound
    /// sliders' values — this list is immutable descriptive metadata and may be
    /// shared between instances.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<Parametric.ParameterDefinition> ParameterDefinitions { get; set; }
        = Array.Empty<Parametric.ParameterDefinition>();
}
