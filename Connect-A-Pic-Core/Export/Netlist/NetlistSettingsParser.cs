using System.Globalization;

namespace CAP_Core.Export.Netlist;

/// <summary>
/// Parses a component's stored parameter string (e.g. <c>"length=10, width=0.5"</c>,
/// the Nazca call-argument format) into simple key/value settings for the netlist.
/// Only plain <c>identifier=number</c> and <c>identifier='string'</c> pairs are kept;
/// anything that looks like an expression is skipped rather than guessed at — the
/// netlist must never fabricate parameter values (issue #687).
/// </summary>
public static class NetlistSettingsParser
{
    /// <summary>
    /// Parses <paramref name="parameterString"/> into settings. Returns an empty
    /// dictionary for null/blank input or when nothing is parseable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? parameterString)
    {
        var settings = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(parameterString))
            return settings;

        foreach (var entry in parameterString.Split(','))
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = entry[..separatorIndex].Trim();
            var value = entry[(separatorIndex + 1)..].Trim();
            if (!IsIdentifier(key))
                continue;

            if (TryNormalizeValue(value, out var normalized))
                settings[key] = normalized;
        }
        return settings;
    }

    private static bool IsIdentifier(string key)
    {
        if (key.Length == 0 || char.IsDigit(key[0]))
            return false;
        return key.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    /// <summary>
    /// Accepts plain numbers (kept verbatim when invariant-parseable) and single- or
    /// double-quoted strings (unquoted). Everything else is rejected.
    /// </summary>
    private static bool TryNormalizeValue(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length == 0)
            return false;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            normalized = value;
            return true;
        }

        var isQuoted = value.Length >= 2
            && (value[0] == '\'' || value[0] == '"')
            && value[^1] == value[0];
        if (isQuoted)
        {
            normalized = value[1..^1];
            return true;
        }
        return false;
    }
}
