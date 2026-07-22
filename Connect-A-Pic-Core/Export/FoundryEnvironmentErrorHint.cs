using System.Text.RegularExpressions;

namespace CAP_Core.Export;

/// <summary>
/// Turns raw Python errors from the render subprocess into a short, actionable message when the
/// cause is a missing or unusable foundry PDK package in the active Python environment.
/// Returns null for unrecognised errors, so callers keep showing the raw error text.
/// </summary>
public static class FoundryEnvironmentErrorHint
{
    // Packages the managed-environment installer provisions. Only these get a hint —
    // an unknown module is most likely the user's own import and keeps the raw error.
    private static readonly string[] _knownFoundryModules =
        { "cspdk", "ubcpdk", "siepic_ebeam_pdk", "nazca", "gdsfactory", "klayout" };

    private static readonly Regex _missingModule = new(
        @"No module named '([A-Za-z0-9_\.]+)'", RegexOptions.Compiled);

    private static readonly Regex _missingAttribute = new(
        @"module '([A-Za-z0-9_\.]+)' has no attribute '([A-Za-z0-9_]+)'", RegexOptions.Compiled);

    /// <summary>A user-actionable message for a recognised foundry-package problem, or null.</summary>
    public static string? Describe(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return null;

        var missing = _missingModule.Match(rawError);
        if (missing.Success && IsKnownFoundryModule(missing.Groups[1].Value))
        {
            var package = TopLevel(missing.Groups[1].Value);
            return $"The foundry package '{package}' is not installed in the active Python environment — " +
                   "open Settings → Python Environments and re-run Install to add it. " +
                   "(Full Python error in the Error Console.)";
        }

        var attribute = _missingAttribute.Match(rawError);
        if (attribute.Success && IsKnownFoundryModule(attribute.Groups[1].Value))
        {
            var module = attribute.Groups[1].Value;
            var package = TopLevel(module);
            return $"The foundry package '{package}' in the active Python environment has no " +
                   $"'{module}.{attribute.Groups[2].Value}' — either the package is outdated " +
                   "(open Settings → Python Environments and re-run Install to update it) or the code " +
                   $"must import the submodule explicitly (e.g. 'import {module}.{attribute.Groups[2].Value}') " +
                   "and resolve cells via gf.get_component(...). (Full Python error in the Error Console.)";
        }

        return null;
    }

    private static bool IsKnownFoundryModule(string dottedModule) =>
        _knownFoundryModules.Contains(TopLevel(dottedModule));

    private static string TopLevel(string dottedModule) =>
        dottedModule.Split('.')[0];
}
