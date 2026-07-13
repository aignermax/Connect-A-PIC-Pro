using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;

namespace CAP.Avalonia.Views;

/// <summary>
/// Window for authoring a new PDK component: render its geometry, optionally recompute its
/// S-matrix via FDTD, and save it into a fabrication process's user PDK. DataContext must be a
/// <see cref="CAP.Avalonia.ViewModels.Components.AddCustomComponent.NewComponentViewModel"/>.
/// </summary>
public partial class NewComponentWindow : Window
{
    /// <summary>Initializes the window.</summary>
    public NewComponentWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the rendered preview in a zoom/pan popup (<see cref="ComponentPreviewWindow"/>) when
    /// the thumbnail is clicked. No-ops if a preview hasn't rendered yet (null-tolerant).
    /// </summary>
    private void OnPreviewThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is NewComponentViewModel vm && vm.PreviewBitmap is { } bitmap)
        {
            new ComponentPreviewWindow(bitmap).Show(this);
        }
    }

    /// <summary>Copies the help flyout's gdsfactory example code to the clipboard.</summary>
    private void OnCopyGdsFactoryExample(object? sender, RoutedEventArgs e) => CopyToClipboard(GdsFactoryExampleBox.Text);

    /// <summary>Copies the help flyout's Nazca example code to the clipboard.</summary>
    private void OnCopyNazcaExample(object? sender, RoutedEventArgs e) => CopyToClipboard(NazcaExampleBox.Text);

    /// <summary>Writes <paramref name="text"/> to the OS clipboard, if any and non-empty.</summary>
    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            _ = clipboard.SetTextAsync(text);
        }
    }
}
