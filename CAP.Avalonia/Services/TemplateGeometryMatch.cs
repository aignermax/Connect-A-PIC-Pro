using CAP_Core.Components.Core;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services;

/// <summary>
/// Decides whether a canvas instance's geometry is still identical to its PDK
/// template draft (issue #580 E). An FDTD-recomputed S-matrix may only be
/// promoted to the template-scoped (user-global) override when this holds —
/// otherwise instances with modified geometry would silently receive an
/// S-matrix computed for a different shape.
/// </summary>
public static class TemplateGeometryMatch
{
    /// <summary>
    /// Returns true when the component's geometry matches the template draft:
    /// no geometry-affecting Nazca override is active (raw code or a
    /// module/function/parameter override, see <see cref="ComponentGeometryKey"/>),
    /// and the live Nazca call tuple (module | function | parameters) equals the
    /// template values.
    /// </summary>
    /// <param name="component">Live canvas instance to check.</param>
    /// <param name="activeOverride">The instance's stored Nazca override record, or null if none.</param>
    /// <param name="templateModuleName">The PDK template's original module name.</param>
    /// <param name="templateFunctionName">The PDK template's original function name.</param>
    /// <param name="templateFunctionParameters">The PDK template's original parameter string.</param>
    public static bool Matches(
        Component component,
        NazcaCodeOverride? activeOverride,
        string? templateModuleName,
        string? templateFunctionName,
        string? templateFunctionParameters)
    {
        if (activeOverride != null && HasGeometryOverride(activeOverride))
            return false;

        return SameText(component.NazcaModuleName, templateModuleName)
            && SameText(component.NazcaFunctionName, templateFunctionName)
            && SameText(component.NazcaFunctionParameters, templateFunctionParameters);
    }

    /// <summary>
    /// True when the override record carries any geometry-affecting field: raw
    /// cell code, or a module/function/parameter override (null means "use the
    /// PDK template value", so non-null means modified).
    /// </summary>
    private static bool HasGeometryOverride(NazcaCodeOverride o) =>
        !string.IsNullOrWhiteSpace(o.RawCode)
        || o.FunctionName != null
        || o.FunctionParameters != null
        || o.ModuleName != null;

    /// <summary>Ordinal comparison treating null and empty as equal.</summary>
    private static bool SameText(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
}
