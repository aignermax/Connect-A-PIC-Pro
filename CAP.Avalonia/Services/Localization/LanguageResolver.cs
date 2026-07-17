using System.Globalization;

namespace CAP.Avalonia.Services.Localization;

/// <summary>
/// Maps an OS culture to the closest supported UI language code.
/// Regional variants collapse to their base language (de-AT → de, es-MX → es,
/// zh-CN / zh-SG / zh-Hant-TW → zh-Hans); anything unsupported falls back to English.
/// </summary>
public static class LanguageResolver
{
    /// <summary>
    /// Resolves <paramref name="culture"/> to a shipped language code
    /// (<c>en</c>, <c>de</c>, <c>zh-Hans</c> or <c>es</c>).
    /// </summary>
    public static string Resolve(CultureInfo culture)
    {
        // TwoLetterISOLanguageName collapses every regional/script variant to the base
        // language ("zh" for zh-CN, zh-SG, zh-Hans-*, zh-Hant-*) on all three OSes,
        // so no per-platform culture-name parsing is needed.
        return culture.TwoLetterISOLanguageName switch
        {
            "en" => SupportedLanguage.English.Code,
            "de" => SupportedLanguage.German.Code,
            "es" => SupportedLanguage.Spanish.Code,
            "zh" => SupportedLanguage.ChineseSimplified.Code,
            _ => SupportedLanguage.English.Code,
        };
    }
}
