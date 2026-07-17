using System.ComponentModel;
using System.Globalization;

namespace CAP.Avalonia.Services.Localization;

/// <summary>
/// Holds the active UI language and translates string keys. Views bind through
/// <see cref="LocalizeExtension"/> to the indexer; switching the language raises
/// <c>Item[]</c> so every bound string re-reads live (no restart needed).
/// Lookup order: active language → English → the key itself (last resort, covered
/// by a completeness test so it never shows in practice).
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>Preference value meaning "follow the OS display language".</summary>
    public const string SystemLanguageCode = "system";

    private readonly Func<string, IReadOnlyDictionary<string, string>> _tableLoader;
    private readonly Func<CultureInfo> _systemCultureProvider;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tables = new();

    /// <summary>
    /// Process-wide instance used by AXAML bindings (markup extensions cannot use
    /// constructor DI). The same instance is registered in the DI container.
    /// </summary>
    public static LocalizationService Instance { get; } = new();

    /// <summary>Creates the production service: embedded tables, OS UI culture.</summary>
    public LocalizationService()
        : this(LocalizationResourceLoader.Load, () => CultureInfo.CurrentUICulture)
    {
    }

    /// <summary>Test constructor with injectable table source and system culture.</summary>
    internal LocalizationService(
        Func<string, IReadOnlyDictionary<string, string>> tableLoader,
        Func<CultureInfo> systemCultureProvider)
    {
        _tableLoader = tableLoader;
        _systemCultureProvider = systemCultureProvider;
        ActiveLanguageCode = LanguageResolver.Resolve(_systemCultureProvider());
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The resolved active language code ("en", "de", "zh-Hans" or "es").</summary>
    public string ActiveLanguageCode { get; private set; }

    /// <summary>Translates <paramref name="key"/> in the active language (bindable indexer).</summary>
    public string this[string key] => Translate(key);

    /// <summary>
    /// Switches the UI language. <paramref name="languageCodeOrSystem"/> is a shipped
    /// code or <see cref="SystemLanguageCode"/> (resolves the OS display language);
    /// unknown values fall back to English so stale preferences can never break startup.
    /// </summary>
    public void SetLanguage(string? languageCodeOrSystem)
    {
        var code = ResolveRequestedCode(languageCodeOrSystem);
        if (code == ActiveLanguageCode)
            return;

        ActiveLanguageCode = code;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveLanguageCode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>Translates a key: active table → English table → the key itself.</summary>
    public string Translate(string key)
    {
        if (GetTable(ActiveLanguageCode).TryGetValue(key, out var text))
            return text;
        if (GetTable(SupportedLanguage.English.Code).TryGetValue(key, out var english))
            return english;
        return key;
    }

    private string ResolveRequestedCode(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested == SystemLanguageCode)
            return LanguageResolver.Resolve(_systemCultureProvider());

        var match = SupportedLanguage.All.FirstOrDefault(
            l => string.Equals(l.Code, requested, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? SupportedLanguage.English.Code;
    }

    private IReadOnlyDictionary<string, string> GetTable(string code)
    {
        if (!_tables.TryGetValue(code, out var table))
        {
            table = _tableLoader(code);
            _tables[code] = table;
        }
        return table;
    }
}
