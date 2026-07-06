using System.Text.Json;
using System.Text.Json.Serialization;

namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>
/// A component manifest (<c>component.json</c>) from the photonic registry,
/// describing ports, design parameters, geometry source and artifact tiers.
/// </summary>
public class ComponentManifest
{
    /// <summary>Registry-wide component identifier (kebab-case).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Human-readable component name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Short description of the component.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Process identifier this component belongs to.</summary>
    [JsonPropertyName("process")]
    public string Process { get; set; } = "";

    /// <summary>Optical ports of the component.</summary>
    [JsonPropertyName("ports")]
    public List<RegistryPort> Ports { get; set; } = new();

    /// <summary>Physical properties (passivity, reciprocity).</summary>
    [JsonPropertyName("properties")]
    public ComponentPhysicalProperties Properties { get; set; } = new();

    /// <summary>Free-form design parameters (unit encoded in the key, e.g. <c>length_um</c>).</summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, JsonElement> Parameters { get; set; } = new();

    /// <summary>Geometry source description (may have format <c>none</c>).</summary>
    [JsonPropertyName("geometry")]
    public GeometryInfo? Geometry { get; set; }

    /// <summary>Artifact references grouped by tier (simulated / measured).</summary>
    [JsonPropertyName("artifacts")]
    public ComponentArtifacts Artifacts { get; set; } = new();

    /// <summary>License identifier for the component data.</summary>
    [JsonPropertyName("license")]
    public string License { get; set; } = "";
}

/// <summary>An optical port declared in a component manifest.</summary>
public class RegistryPort
{
    /// <summary>Port name, e.g. <c>o1</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Port kind, currently always <c>optical</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Optional free-form port description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>Physical properties of a registry component.</summary>
public class ComponentPhysicalProperties
{
    /// <summary>True when the component contains no active elements.</summary>
    [JsonPropertyName("passive")]
    public bool Passive { get; set; }

    /// <summary>True when the S-matrix is symmetric (S = Sᵀ).</summary>
    [JsonPropertyName("reciprocal")]
    public bool Reciprocal { get; set; }
}

/// <summary>Geometry source information of a registry component.</summary>
public class GeometryInfo
{
    /// <summary>Geometry format: <c>gds</c>, <c>parametric-nazca</c>, <c>parametric-gdsfactory</c> or <c>none</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    /// <summary>Repo-relative path to the GDS file or parametric script.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Cell name inside the GDS file, when applicable.</summary>
    [JsonPropertyName("cellName")]
    public string? CellName { get; set; }
}

/// <summary>Artifact references of a component, grouped by tier.</summary>
public class ComponentArtifacts
{
    /// <summary>Simulated S-matrix artifacts.</summary>
    [JsonPropertyName("simulated")]
    public List<ArtifactRef> Simulated { get; set; } = new();

    /// <summary>Fab-measured artifacts.</summary>
    [JsonPropertyName("measured")]
    public List<ArtifactRef> Measured { get; set; } = new();
}

/// <summary>Reference to a single artifact file with status and provenance.</summary>
public class ArtifactRef
{
    /// <summary>Path relative to the component directory.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    /// <summary>Trust status: <c>demo</c>, <c>unverified</c>, <c>verified</c>, <c>disputed</c> or <c>withdrawn</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>How, by whom and when this artifact was produced.</summary>
    [JsonPropertyName("provenance")]
    public ArtifactProvenance Provenance { get; set; } = new();
}

/// <summary>Provenance record of an artifact.</summary>
public class ArtifactProvenance
{
    /// <summary>Method used: <c>analytic-model</c>, <c>fdtd</c>, <c>eme</c>, <c>fem</c> or <c>measurement</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>Tool used to produce the data, e.g. <c>tidy3d 2.7</c>.</summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    /// <summary>Solver settings or measurement conditions.</summary>
    [JsonPropertyName("settings")]
    public string? Settings { get; set; }

    /// <summary>Author or script that created the artifact.</summary>
    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "";

    /// <summary>Creation date (ISO 8601).</summary>
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    /// <summary>Measurements only: which fab produced the device.</summary>
    [JsonPropertyName("fab")]
    public string? Fab { get; set; }

    /// <summary>Measurements only: fabrication run identifier.</summary>
    [JsonPropertyName("runId")]
    public string? RunId { get; set; }
}
