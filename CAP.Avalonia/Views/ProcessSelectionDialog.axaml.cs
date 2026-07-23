using Avalonia.Controls;
using Avalonia.Input;
using CAP.Avalonia.ViewModels.Process;

namespace CAP.Avalonia.Views;

/// <summary>
/// New-Design process-selection dialog (issue #570): lets the user pick a derived
/// fabrication process, or Playground, before starting a new design. A single
/// click on an entry confirms it and closes the dialog (issue #778); Escape
/// cancels and Enter confirms the keyboard selection. The caller awaits
/// <see cref="Window.ShowDialog(Window)"/> and then reads
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
    /// Confirms the clicked choice immediately (issue #778): selects it,
    /// writes the view model's <c>Result</c> and closes the dialog.
    /// </summary>
    private void OnChoiceTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProcessSelectionViewModel vm)
            return;
        if (sender is not Control { DataContext: ProcessChoiceItem item })
            return;

        vm.SelectedChoice = item;
        ConfirmAndClose(vm);
    }

    /// <summary>Escape cancels (Result stays null); Enter confirms the keyboard selection.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && DataContext is ProcessSelectionViewModel vm && vm.CanConfirm)
        {
            e.Handled = true;
            ConfirmAndClose(vm);
        }
    }

    /// <summary>Executes the confirm command and closes the dialog so the caller can read <c>Result</c>.</summary>
    private void ConfirmAndClose(ProcessSelectionViewModel vm)
    {
        if (!vm.ConfirmCommand.CanExecute(null))
            return;

        vm.ConfirmCommand.Execute(null);
        Close();
    }
}
