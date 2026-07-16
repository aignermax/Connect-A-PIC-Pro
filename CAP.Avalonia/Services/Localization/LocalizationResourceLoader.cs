using System.Reflection;
using System.Text.Json;

namespace CAP.Avalonia.Services.Localization;

/// <summary>
/// Loads the per-language string tables embedded from <c>Assets/i18n/strings-{code}.json</c>
/// (flat JSON object: key → translated text). JSON was chosen over .resx because it is
/// trivially diffable/editable by translators, needs no designer code-gen, and avoids
/// satellite-assembly culture probing that behaves differently per OS.
/// </summary>
public static class LocalizationResourceLoader
{
    private const string ResourceNamePrefix = "i18n.strings-";

    /// <summary>
    /// Loads the string table for <paramref name="languageCode"/> (e.g. "en", "zh-Hans").
    /// Returns an empty table when the resource is missing or malformed — the
    /// <see cref="LocalizationService"/> then falls back to English per key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(string languageCode)
    {
        var assembly = typeof(LocalizationResourceLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{ResourceNamePrefix}{languageCode}.json");
        if (stream == null)
            return new Dictionary<string, string>();

        try
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return table ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
