using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CAP.Avalonia.Views;

public partial class CreateCustomPdkWindow : Window
{
    public CreateCustomPdkWindow()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
