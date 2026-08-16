using CAP_Core.Components.Core;

namespace CAP_Core.Routing;

/// <summary>
/// Process bend-radius floor members of <see cref="WaveguideRouter"/>: the per-connection
/// override provider and the floor resolution for one pin pair (issue #937).
/// </summary>
public partial class WaveguideRouter
{
    /// <summary>
    /// Optional per-connection process bend-radius floor provider (µm), consulted with the
    /// connection's endpoint pins. On a multi-process canvas (e.g. a Cornerstone SiN chiplet
    /// next to a SiEPIC SOI chiplet) each connection is floored by its own endpoints' process
    /// instead of one canvas-wide value, so the stricter chiplet's foundry minimum is enforced
    /// and the looser one is not over-constrained (issue #937). A null return means "no
    /// per-connection opinion" — the canvas-wide <see cref="ProcessMinBendRadiusMicrometers"/>
    /// governs. Invoked on the routing thread, so the provider must only read pass-start
    /// snapshots, never live UI collections.
    /// </summary>
    public Func<PhysicalPin, PhysicalPin, double?>? ConnectionProcessFloorProvider { get; set; }

    /// <summary>
    /// Resolves the process bend-radius floor (µm) for one connection: the metal floor for
    /// electrical pins (RF traces must bend at the metal cross-section radius — checking one
    /// pin is authoritative, cross-kind pairs are rejected at connection creation time),
    /// the per-connection provider's value when wired and resolvable for these endpoints,
    /// otherwise the canvas-wide <see cref="ProcessMinBendRadiusMicrometers"/>.
    /// </summary>
    public double ResolveProcessFloorFor(PhysicalPin startPin, PhysicalPin endPin) =>
        startPin.MatterType == MatterType.Electricity || endPin.MatterType == MatterType.Electricity
            ? MetalProcessMinBendRadiusMicrometers
            : ConnectionProcessFloorProvider?.Invoke(startPin, endPin) ?? ProcessMinBendRadiusMicrometers;
}
