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
    /// Returns true when the template name denotes a fiber-interface coupler
    /// that injects light into the circuit: a grating or edge coupler in any
    /// PDK naming variant (e.g. "Grating Coupler TE 1550", "Grating Coupler
    /// Elliptical", "Edge Coupler"). On-chip splitters whose names also contain
    /// "Coupler" — directional, adiabatic, MMI, generic "Coupler" — are passive
    /// components, not sources, and return false.
    /// </summary>
    /// <param name="templateName">The component template name, may be null.</param>
    public static bool IsLightInjectingCoupler(string? templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return false;
        return templateName.Contains("Grating Coupler", StringComparison.OrdinalIgnoreCase)
            || templateName.Contains("Edge Coupler", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Component-based overload for contexts without a template name (group children,
    /// components re-added by ungrouping): classifies by the identifier or the
    /// PDK-derived Nazca function name (reliable — not user-editable, and preserved
    /// through prefab serialize/deserialize where Identifier may become GUID-based).
    /// </summary>
    /// <param name="component">The component instance, may be null.</param>
    public static bool IsLightInjectingCoupler(Core.Component? component)
    {
        if (component == null) return false;

        var id = component.Identifier?.ToLowerInvariant() ?? "";
        if (id.Contains("grating") || id.Contains("edge coupler"))
            return true;

        var nazcaName = component.NazcaFunctionName?.ToLowerInvariant() ?? "";
        return nazcaName.Contains("_gc_") || nazcaName.Contains("edge_coupler") || nazcaName.Contains("grating");
    }
}
