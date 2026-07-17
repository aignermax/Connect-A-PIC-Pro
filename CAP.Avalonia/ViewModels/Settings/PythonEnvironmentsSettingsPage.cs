using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page hosting the managed Python environment manager (create, install
/// Nazca, health-check, repair, remove, set active). Lives in Settings — not the
/// Properties sidebar — because environments are application-wide configuration.
/// </summary>
public class PythonEnvironmentsSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "📦";

    /// <inheritdoc/>
    public override string? Category => "Export";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>Initializes a new instance of <see cref="PythonEnvironmentsSettingsPage"/>.</summary>
    /// <param name="viewModel">The shared environment-manager ViewModel from DI.</param>
    /// <param name="localization">The process-wide localization service.</param>
    public PythonEnvironmentsSettingsPage(PythonEnvironmentManagerViewModel viewModel, LocalizationService localization)
        : base("Settings.Section.PythonEnvironments", localization)
    {
        ViewModel = viewModel;
    }

    /// <summary>
    /// Navigating to this page discovers system interpreters so the unified list (managed
    /// environments + system Pythons, each with Nazca and gdsfactory versions) is populated
    /// without a manual refresh (issue #645).
    /// </summary>
    public override void OnSelected() =>
        ((PythonEnvironmentManagerViewModel)ViewModel).RefreshInterpretersCommand.Execute(null);
}
