using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// ViewModel for the Language settings page: a picker with "System" (auto-detect
/// the OS display language, the default) plus every shipped language in its own
/// native name. Selecting an entry switches the UI live and persists the choice.
/// </summary>
public partial class LanguageSettingsViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly UserPreferencesService _preferences;
    private readonly bool _initialized;

    /// <summary>One selectable entry in the language picker.</summary>
    public sealed record LanguageOption(string Code, string DisplayName)
    {
        /// <inheritdoc/>
        public override string ToString() => DisplayName;
    }

    /// <summary>All picker entries: System first, then the shipped languages.</summary>
    public IReadOnlyList<LanguageOption> Options { get; }

    /// <summary>The currently chosen picker entry.</summary>
    [ObservableProperty]
    private LanguageOption _selectedOption;

    /// <summary>Initializes the page from the persisted preference.</summary>
    public LanguageSettingsViewModel(LocalizationService localization, UserPreferencesService preferences)
    {
        _localization = localization;
        _preferences = preferences;

        var options = new List<LanguageOption>
        {
            new(LocalizationService.SystemLanguageCode,
                localization.Translate("Settings.Language.SystemOption")),
        };
        options.AddRange(SupportedLanguage.All.Select(l => new LanguageOption(l.Code, l.NativeName)));
        Options = options;

        var saved = preferences.GetUiLanguage();
        _selectedOption = Options.FirstOrDefault(o =>
            string.Equals(o.Code, saved, StringComparison.OrdinalIgnoreCase)) ?? Options[0];
        _initialized = true;
    }

    partial void OnSelectedOptionChanged(LanguageOption value)
    {
        if (!_initialized || value == null)
            return;
        _localization.SetLanguage(value.Code);
        _preferences.SetUiLanguage(value.Code);
    }
}
