namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for global interconnect (waveguide routing) defaults:
/// width, bend radius and optional GDS layer (issue #574).
/// </summary>
public class InterconnectSettingsPage : ISettingsPage
{
    /// <inheritdoc/>
    public string Title => "Interconnects";

    /// <inheritdoc/>
    public string Icon => "〰";

    /// <inheritdoc/>
    public string? Category => "Export";

    /// <inheritdoc/>
    public object ViewModel { get; }

    /// <summary>Initializes a new instance of <see cref="InterconnectSettingsPage"/>.</summary>
    public InterconnectSettingsPage(InterconnectSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
    }
}
