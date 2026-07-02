using System.Text.Json.Serialization;

namespace CAP_DataAccess.Components.ComponentDraftMapper.DTOs
{
    /// <summary>
    /// DTO for a Process Design Kit (PDK) file containing multiple component definitions.
    /// This serves as the intermediate JSON format for foundry component libraries.
    /// </summary>
    public class PdkDraft
    {
        /// <summary>
        /// File format version for backwards compatibility.
        /// </summary>
        [JsonPropertyName("fileFormatVersion")]
        public int FileFormatVersion { get; set; } = 1;

        /// <summary>
        /// Name of the PDK (e.g., "AMF Si Photonics", "IMEC iSiPP50G").
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Optional description of the PDK.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Foundry or provider name.
        /// </summary>
        [JsonPropertyName("foundry")]
        public string? Foundry { get; set; }

        /// <summary>
        /// PDK version string.
        /// </summary>
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        /// <summary>
        /// Default wavelength in nm for this PDK (e.g., 1550 for C-band).
        /// </summary>
        [JsonPropertyName("defaultWavelengthNm")]
        public int DefaultWavelengthNm { get; set; } = 1550;

        /// <summary>
        /// Python module name for Nazca import (e.g., "amf", "imec").
        /// Used to generate: import {nazcaModuleName} as pdk
        /// </summary>
        [JsonPropertyName("nazcaModuleName")]
        public string? NazcaModuleName { get; set; }

        /// <summary>
        /// Optional PDK-wide default material dispersion model.
        /// When a component has no own <c>materialDispersion</c> block, this is used as fallback.
        /// Old PDKs without this field load without dispersion (constant-loss behaviour).
        /// </summary>
        [JsonPropertyName("materialDispersion")]
        public MaterialDispersionDraft? MaterialDispersion { get; set; }

        /// <summary>
        /// Fabrication process this PDK targets (layer stack, cross-sections,
        /// materials, design rules). Optional so older PDK files still parse;
        /// when present it is the process a design's components must agree on
        /// (one process per chip — issue #570).
        /// </summary>
        [JsonPropertyName("process")]
        public ProcessDefinition? Process { get; set; }

        /// <summary>
        /// List of component definitions in this PDK.
        /// </summary>
        [JsonPropertyName("components")]
        public List<PdkComponentDraft> Components { get; set; } = new();
    }

    /// <summary>
    /// A component definition within a PDK.
    /// Simplified version of ComponentDraft optimized for PDK use.
    /// </summary>
    public class PdkComponentDraft
    {
        /// <summary>
        /// Display name of the component (e.g., "MMI 2x2 Coupler").
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Category for UI grouping (e.g., "Couplers", "Modulators", "I/O").
        /// </summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = "General";

        /// <summary>
        /// Nazca function name to call (e.g., "pdk.mmi2x2").
        /// </summary>
        [JsonPropertyName("nazcaFunction")]
        public string NazcaFunction { get; set; }

        /// <summary>
        /// Optional Nazca function parameters as string (e.g., "length=50, width=5").
        /// </summary>
        [JsonPropertyName("nazcaParameters")]
        public string? NazcaParameters { get; set; }

        /// <summary>
        /// Physical width in micrometers.
        /// </summary>
        [JsonPropertyName("widthMicrometers")]
        public double WidthMicrometers { get; set; }

        /// <summary>
        /// Physical height in micrometers.
        /// </summary>
        [JsonPropertyName("heightMicrometers")]
        public double HeightMicrometers { get; set; }

        /// <summary>
        /// Optional explicit Nazca origin offset X in micrometers.
        /// Overrides the default first-pin heuristic when set.
        /// </summary>
        // Declared right after HeightMicrometers (and not at the end of the
        // class) so the saver emits these next to the bbox dimensions —
        // matches the original hand-written PDK JSON layout and prevents
        // the saver from churning the file every time it's saved.
        [JsonPropertyName("nazcaOriginOffsetX")]
        public double? NazcaOriginOffsetX { get; set; }

        /// <inheritdoc cref="NazcaOriginOffsetX"/>
        [JsonPropertyName("nazcaOriginOffsetY")]
        public double? NazcaOriginOffsetY { get; set; }

        /// <summary>
        /// Physical pin definitions with µm coordinates.
        /// </summary>
        [JsonPropertyName("pins")]
        public List<PhysicalPinDraft> Pins { get; set; } = new();

        /// <summary>
        /// Optional S-Matrix data for light simulation.
        /// When not provided, component acts as a black box (no light simulation).
        /// </summary>
        [JsonPropertyName("sMatrix")]
        public PdkSMatrixDraft? SMatrix { get; set; }

        /// <summary>
        /// Optional per-component material dispersion model.
        /// When set, overrides the PDK-wide default. Old components without this field
        /// inherit the PDK-wide default (or constant-loss behaviour if neither is set).
        /// </summary>
        [JsonPropertyName("materialDispersion")]
        public MaterialDispersionDraft? MaterialDispersion { get; set; }

        /// <summary>
        /// Optional slider parameters (e.g., for phase shifters).
        /// </summary>
        [JsonPropertyName("sliders")]
        public List<SliderDraft>? Sliders { get; set; }

        /// <summary>
        /// Optional compact-model name for active time-dependent components
        /// (e.g. "LaserDiodeRateEquation", "PhotodiodeRc",
        /// "ElectroOpticPhaseModulator"). Must match a registered model in
        /// <c>CompactModelRegistry</c>; unknown names fail PDK validation —
        /// there is no silent fallback to passive behaviour (issue #529).
        /// </summary>
        [JsonPropertyName("compactModel")]
        public string? CompactModel { get; set; }

        /// <summary>
        /// Optional model-specific parameters for <see cref="CompactModel"/>
        /// (e.g. threshold current, responsivity, V_π). Keys are defined by
        /// each model; missing keys use the model's defaults.
        /// </summary>
        [JsonPropertyName("compactModelParameters")]
        public Dictionary<string, double>? CompactModelParameters { get; set; }
    }

    /// <summary>
    /// Simplified S-Matrix definition for PDK components.
    /// Supports both fixed-value and parametric formula-based connections.
    /// </summary>
    public class PdkSMatrixDraft
    {
        /// <summary>
        /// Wavelength in nm this S-Matrix applies to (for single-wavelength mode).
        /// </summary>
        [JsonPropertyName("wavelengthNm")]
        public int WavelengthNm { get; set; } = 1550;

        /// <summary>
        /// S-Matrix connections as list of (fromPin, toPin, magnitude, phaseDegrees).
        /// Magnitude is transmission amplitude (0-1), phase in degrees.
        /// Used for single-wavelength mode. Ignored when wavelengthData is present.
        /// </summary>
        [JsonPropertyName("connections")]
        public List<SMatrixConnection> Connections { get; set; } = new();

        /// <summary>
        /// Optional multi-wavelength S-Matrix data.
        /// When present, this takes precedence over the single-wavelength connections.
        /// Each entry contains S-Matrix connections at a specific wavelength.
        /// </summary>
        [JsonPropertyName("wavelengthData")]
        public List<WavelengthSMatrixEntry>? WavelengthData { get; set; }

        /// <summary>
        /// Optional parameter definitions for parametric S-Matrix formulas.
        /// When present, connections may use formula strings referencing these parameters.
        /// </summary>
        [JsonPropertyName("parameters")]
        public List<ParameterDefinitionDraft>? Parameters { get; set; }
    }

    /// <summary>
    /// S-Matrix connections at a specific wavelength.
    /// Used for multi-wavelength PDK components (e.g., from measured S-parameter data).
    /// </summary>
    public class WavelengthSMatrixEntry
    {
        [JsonPropertyName("wavelengthNm")]
        public int WavelengthNm { get; set; }

        [JsonPropertyName("connections")]
        public List<SMatrixConnection> Connections { get; set; } = new();
    }

    /// <summary>
    /// A single S-Matrix connection entry.
    /// Supports fixed values (magnitude/phaseDegrees) or formula strings
    /// (magnitudeFormula/phaseDegreesFormula) referencing named parameters.
    /// </summary>
    public class SMatrixConnection
    {
        /// <summary>
        /// Source pin name.
        /// </summary>
        [JsonPropertyName("fromPin")]
        public string FromPin { get; set; }

        /// <summary>
        /// Destination pin name.
        /// </summary>
        [JsonPropertyName("toPin")]
        public string ToPin { get; set; }

        /// <summary>
        /// Transmission amplitude (0-1). For a 50/50 splitter, use ~0.707 (sqrt(0.5)).
        /// Used for fixed-value connections. Ignored when magnitudeFormula is set.
        /// </summary>
        [JsonPropertyName("magnitude")]
        public double Magnitude { get; set; }

        /// <summary>
        /// Phase shift in degrees.
        /// Used for fixed-value connections. Ignored when phaseDegreesFormula is set.
        /// </summary>
        [JsonPropertyName("phaseDegrees")]
        public double PhaseDegrees { get; set; }

        /// <summary>
        /// Formula expression for magnitude (e.g., "sqrt(coupling_ratio)").
        /// When set, this overrides the fixed magnitude value.
        /// </summary>
        [JsonPropertyName("magnitudeFormula")]
        public string? MagnitudeFormula { get; set; }

        /// <summary>
        /// Formula expression for phase in degrees (e.g., "phase_shift").
        /// When set, this overrides the fixed phaseDegrees value.
        /// </summary>
        [JsonPropertyName("phaseDegreesFormula")]
        public string? PhaseDegreesFormula { get; set; }

        /// <summary>
        /// Returns true if this connection uses formula expressions.
        /// </summary>
        [JsonIgnore]
        public bool IsParametric =>
            !string.IsNullOrWhiteSpace(MagnitudeFormula) ||
            !string.IsNullOrWhiteSpace(PhaseDegreesFormula);
    }
}
