using CAP.Avalonia.ViewModels.Solvers;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for the Tidy3D cloud solver: one place to configure the API
/// key shared by the FDTD S-matrix backend and the Tidy3D mode-solver backend.
/// </summary>
public class Tidy3dSettingsPage : ISettingsPage
{
    /// <inheritdoc/>
    public string Title => "Tidy3D Cloud";

    /// <inheritdoc/>
    public string Icon => "☁️";

    /// <inheritdoc/>
    public string? Category => null;

    /// <inheritdoc/>
    public object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="Tidy3dSettingsPage"/>.
    /// </summary>
    public Tidy3dSettingsPage(Tidy3dSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
    }
}
