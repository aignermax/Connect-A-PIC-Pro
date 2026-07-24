using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// Contract for a format shown in the unified Export menu flyout.
/// Implement this interface and register with <see cref="ExportMenuViewModel"/>
/// to add a new export format without touching MainWindow.axaml.
/// </summary>
public interface IExportFormat
{
    /// <summary>Display name shown in the export menu (e.g., "Nazca → GDS (Python)").</summary>
    string Name { get; }

    /// <summary>Emoji icon displayed next to the format name.</summary>
    string Icon { get; }

    /// <summary>Short description shown as a tooltip or subtitle.</summary>
    string Description { get; }

    /// <summary>Button background color as an HTML/Avalonia color string.</summary>
    string Background { get; }

    /// <summary>
    /// Command that triggers the full export flow for this format.
    /// May open a format-specific options dialog before exporting.
    /// </summary>
    IAsyncRelayCommand ExportCommand { get; }
}
