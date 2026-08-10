using System.Text.RegularExpressions;

namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Labels that pin auto-discovery must never turn into pins — on ANY path
/// (configured port layers included): nazca's bounding-box anchor labels
/// (top-left, bottom-center, … — placement anchors every nazca cell carries)
/// and parameter annotations (<c>R:0.0001</c>, <c>n=1.0</c> — name/value pairs
/// with a numeric tail, i.e. cell metadata). Neither can be a port; letting
/// them through on configured layers turns one accepted port-layer suggestion
/// into a dozen ghost pins on every nazca-produced cell.
/// </summary>
internal static class GdsGhostLabelFilter
{
    /// <summary>nazca's bounding-box anchor labels (top-left, bottom-center, …).</summary>
    private static readonly HashSet<string> NazcaAnchorNames = new(StringComparer.Ordinal)
    {
        "tl", "tc", "tr", "lt", "ct", "rt",
        "lc", "cc", "rc",
        "lb", "cb", "rb", "bl", "bc", "br",
        "cl", "cr",
    };

    /// <summary>Parameter annotations like <c>R:0.0001</c> or <c>n=1.0</c>.</summary>
    private static readonly Regex ParameterLabelPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\s*[:=]\s*[-+]?[0-9][0-9.eE+-]*$", RegexOptions.Compiled);

    /// <summary>True for labels the auto-discovery must never turn into pins.</summary>
    public static bool IsGhost(GdsText text)
    {
        var name = text.Text.Trim();
        return NazcaAnchorNames.Contains(name) || ParameterLabelPattern.IsMatch(name);
    }
}
