using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page hosting the UI language picker (System / English / Deutsch /
/// 中文 / Español). The page title is read at construction; pages are transient,
/// so a re-opened Settings window shows the sidebar in the newly chosen language.
/// </summary>
public class LanguageSettingsPage : ISettingsPage
{
    /// <inheritdoc/>
    public string Title { get; }

    /// <inheritdoc/>
    public string Icon => "🌐";

    /// <inheritdoc/>
    public string? Category => null;

    /// <inheritdoc/>
    public object ViewModel { get; }

    /// <summary>Initializes a new instance of <see cref="LanguageSettingsPage"/>.</summary>
    public LanguageSettingsPage(LanguageSettingsViewModel viewModel, LocalizationService localization)
    {
        ViewModel = viewModel;
        Title = localization.Translate("Settings.Language.PageTitle");
    }
}
