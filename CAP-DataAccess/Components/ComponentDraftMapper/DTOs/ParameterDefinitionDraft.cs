using System.Text.Json.Serialization;

namespace CAP_DataAccess.Components.ComponentDraftMapper.DTOs
{
    /// <summary>
    /// DTO for a named parameter in a parametric S-Matrix definition.
    /// Parameters are referenced by name in formula expressions.
    /// </summary>
    public class ParameterDefinitionDraft
    {
        /// <summary>
        /// Name of the parameter (e.g., "coupling_ratio", "phase_shift").
        /// Used as the variable name in formulas.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>
        /// Default value for the parameter.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public double DefaultValue { get; set; }

        /// <summary>
        /// Minimum allowed value.
        /// </summary>
        [JsonPropertyName("minValue")]
        public double MinValue { get; set; }

        /// <summary>
        /// Maximum allowed value.
        /// </summary>
        [JsonPropertyName("maxValue")]
        public double MaxValue { get; set; } = 1.0;

        /// <summary>
        /// Optional display label for UI sliders.
        /// </summary>
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        /// <summary>
        /// Optional physical unit shown next to the value in the UI
        /// (e.g. "dB", "%", "°"). Omit for dimensionless parameters.
        /// </summary>
        [JsonPropertyName("unit")]
        public string? Unit { get; set; }

        /// <summary>
        /// Slider index (0-based) that controls this parameter.
        /// <c>null</c> (or a missing JSON field) means this parameter has no
        /// slider binding — the formula evaluates with the parameter's
        /// <see cref="DefaultValue"/>. A nullable int is the correct shape:
        /// an <c>int</c> sentinel like <c>-1</c> conflates "unbound" with
        /// "invalid index".
        /// </summary>
        [JsonPropertyName("sliderNumber")]
        public int? SliderNumber { get; set; }
    }
}
