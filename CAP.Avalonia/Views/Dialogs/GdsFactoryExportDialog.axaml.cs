using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CAP.Avalonia.Views.Dialogs;

/// <summary>Options dialog for the gdsfactory export (#581).</summary>
public partial class GdsFactoryExportDialog : Window
{
    /// <summary>Initializes a new instance of <see cref="GdsFactoryExportDialog"/>.</summary>
    public GdsFactoryExportDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
