using Avalonia.Controls;
using CAP.Avalonia.ViewModels.Settings;

namespace CAP.Avalonia.Views;

/// <summary>
/// Settings window that hosts the settings registry navigation panel
/// and renders the selected <see cref="ISettingsPage"/> content area.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Initializes a new instance of <see cref="SettingsWindow"/>.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// "Create + install Nazca now" on the GDS-Export page: starts the default
    /// install (via the GdsExportViewModel delegate) and navigates to the
    /// Python-Environments page so the progress is visible there.
    /// </summary>
    private void OnCreateNazcaEnvironmentClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is ViewModels.Export.GdsExportViewModel gdsExport)
            gdsExport.InstallNazcaCommand.Execute(null);

        if (DataContext is SettingsWindowViewModel vm)
            vm.SelectPage(typeof(PythonEnvironmentsSettingsPage));
    }
}
