using System.Text.Json.Serialization;

namespace CAP_DataAccess.Components.ComponentDraftMapper.DTOs
{
    /// <summary>
    /// DTO for physical pin definitions in component JSON files.
    /// Physical pins define the actual µm positions of optical ports on a component,
    /// used for direct waveguide connections (non-grid mode) and Nazca export.
    /// </summary>
    public class PhysicalPinDraft
    {
        /// <summary>
        /// Name of the pin, used for Nazca export (e.g., "a0", "b1", "west0").
        /// Should match the corresponding logical pin name when applicable.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// X offset from the component origin in micrometers.
        /// </summary>
        [JsonPropertyName("offsetXMicrometers")]
        public double OffsetXMicrometers { get; set; }

        /// <summary>
        /// Y offset from the component origin in micrometers.
        /// </summary>
        [JsonPropertyName("offsetYMicrometers")]
        public double OffsetYMicrometers { get; set; }

        /// <summary>
        /// Output angle in degrees (0 = right, 90 = up, 180 = left, 270 = down).
        /// Used for waveguide routing direction.
        /// </summary>
        [JsonPropertyName("angleDegrees")]
        public double AngleDegrees { get; set; }

        /// <summary>
        /// Optional reference to the logical pin number (from pins array).
        /// When set, links this physical pin to the S-Matrix simulation.
        /// </summary>
        [JsonPropertyName("logicalPinNumber")]
        public int? LogicalPinNumber { get; set; }

        /// <summary>
        /// Signal domain of the pin: "Optical" (default) or "Electrical".
        /// Absent/null values in legacy PDKs are treated as optical.
        /// Electrical pins correspond to GDS layer 1/11 (ElecRec) metal contacts.
        /// </summary>
        [JsonPropertyName("pinKind")]
        public string? PinKind { get; set; }
    }
}
