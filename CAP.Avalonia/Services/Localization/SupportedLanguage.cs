namespace CAP.Avalonia.Services.Localization;

/// <summary>
/// A UI language shipped with Lunima. <see cref="Code"/> matches the embedded
/// resource file <c>Assets/i18n/strings-{Code}.json</c>; <see cref="NativeName"/>
/// is shown in the language picker (always in that language itself, so a user
/// stuck in the wrong language can still find their own).
/// </summary>
public sealed record SupportedLanguage(string Code, string NativeName)
{
    /// <summary>English — the source-of-truth language and runtime fallback.</summary>
    public static readonly SupportedLanguage English = new("en", "English");

    /// <summary>German.</summary>
    public static readonly SupportedLanguage German = new("de", "Deutsch");

    /// <summary>Simplified Chinese.</summary>
    public static readonly SupportedLanguage ChineseSimplified = new("zh-Hans", "中文（简体）");

    /// <summary>Spanish.</summary>
    public static readonly SupportedLanguage Spanish = new("es", "Español");

    /// <summary>Japanese.</summary>
    public static readonly SupportedLanguage Japanese = new("ja", "日本語");

    /// <summary>All languages the UI ships with, in picker order.</summary>
    public static readonly IReadOnlyList<SupportedLanguage> All =
        new[] { English, German, ChineseSimplified, Spanish, Japanese };

    /// <summary>Returns true when <paramref name="code"/> is one of the shipped language codes.</summary>
    public static bool IsSupportedCode(string? code) =>
        All.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
}
