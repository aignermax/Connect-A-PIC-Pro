using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;

namespace CAP.Avalonia.Views;

public partial class NewComponentWindow : Window
{
    public NewComponentWindow()
    {
        InitializeComponent();
    }

    private void OnPreviewThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is NewComponentViewModel vm && vm.PreviewBitmap is { } bitmap)
        {
            new ComponentPreviewWindow(bitmap).Show(this);
        }
    }

    private void OnCopyGdsFactoryExample(object? sender, RoutedEventArgs e) => CopyToClipboard(GdsFactoryExampleBox.Text);

    private void OnCopyNazcaExample(object? sender, RoutedEventArgs e) => CopyToClipboard(NazcaExampleBox.Text);

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
