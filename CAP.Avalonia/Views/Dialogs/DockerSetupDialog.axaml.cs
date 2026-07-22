using Avalonia.Controls;
using Avalonia.Interactivity;
using CAP.Avalonia.ViewModels.Solvers.DockerSetup;

namespace CAP.Avalonia.Views.Dialogs;

/// <summary>
/// Modal "Set up FDTD" dialog shown when Docker is missing or its engine is
/// stopped. Wires the ViewModel's clipboard callback to the window's clipboard
/// and closes with <c>true</c> when the re-check reports Docker available.
/// </summary>
public partial class DockerSetupDialog : Window
{
    public DockerSetupDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not DockerSetupViewModel vm)
                return;
            vm.CopyToClipboard = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(text);
            };
            vm.CloseRequested += (_, _) => Close(vm.IsDockerAvailable);
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
