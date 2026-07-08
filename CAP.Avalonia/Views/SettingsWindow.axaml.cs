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
    /// "Manage in Python Environments →" on the GDS-Export page: navigates to the
    /// Python-Environments page, the single place where interpreters (managed
    /// environments + discovered system Pythons) are listed and activated (issue #645).
    /// </summary>
    private void OnManageInterpretersClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.SelectPage(typeof(PythonEnvironmentsSettingsPage));
    }
}
