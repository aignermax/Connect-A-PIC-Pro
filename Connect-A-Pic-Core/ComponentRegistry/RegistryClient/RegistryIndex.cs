using System.Text.Json.Serialization;

namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>
/// Root object of the registry's <c>index.json</c> — the single entry point
/// listing every process and component published in the open photonic registry.
/// </summary>
public class RegistryIndex
{
    /// <summary>Version of the index schema.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>All fabrication processes known to the registry.</summary>
    [JsonPropertyName("processes")]
    public List<RegistryProcess> Processes { get; set; } = new();

    /// <summary>All components known to the registry.</summary>
    [JsonPropertyName("components")]
    public List<RegistryIndexEntry> Components { get; set; } = new();
}

/// <summary>A fabrication process entry in the registry index.</summary>
public class RegistryProcess
{
    /// <summary>Unique process identifier (kebab-case), e.g. <c>generic-si220</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Human-readable process name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Publication status of the process (e.g. <c>demo</c>, <c>verified</c>).</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

/// <summary>A component entry in the registry index.</summary>
public class RegistryIndexEntry
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

    /// <summary>Number of optical ports.</summary>
    [JsonPropertyName("portCount")]
    public int PortCount { get; set; }

    /// <summary>Repo-relative path to the component manifest (<c>component.json</c>).</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Which artifact tiers are available for this component.</summary>
    [JsonPropertyName("tiers")]
    public ArtifactTierFlags Tiers { get; set; } = new();

    /// <summary>Best (most trustworthy) artifact status across all tiers.</summary>
    [JsonPropertyName("bestStatus")]
    public string BestStatus { get; set; } = "";

    /// <summary>
    /// Repo-relative path to the geometry preview SVG, or null when the
    /// registry has not published one (additive field, photonic-registry#1).
    /// </summary>
    [JsonPropertyName("preview")]
    public string? Preview { get; set; }
}

/// <summary>Availability flags for the three registry artifact tiers.</summary>
public class ArtifactTierFlags
{
    /// <summary>True when a geometry artifact (GDS / parametric) is available.</summary>
    [JsonPropertyName("geometry")]
    public bool Geometry { get; set; }

    /// <summary>True when a simulated S-matrix artifact is available.</summary>
    [JsonPropertyName("simulated")]
    public bool Simulated { get; set; }

    /// <summary>True when a fab-measured artifact is available.</summary>
    [JsonPropertyName("measured")]
    public bool Measured { get; set; }
}
