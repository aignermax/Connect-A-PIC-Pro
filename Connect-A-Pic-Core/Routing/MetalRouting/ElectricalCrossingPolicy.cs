namespace CAP_Core.Routing.MetalRouting;

/// <summary>
/// Process-dependent policy for a metal trace crossing an optical waveguide (issue #682).
/// Some fabs route metal on a higher layer and allow plain crossings; others require a
/// bridge structure (e.g. an air bridge) wherever metal crosses a waveguide.
/// </summary>
public enum ElectricalCrossingPolicy
{
    /// <summary>Metal may cross waveguides directly (metal sits on a higher layer).</summary>
    DirectCrossingAllowed,

    /// <summary>Every metal/waveguide crossing needs a bridge element in the layout.</summary>
    BridgeRequired,
}
