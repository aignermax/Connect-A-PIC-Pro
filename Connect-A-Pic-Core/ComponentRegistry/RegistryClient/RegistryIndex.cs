namespace CAP_Core.ComponentRegistry;

/// <summary>
/// Deserialized form of the registry's <c>index.json</c> — the single entry point
/// listing every process and component so clients can render a library view
/// without fetching each component manifest individually.
/// </summary>
public sealed class RegistryIndex
{
    /// <summary>Gets the index schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the fabrication processes available in the registry.</summary>
    public List<RegistryIndexProcess> Processes { get; init; } = new();

    /// <summary>Gets the summary entries for all registry components.</summary>
    public List<RegistryIndexComponent> Components { get; init; } = new();
}

/// <summary>Summary of a fabrication process listed in the registry index.</summary>
public sealed class RegistryIndexProcess
{
    /// <summary>Gets the process identifier (e.g. "generic-si220").</summary>
    public string Id { get; init; } = "";

    /// <summary>Gets the human-readable process name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the process trust status (e.g. "demo").</summary>
    public string Status { get; init; } = "";
}

/// <summary>Summary of a component listed in the registry index.</summary>
public sealed class RegistryIndexComponent
{
    /// <summary>Gets the registry-wide component identifier.</summary>
    public string Id { get; init; } = "";

    /// <summary>Gets the human-readable component name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the component description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Gets the id of the process this component belongs to.</summary>
    public string Process { get; init; } = "";

    /// <summary>Gets the number of optical ports.</summary>
    public int PortCount { get; init; }

    /// <summary>Gets the repo-relative path to the component manifest.</summary>
    public string Path { get; init; } = "";

    /// <summary>Gets which artifact tiers exist for this component.</summary>
    public RegistryComponentTiers Tiers { get; init; } = new();

    /// <summary>Gets the best artifact status across all tiers (e.g. "demo", "verified").</summary>
    public string BestStatus { get; init; } = "";
}

/// <summary>Flags describing which of the three artifact tiers a component provides.</summary>
public sealed class RegistryComponentTiers
{
    /// <summary>Gets whether tier 1 (geometry) data exists.</summary>
    public bool Geometry { get; init; }

    /// <summary>Gets whether tier 2 (simulated S-matrix) data exists.</summary>
    public bool Simulated { get; init; }

    /// <summary>Gets whether tier 3 (fab-measured) data exists.</summary>
    public bool Measured { get; init; }
}
