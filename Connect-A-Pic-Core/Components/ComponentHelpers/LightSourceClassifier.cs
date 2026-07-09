namespace CAP_Core.Components.ComponentHelpers;

/// <summary>
/// Single source of truth for deciding whether a component template acts as a
/// light-injecting source (grating/edge coupler). Shared by the properties panel
/// (laser editor visibility) and the transient/eye simulation (laser inputs) so
/// both always agree on what a light source is (Issue #689).
/// </summary>
public static class LightSourceClassifier
{
    /// <summary>
    /// Returns true when the template name denotes a coupler that injects light
    /// into the circuit (e.g. "Grating Coupler TE 1550", "Edge Coupler").
    /// Directional couplers are passive splitters and return false.
    /// </summary>
    /// <param name="templateName">The component template name, may be null.</param>
    public static bool IsLightInjectingCoupler(string? templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return false;
        if (!templateName.Contains("Coupler", StringComparison.OrdinalIgnoreCase)) return false;
        return !templateName.Contains("Directional", StringComparison.OrdinalIgnoreCase);
    }
}
