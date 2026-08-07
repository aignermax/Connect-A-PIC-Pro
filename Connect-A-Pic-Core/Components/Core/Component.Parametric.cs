using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Substitutes SLIDER&lt;n&gt; placeholders in a nazca parameter string with the
    /// bound sliders' current values (invariant culture) — how slider-driven
    /// parameters reach the generated export code.
    /// </summary>
    public string InsertSliderValue(string nazcaFunctionParameterString)
    {
        if (SliderMap?.Values == null) return nazcaFunctionParameterString;
        foreach (var slider in SliderMap.Values)
        {
            string pattern = "SLIDER" + slider.Number;
            nazcaFunctionParameterString = Regex.Replace(nazcaFunctionParameterString, Regex.Escape(pattern), slider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
        }
        return nazcaFunctionParameterString;
    }
}
