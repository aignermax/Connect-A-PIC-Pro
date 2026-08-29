using CAP_Core.ComponentRegistry.RegistryClient;

namespace CAP.Avalonia.Services.ComponentRegistry;

/// <summary>
/// The S-parameter artifact of a registry component chosen for adoption into a
/// local PDK: its tier (<c>simulated</c> or <c>measured</c>), the artifact
/// itself, and whether it carries the <c>disputed</c> trust status (which
/// requires an explicit user confirmation before downloading, issue #773).
/// </summary>
public sealed record RegistryArtifactChoice(string Tier, ArtifactRef Artifact, bool IsDisputed);

/// <summary>
/// Picks the S-parameter artifact a registry download adopts. Physics rule:
/// only real published data qualifies — <c>withdrawn</c> artifacts are never
/// adopted, and a clean artifact is preferred over a disputed one (a disputed
/// artifact is picked only when nothing clean exists; the UI then asks for
/// explicit confirmation). Simulated data is preferred over measured data of
/// equal trust because it covers the full wavelength grid deterministically.
/// </summary>
public static class RegistryArtifactSelector
{
    /// <summary>Tier name of simulated S-matrix artifacts.</summary>
    public const string SimulatedTier = "simulated";

    /// <summary>Tier name of fab-measured artifacts.</summary>
    public const string MeasuredTier = "measured";

    private const string WithdrawnStatus = "withdrawn";
    private const string DisputedStatus = "disputed";

    /// <summary>
    /// Selects the artifact to adopt, or null when the manifest has no usable
    /// S-parameter artifact at all (empty tiers, or every artifact withdrawn).
    /// </summary>
    public static RegistryArtifactChoice? Select(ComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return Pick(manifest.Artifacts.Simulated, SimulatedTier, disputedOk: false)
            ?? Pick(manifest.Artifacts.Measured, MeasuredTier, disputedOk: false)
            ?? Pick(manifest.Artifacts.Simulated, SimulatedTier, disputedOk: true)
            ?? Pick(manifest.Artifacts.Measured, MeasuredTier, disputedOk: true);
    }

    private static RegistryArtifactChoice? Pick(List<ArtifactRef> artifacts, string tier, bool disputedOk)
    {
        foreach (var artifact in artifacts)
        {
            if (string.Equals(artifact.Status, WithdrawnStatus, StringComparison.OrdinalIgnoreCase))
                continue;
            bool isDisputed = string.Equals(artifact.Status, DisputedStatus, StringComparison.OrdinalIgnoreCase);
            if (isDisputed != disputedOk)
                continue;
            return new RegistryArtifactChoice(tier, artifact, isDisputed);
        }
        return null;
    }
}
