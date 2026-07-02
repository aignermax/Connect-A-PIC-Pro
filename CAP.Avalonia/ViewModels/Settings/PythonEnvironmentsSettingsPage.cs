using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page hosting the managed Python environment manager (create, install
/// Nazca, health-check, repair, remove, set active). Lives in Settings — not the
/// Properties sidebar — because environments are application-wide configuration.
/// </summary>
public class PythonEnvironmentsSettingsPage : ISettingsPage
{
    /// <inheritdoc/>
    public string Title => "Python Environments";

    /// <inheritdoc/>
    public string Icon => "📦";

    /// <inheritdoc/>
    public string? Category => "Export";

    /// <inheritdoc/>
    public object ViewModel { get; }

    /// <summary>Initializes a new instance of <see cref="PythonEnvironmentsSettingsPage"/>.</summary>
    /// <param name="viewModel">The shared environment-manager ViewModel from DI.</param>
    public PythonEnvironmentsSettingsPage(PythonEnvironmentManagerViewModel viewModel)
    {
        ViewModel = viewModel;
    }
}
