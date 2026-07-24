using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Solvers;

namespace CAP.Avalonia.ViewModels.Settings;

public class Tidy3dSettingsPage : LocalizedSettingsPage
{
    public override string Icon => "☁️";

    public override object ViewModel { get; }

    public Tidy3dSettingsPage(Tidy3dSettingsViewModel viewModel, LocalizationService localization)
        : base("Settings.Section.Tidy3dCloud", localization)
    {
        ViewModel = viewModel;
    }
}
