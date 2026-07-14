using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CAP.Avalonia.Views;

/// <summary>
/// Standalone "Create Custom PDK" dialog (issue #729 follow-up design, 2026-07-14): names a
/// new user PDK and gives it a fabrication process, either adopted from an already-loaded
/// process or freshly authored via the embedded process-definition editor. DataContext is a
/// <c>CreateCustomPdkViewModel</c>. The caller (wired in <c>MainWindow.axaml.cs</c>'s
/// "New PDK…" hook) shows this window modally and closes it itself once the view model
/// raises <c>PdkCreated</c>; this class only owns the Cancel affordance.
/// </summary>
public partial class CreateCustomPdkWindow : Window
{
    /// <summary>Initialises the dialog.</summary>
    public CreateCustomPdkWindow()
    {
        InitializeComponent();
    }

    /// <summary>Closes the dialog without creating a PDK.</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
