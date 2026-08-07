namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Normalizes the pin names of a detected-pin list so they are fit for
/// persistence and name-based pin resolution:
/// <list type="bullet">
/// <item>Blank/whitespace names (a legal empty GDS STRING) are renamed
/// <c>pin_N</c> — the PDK loader rejects blank pin names on load, so one saved
/// blank pin would poison the whole user-PDK file (the next save of the file
/// would fail validation mid-import, and every later app start would silently
/// skip the file).</item>
/// <item>Duplicate names (two labels with the same text — legal GDS — or a
/// label literally named like a heuristic <c>heur_N</c> pin) are suffixed
/// <c>_2</c>, <c>_3</c>, … — connections resolve pins by name
/// (<c>FirstOrDefault(Name)</c>), so duplicates would silently mis-wire the
/// reconstructed circuit.</item>
/// </list>
/// Renames are deterministic in pin order and every rename is recorded as a
/// warning. Already-clean lists pass through untouched (idempotent).
/// </summary>
public static class GdsPinNameNormalizer
{
    /// <summary>
    /// Returns <paramref name="pins"/> with unique, non-blank names. Renames
    /// happen in pin order; each one appends a user-presentable warning to
    /// <paramref name="warnings"/>.
    /// </summary>
    /// <param name="pins">The detected pins of one cell, in their final order.</param>
    /// <param name="ownerDescription">
    /// User-presentable owner prefix for warnings (e.g. <c>"Cell 'wgA'"</c>).
    /// </param>
    /// <param name="warnings">The import's warning sink.</param>
    public static IReadOnlyList<DetectedPin> Normalize(
        IReadOnlyList<DetectedPin> pins, string ownerDescription, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(warnings);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DetectedPin>(pins.Count);
        var nextGenerated = 1;

        foreach (var pin in pins)
        {
            var name = pin.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                do
                {
                    name = $"pin_{nextGenerated++}";
                }
                while (used.Contains(name));
                warnings.Add(
                    $"{ownerDescription}: a pin label is empty — renamed to '{name}' " +
                    "(blank pin names are rejected when the component is persisted).");
            }
            else if (used.Contains(name))
            {
                var baseName = name;
                var suffix = 2;
                do
                {
                    name = $"{baseName}_{suffix++}";
                }
                while (used.Contains(name));
                warnings.Add(
                    $"{ownerDescription}: duplicate pin name '{baseName}' — renamed a later pin to '{name}' " +
                    "(pin names must be unique: connections resolve pins by name).");
            }

            used.Add(name);
            result.Add(string.Equals(name, pin.Name, StringComparison.Ordinal) ? pin : pin with { Name = name });
        }

        return result;
    }
}
