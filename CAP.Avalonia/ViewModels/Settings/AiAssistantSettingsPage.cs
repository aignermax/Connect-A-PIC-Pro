using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.AI;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for AI Design Assistant configuration (API key and model selection).
/// Shares the singleton <see cref="AiAssistantViewModel"/> so the key entered here
/// is immediately available to the chat panel in the right sidebar.
/// </summary>
public class AiAssistantSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "🤖";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="AiAssistantSettingsPage"/>.
    /// </summary>
    public AiAssistantSettingsPage(AiAssistantViewModel aiAssistantViewModel, LocalizationService localization)
        : base("Settings.Section.AiAssistant", localization)
    {
        ViewModel = aiAssistantViewModel;
    }
}
