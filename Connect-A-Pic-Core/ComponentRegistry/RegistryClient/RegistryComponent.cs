using System.Text.Json;

namespace CAP_Core.ComponentRegistry;

/// <summary>
/// Deserialized form of a registry <c>component.json</c> manifest: ports,
/// design parameters, and references to the artifact tiers (geometry,
/// simulated S-matrices, fab measurements) with their provenance.
/// </summary>
public sealed class RegistryComponent
{
    /// <summary>Gets the registry-wide component identifier (kebab-case).</summary>
    public string Id { get; init; } = "";

    /// <summary>Gets the human-readable component name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the component description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Gets the id of the process this component belongs to.</summary>
    public string Process { get; init; } = "";

    /// <summary>Gets the optical ports (GDSFactory naming: o1, o2, ...).</summary>
    public List<RegistryPort> Ports { get; init; } = new();

    /// <summary>Gets the physical properties relevant for validation.</summary>
    public RegistryComponentProperties Properties { get; init; } = new();

    /// <summary>Gets the design parameters (unit encoded in the key, e.g. "length_um").</summary>
    public Dictionary<string, JsonElement> Parameters { get; init; } = new();

    /// <summary>Gets the geometry artifact description, if any.</summary>
    public RegistryGeometry? Geometry { get; init; }

    /// <summary>Gets the simulated and measured artifact references.</summary>
    public RegistryArtifacts Artifacts { get; init; } = new();

    /// <summary>Gets the license identifier of this component's data.</summary>
    public string License { get; init; } = "";
}

/// <summary>An optical port of a registry component.</summary>
public sealed class RegistryPort
{
    /// <summary>Gets the port name (o1, o2, ...).</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the port kind (currently always "optical").</summary>
    public string Kind { get; init; } = "";

    /// <summary>Gets the optional human-readable port description.</summary>
    public string? Description { get; init; }
}

/// <summary>Physical properties the registry's CI validates against.</summary>
public sealed class RegistryComponentProperties
{
    /// <summary>Gets whether the component is passive (S-matrix must not amplify).</summary>
    public bool Passive { get; init; }

    /// <summary>Gets whether the component is reciprocal (S equals its transpose).</summary>
    public bool Reciprocal { get; init; }
}

/// <summary>Reference to the tier-1 geometry source of a component.</summary>
public sealed class RegistryGeometry
{
    /// <summary>Gets the geometry format ("gds", "parametric-nazca", "parametric-gdsfactory", "none").</summary>
    public string Format { get; init; } = "none";

    /// <summary>Gets the repo-relative path to the GDS file or parametric script.</summary>
    public string? Source { get; init; }

    /// <summary>Gets the GDS cell name, if applicable.</summary>
    public string? CellName { get; init; }
}

/// <summary>Groups the artifact references of a component by tier.</summary>
public sealed class RegistryArtifacts
{
    /// <summary>Gets the tier-2 simulated S-matrix artifacts.</summary>
    public List<RegistryArtifactRef> Simulated { get; init; } = new();

    /// <summary>Gets the tier-3 fab-measured artifacts.</summary>
    public List<RegistryArtifactRef> Measured { get; init; } = new();
}

/// <summary>Reference to one artifact file plus its trust status and provenance.</summary>
public sealed class RegistryArtifactRef
{
    /// <summary>Gets the artifact path relative to the component directory.</summary>
    public string File { get; init; } = "";

    /// <summary>Gets the trust status ("demo", "unverified", "verified", "disputed", "withdrawn").</summary>
    public string Status { get; init; } = "";

    /// <summary>Gets the provenance record describing how the data was produced.</summary>
    public RegistryProvenance Provenance { get; init; } = new();
}

/// <summary>Provenance of an artifact: how, by whom, and from which fab run it was produced.</summary>
public sealed class RegistryProvenance
{
    /// <summary>Gets the method ("analytic-model", "fdtd", "eme", "fem", "measurement").</summary>
    public string Method { get; init; } = "";

    /// <summary>Gets the tool and version used (e.g. "tidy3d 2.7").</summary>
    public string? Tool { get; init; }

    /// <summary>Gets the solver settings or measurement conditions.</summary>
    public string? Settings { get; init; }

    /// <summary>Gets who created the artifact.</summary>
    public string CreatedBy { get; init; } = "";

    /// <summary>Gets the creation date (ISO format).</summary>
    public string Date { get; init; } = "";

    /// <summary>Gets the fab that produced the measured device (measurements only).</summary>
    public string? Fab { get; init; }

    /// <summary>Gets the fabrication run identifier (measurements only).</summary>
    public string? RunId { get; init; }
}
