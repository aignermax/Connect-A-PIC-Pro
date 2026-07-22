using System;
using CAP_Core.Components.Core;

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
    /// the live Nazca call tuple (module | function | parameters) equals the
    /// template values.
    /// </summary>
    /// <param name="component">Live canvas instance to check.</param>
    /// <param name="templateModuleName">The PDK template's original module name.</param>
    /// <param name="templateFunctionName">The PDK template's original function name.</param>
    /// <param name="templateFunctionParameters">The PDK template's original parameter string.</param>
    public static bool Matches(
        Component component,
        string? templateModuleName,
        string? templateFunctionName,
        string? templateFunctionParameters)
    {
        return SameText(component.NazcaModuleName, templateModuleName)
            && SameText(component.NazcaFunctionName, templateFunctionName)
            && SameText(component.NazcaFunctionParameters, templateFunctionParameters);
    }

    /// <summary>Ordinal comparison treating null and empty as equal.</summary>
    private static bool SameText(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
}
