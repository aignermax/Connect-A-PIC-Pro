using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Layout;
using global::Avalonia.Media;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.Services;

/// <summary>
/// Implementation of IInputDialogService using Avalonia Window dialogs.
/// </summary>
public class InputDialogService : IInputDialogService
{
    /// <summary>
    /// Shows a text input dialog.
    /// </summary>
    public async Task<string?> ShowInputDialogAsync(
        string title,
        string prompt,
        string? defaultValue = null)
    {
        var result = await ShowMultiInputDialogAsync(
            title,
            (prompt, defaultValue ?? ""));

        return result?[prompt];
    }

    /// <summary>
    /// Shows a dialog with multiple input fields.
    /// </summary>
    public async Task<Dictionary<string, string>?> ShowMultiInputDialogAsync(
        string title,
        params (string label, string defaultValue)[] fields)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        var mainWindow = desktop.MainWindow;
        if (mainWindow == null)
            return null;

        // Create dialog window
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = fields.Length * 60 + 100,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var stackPanel = new StackPanel
        {
            Margin = new Thickness(20)
        };

        var textBoxes = new List<TextBox>();

        // Add input fields
        foreach (var (label, defaultValue) in fields)
        {
            stackPanel.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 10, 0, 5),
                Foreground = Brushes.White
            });

            var textBox = new TextBox
            {
                Text = defaultValue,
                Watermark = string.Format(LocalizationService.Instance.Translate("Dialog.EnterWatermark"), label.ToLower()),
                Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3e3e3e"))
            };

            textBoxes.Add(textBox);
            stackPanel.Children.Add(textBox);
        }

        // Add buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            Spacing = 10
        };

        var okButton = new Button
        {
            Content = LocalizationService.Instance.Translate("Common.Ok"),
            Width = 80,
            Background = new SolidColorBrush(Color.Parse("#0d6efd")),
            Foreground = Brushes.White
        };

        var cancelButton = new Button
        {
            Content = LocalizationService.Instance.Translate("Common.Cancel"),
            Width = 80,
            Background = new SolidColorBrush(Color.Parse("#3d3d3d")),
            Foreground = Brushes.White
        };

        bool? dialogResult = null;

        okButton.Click += (s, e) =>
        {
            dialogResult = true;
            dialog.Close();
        };

        cancelButton.Click += (s, e) =>
        {
            dialogResult = false;
            dialog.Close();
        };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        stackPanel.Children.Add(buttonPanel);

        dialog.Content = stackPanel;

        // Focus first textbox and select all text
        dialog.Opened += (s, e) =>
        {
            if (textBoxes.Count > 0)
            {
                textBoxes[0].Focus();
                textBoxes[0].SelectAll();
            }
        };

        // Show dialog
        await dialog.ShowDialog(mainWindow);

        // Return results
        if (dialogResult == true)
        {
            var results = new Dictionary<string, string>();
            for (int i = 0; i < fields.Length; i++)
            {
                results[fields[i].label] = textBoxes[i].Text ?? "";
            }
            return results;
        }

        return null;
    }
}
