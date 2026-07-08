using CAP.Avalonia.ViewModels.Export;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for GDS export configuration — Python interpreter discovery,
/// Nazca availability, and the Generate-GDS toggle. Reuses the existing
/// singleton <see cref="GdsExportViewModel"/> so changes are visible to every
/// caller that triggers a GDS export (top-toolbar button, save-with-GDS flow).
/// Replaces the former "Python Environment" page plus the right-panel
/// GdsExportPanel; those duplicated the same bindings.
/// </summary>
public class GdsExportSettingsPage : ISettingsPage
{
    /// <inheritdoc/>
    public string Title => "GDS Export";

    /// <inheritdoc/>
    public string Icon => "🐍";

    /// <inheritdoc/>
    public string? Category => "Export";

    /// <inheritdoc/>
    public object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="GdsExportSettingsPage"/>.
    /// </summary>
    public GdsExportSettingsPage(GdsExportViewModel gdsExportViewModel)
    {
        ViewModel = gdsExportViewModel;
    }

    /// <summary>
    /// Navigating to this page refreshes the interpreter list automatically —
    /// no manual "check environment" click needed.
    /// </summary>
    public void OnSelected() =>
        ((GdsExportViewModel)ViewModel).RefreshInterpretersCommand.Execute(null);
}
