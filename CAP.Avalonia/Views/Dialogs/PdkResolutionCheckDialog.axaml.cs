using Avalonia.Controls;
using Avalonia.Interactivity;
using CAP.Avalonia.ViewModels.PdkResolution;

namespace CAP.Avalonia.Views.Dialogs;

/// <summary>
/// Dialog window for "Tools → Check PDKs against Python…" (issue #515).
/// DataContext must be set to <see cref="PdkResolutionCheckViewModel"/>.
/// </summary>
public partial class PdkResolutionCheckDialog : Window
{
    /// <summary>Initializes the dialog and bridges the ViewModel's clipboard callback.</summary>
    public PdkResolutionCheckDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PdkResolutionCheckViewModel vm)
                vm.CopyToClipboard = async text =>
                {
                    var clipboard = Clipboard;
                    if (clipboard != null)
                        await clipboard.SetTextAsync(text);
                };
        };
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
