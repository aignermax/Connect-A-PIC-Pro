using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Update;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for software update configuration.
/// Delegates to the shared <see cref="UpdateViewModel"/> so
/// startup-check state is shared with the main-window update banner.
/// </summary>
public class UpdateSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "🔄";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="UpdateSettingsPage"/>.
    /// </summary>
    public UpdateSettingsPage(UpdateViewModel updateViewModel, LocalizationService localization)
        : base("Settings.Section.SoftwareUpdates", localization)
    {
        ViewModel = updateViewModel;
    }
}
