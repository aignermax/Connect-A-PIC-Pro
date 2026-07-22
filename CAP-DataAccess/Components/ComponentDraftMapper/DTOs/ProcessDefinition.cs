using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CAP_DataAccess.Components.ComponentDraftMapper.DTOs
{
    /// <summary>
    /// The fabrication process a PDK targets: its layer stack, waveguide/metal
    /// cross-sections (with widths and bend radii), materials, and basic design
    /// rules. A monolithic chip is built in exactly one process, so this is the
    /// unit a design's components must agree on (see issue #570). Modelled after
    /// real foundry PDKs (e.g. HHI: layers, xsections E200/E600/E1700, metal
    /// lines, per-xsection bend radii).
    /// </summary>
    public class ProcessDefinition
    {
        /// <summary>Process name (e.g. "HHI-MPW", "Generic SOI 220nm").</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Foundry / source of the process.</summary>
        [JsonPropertyName("foundry")]
        public string? Foundry { get; set; }

        /// <summary>Process / PDK version string.</summary>
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        /// <summary>
        /// Defining waveguide-core thickness in nm (e.g. 220 for 220 nm SOI). The key
        /// physical axis for process compatibility (issue #570); optional so old PDKs parse.
        /// </summary>
        [JsonPropertyName("coreThicknessNm")]
        public double? CoreThicknessNm { get; set; }

        /// <summary>GDS layer stack.</summary>
        [JsonPropertyName("layers")]
        public List<ProcessLayer> Layers { get; set; } = new();

        /// <summary>Waveguide and metal cross-sections usable for routing.</summary>
        [JsonPropertyName("xsections")]
        public List<ProcessXsection> Xsections { get; set; } = new();

        /// <summary>Optical materials and their refractive index by wavelength.</summary>
        [JsonPropertyName("materials")]
        public List<ProcessMaterial> Materials { get; set; } = new();

        /// <summary>Allowed placement/connection angles in degrees (e.g. 0/90/180/270).</summary>
        [JsonPropertyName("allowedAngles")]
        public List<int> AllowedAngles { get; set; } = new();

        /// <summary>
        /// Waveguide-crossing policy for metal routing (issue #682): true when this
        /// process requires a bridge element (e.g. air bridge) wherever a metal trace
        /// crosses an optical waveguide; false/absent (default) when metal runs on a
        /// higher layer and may cross waveguides directly. Nullable so PDKs written
        /// before this field existed round-trip through the saver byte-identically.
        /// </summary>
        [JsonPropertyName("electricalBridgeRequired")]
        public bool? ElectricalBridgeRequired { get; set; }
    }

    /// <summary>A single GDS layer in the process stack.</summary>
    public class ProcessLayer
    {
        /// <summary>Layer name (e.g. "WAVEGUIDE", "METAL-1").</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>GDS layer number.</summary>
        [JsonPropertyName("layer")]
        public int Layer { get; set; }

        /// <summary>GDS datatype.</summary>
        [JsonPropertyName("datatype")]
        public int Datatype { get; set; }

        /// <summary>Polarity hint ("Light" / "Dark"), as foundries annotate.</summary>
        [JsonPropertyName("field")]
        public string? Field { get; set; }

        /// <summary>Human-readable description.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    /// <summary>Kind of cross-section: an optical waveguide or a metal/electrical line.</summary>
    public enum XsectionKind
    {
        /// <summary>Optical (light-guiding) waveguide.</summary>
        Optical,
        /// <summary>Metal / electrical routing line.</summary>
        Metal,
    }

    /// <summary>
    /// A routing cross-section (waveguide type or metal line): its width, allowed
    /// bend radius, and the layers it is drawn on.
    /// </summary>
    public class ProcessXsection
    {
        /// <summary>Cross-section name (e.g. "E1700", "MetalDC").</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Whether this carries light or electrical current. Serialized as a string ("Optical"/"Metal").</summary>
        [JsonPropertyName("kind")]
        [JsonConverter(typeof(JsonStringEnumConverter<XsectionKind>))]
        public XsectionKind Kind { get; set; } = XsectionKind.Optical;

        /// <summary>Waveguide / line width in µm.</summary>
        [JsonPropertyName("widthUm")]
        public double WidthUm { get; set; }

        /// <summary>Minimum allowed bend radius in µm.</summary>
        [JsonPropertyName("minRadiusUm")]
        public double MinRadiusUm { get; set; }

        /// <summary>Foundry-recommended bend radius in µm (≥ minimum).</summary>
        [JsonPropertyName("recommendedRadiusUm")]
        public double RecommendedRadiusUm { get; set; }

        /// <summary>Names of the layers this cross-section is composed of.</summary>
        [JsonPropertyName("layers")]
        public List<string> Layers { get; set; } = new();

        /// <summary>Human-readable description.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    /// <summary>
    /// An optical material and its (dispersive) refractive index. Index values are
    /// user/licence-supplied — foundry material data is proprietary and never shipped.
    /// </summary>
    public class ProcessMaterial
    {
        /// <summary>Material name (e.g. "Si", "SiO2", "InP").</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Real refractive index n keyed by wavelength in nm.</summary>
        [JsonPropertyName("nByWavelengthNm")]
        public Dictionary<int, double> NByWavelengthNm { get; set; } = new();

        /// <summary>Role hint ("core" / "cladding" / "metal" / …).</summary>
        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }
}
