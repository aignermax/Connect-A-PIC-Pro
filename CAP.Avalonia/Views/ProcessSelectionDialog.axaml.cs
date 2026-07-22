using Avalonia.Controls;
using Avalonia.Interactivity;
using CAP.Avalonia.ViewModels.Process;

namespace CAP.Avalonia.Views;

/// <summary>
/// New-Design process-selection dialog (issue #570): lets the user pick a derived
/// fabrication process, or Playground, before starting a new design. The caller
/// awaits <see cref="Window.ShowDialog(Window)"/> and then reads
/// <see cref="ProcessSelectionViewModel.Result"/> off the bound view model; the
/// dialog carries no return value of its own.
/// </summary>
public partial class ProcessSelectionDialog : Window
{
    /// <summary>Initializes the process-selection dialog.</summary>
    public ProcessSelectionDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Confirms the currently selected choice into the view model's <c>Result</c>,
    /// then closes the dialog so the caller can read it.
    /// </summary>
    private void OnStartDesignClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProcessSelectionViewModel vm || !vm.ConfirmCommand.CanExecute(null))
            return;

        vm.ConfirmCommand.Execute(null);
        Close();
    }

    /// <summary>Closes the dialog without confirming a selection; Result stays null.</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
