using CAP_Core.Components.Core;

namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// The fabrication-process routing cross-section of one waveguide connection (or frozen
/// waveguide path): the waveguide width and GDS layer stamped onto the endpoint pins by
/// the PDK they came from, plus the endpoint component owning a gdsfactory routing
/// cross-section when one exists. All members are null when neither endpoint carries
/// process data (demo/playground components) — exporters then keep their historical
/// global defaults.
/// </summary>
public readonly record struct ProcessCrossSection(
    double? WidthMicrometers,
    int? GdsLayer,
    Component? GdsFactoryOwner)
{
    /// <summary>True when an endpoint pin contributed process-derived optical geometry (width or layer).</summary>
    public bool HasOpticalStamps => WidthMicrometers.HasValue || GdsLayer.HasValue;

    /// <summary>The owner component's gdsfactory routing cross-section (e.g. "xs_nc"), or null.</summary>
    public string? GdsFactoryRoutingCrossSection => GdsFactoryOwner?.GdsFactoryRoutingCrossSection;
}

/// <summary>
/// Resolves which process cross-section a waveguide connection routes on from its
/// endpoint pins — never from a global user preference. PDK-converted pins carry their
/// process' width/layer stamps, so a connection between two components of one chiplet
/// resolves to that chiplet's stack; a frozen group path keeps its pins (and with them
/// the chiplet's stamps) frozen together with the geometry, so grouped routes resolve
/// the same way. On a cross-process boundary (a chiplet abutment with genuinely
/// different endpoint stacks) the start pin's process wins — one deterministic owner,
/// matching the direction the connection was drawn.
/// </summary>
public static class ConnectionCrossSectionResolver
{
    /// <summary>
    /// Resolves the process cross-section for a connection between the two given pins.
    /// Returns the all-null cross-section when neither pin carries process data.
    /// </summary>
    public static ProcessCrossSection Resolve(PhysicalPin? startPin, PhysicalPin? endPin) =>
        new(
            startPin?.WaveguideWidthMicrometers ?? endPin?.WaveguideWidthMicrometers,
            startPin?.Layer ?? endPin?.Layer,
            OwnerOf(startPin) ?? OwnerOf(endPin));

    private static Component? OwnerOf(PhysicalPin? pin) =>
        pin?.ParentComponent is { } component
        && !string.IsNullOrEmpty(component.GdsFactoryRoutingCrossSection)
            ? component
            : null;
}
