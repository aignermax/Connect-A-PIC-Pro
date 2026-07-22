using Avalonia.Controls;
using CAP.Avalonia.ViewModels.ComponentSettings;

namespace CAP.Avalonia.Views;

/// <summary>
/// Code-behind for the Component Settings dialog window.
/// </summary>
public partial class ComponentSettingsDialog : Window
{
    /// <summary>Initialises the Component Settings dialog.</summary>
    public ComponentSettingsDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Cancels a running FDTD recompute when the dialog is closed — otherwise the
    /// solve (and its Docker container) would keep running in the background.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is ComponentSettingsDialogViewModel vm)
            vm.CancelRecalculate();
        base.OnClosing(e);
    }
}
