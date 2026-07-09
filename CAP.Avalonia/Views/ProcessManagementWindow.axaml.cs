using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CAP.Avalonia.ViewModels;

namespace CAP.Avalonia.Views;

/// <summary>
/// Window for viewing and adjusting a fabrication process (layer stack,
/// cross-sections, materials). DataContext is a <c>ProcessManagementViewModel</c>.
/// </summary>
public partial class ProcessManagementWindow : Window
{
    /// <summary>Initialises the window.</summary>
    public ProcessManagementWindow()
    {
        InitializeComponent();

        // Design overrides on top of a preset (issue #696): the editor rows are plain DTOs
        // without change notification, so re-diff against the preset baseline whenever a
        // field edit commits (focus leaves the TextBox) and once more when the window closes.
        AddHandler(InputElement.LostFocusEvent, OnAnyControlLostFocus, RoutingStrategies.Bubble);
        Closing += (_, _) => (DataContext as ProcessManagementViewModel)?.RefreshOverrideSummary();
    }

    private void OnAnyControlLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as ProcessManagementViewModel)?.RefreshOverrideSummary();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
