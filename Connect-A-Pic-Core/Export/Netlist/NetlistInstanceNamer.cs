using System.Text;
using CAP_Core.Components.Core;

namespace CAP_Core.Export.Netlist;

/// <summary>
/// Produces unique, netlist-safe instance names for components. Names are YAML mapping
/// keys and appear in <c>instance,port</c> references, so they are restricted to
/// letters, digits and underscores; collisions get a numeric suffix.
/// </summary>
public static class NetlistInstanceNamer
{
    /// <summary>
    /// Maps every component to a unique sanitised instance name, preserving input order.
    /// </summary>
    public static IReadOnlyDictionary<Component, string> BuildNameMap(
        IReadOnlyList<Component> components)
    {
        var names = new Dictionary<Component, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in components)
        {
            var baseName = Sanitize(comp.Name);
            var candidate = baseName;
            for (var suffix = 2; !used.Add(candidate); suffix++)
                candidate = $"{baseName}_{suffix}";
            names[comp] = candidate;
        }
        return names;
    }

    /// <summary>
    /// Replaces every character outside <c>[A-Za-z0-9_]</c> with an underscore and
    /// prefixes a leading digit, mirroring the SAX exporter's identifier rules.
    /// </summary>
    public static string Sanitize(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return "comp";

        var sb = new StringBuilder(rawName.Length);
        foreach (var ch in rawName)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

        var result = sb.ToString();
        return char.IsDigit(result[0]) ? "_" + result : result;
    }
}
