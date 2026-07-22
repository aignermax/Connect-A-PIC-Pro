using CAP.Avalonia.Services.Localization;
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
public class GdsExportSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "🐍";

    /// <inheritdoc/>
    public override string? Category => "Export";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="GdsExportSettingsPage"/>.
    /// </summary>
    public GdsExportSettingsPage(GdsExportViewModel gdsExportViewModel, LocalizationService localization)
        : base("Settings.Section.GdsExport", localization)
    {
        ViewModel = gdsExportViewModel;
    }

    /// <summary>
    /// Navigating to this page refreshes the interpreter list automatically —
    /// no manual "check environment" click needed.
    /// </summary>
    public override void OnSelected() =>
        ((GdsExportViewModel)ViewModel).RefreshInterpretersCommand.Execute(null);
}
