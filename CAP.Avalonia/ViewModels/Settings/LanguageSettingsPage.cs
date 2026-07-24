using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page hosting the UI language picker (System / English / Deutsch /
/// 中文 / Español). The nav title updates live when the language is switched.
/// </summary>
public class LanguageSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "🌐";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>Initializes a new instance of <see cref="LanguageSettingsPage"/>.</summary>
    public LanguageSettingsPage(LanguageSettingsViewModel viewModel, LocalizationService localization)
        : base("Settings.Language.PageTitle", localization)
    {
        ViewModel = viewModel;
    }
}
