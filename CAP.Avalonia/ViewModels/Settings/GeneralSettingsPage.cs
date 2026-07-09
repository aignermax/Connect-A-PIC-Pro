using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for general application preferences that do not belong to a
/// dedicated category. Hosts the adaptive crossing-insertion toggle (issue #553).
/// </summary>
public class GeneralSettingsPage : ISettingsPage
{
    /// <summary>Creates the page bound to the crossing-insertion wiring.</summary>
    /// <param name="crossingBinder">Injected crossing-insertion binder (DI singleton).
    /// Null in tests / headless contexts that bypass DI — the toggle is then inert.</param>
    public GeneralSettingsPage(CrossingInsertionCanvasBinder? crossingBinder = null)
    {
        ViewModel = crossingBinder != null
            ? new GeneralSettingsViewModel(crossingBinder)
            : new GeneralSettingsViewModel();
    }

    /// <inheritdoc/>
    public string Title => "General";

    /// <inheritdoc/>
    public string Icon => "⚙";

    /// <inheritdoc/>
    public string? Category => null;

    /// <inheritdoc/>
    public object ViewModel { get; }
}
