using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Layout;
using global::Avalonia.Media;

namespace CAP.Avalonia.Services;

/// <summary>
/// Implementation of IMessageBoxService using Avalonia Window dialogs.
/// </summary>
public class MessageBoxService : IMessageBoxService
{
    /// <summary>
    /// Shows a confirmation dialog with Save/Don't Save/Cancel options.
    /// </summary>
    public async Task<SavePromptResult> ShowSavePromptAsync(string message, string title)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return SavePromptResult.Cancel;

        var mainWindow = desktop.MainWindow;
        if (mainWindow == null)
            return SavePromptResult.Cancel;

        // Create dialog window
        var dialog = new Window
        {
            Title = title,
            Width = 450,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#2d2d2d"))
        };

        var stackPanel = new StackPanel
        {
            Margin = new Thickness(20)
        };

        // Message text
        stackPanel.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 10, 0, 20),
            Foreground = Brushes.White,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14
        });

        // Button panel
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        var saveButton = new Button
        {
            Content = "Save",
            Width = 90,
            Height = 32,
            Background = new SolidColorBrush(Color.Parse("#0d6efd")),
            Foreground = Brushes.White
        };

        var dontSaveButton = new Button
        {
            Content = "Don't Save",
            Width = 90,
            Height = 32,
            Background = new SolidColorBrush(Color.Parse("#6c757d")),
            Foreground = Brushes.White
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90,
            Height = 32,
            Background = new SolidColorBrush(Color.Parse("#3d3d3d")),
            Foreground = Brushes.White
        };

        SavePromptResult? result = null;

        saveButton.Click += (s, e) =>
        {
            result = SavePromptResult.Save;
            dialog.Close();
        };

        dontSaveButton.Click += (s, e) =>
        {
            result = SavePromptResult.DontSave;
            dialog.Close();
        };

        cancelButton.Click += (s, e) =>
        {
            result = SavePromptResult.Cancel;
            dialog.Close();
        };

        buttonPanel.Children.Add(saveButton);
        buttonPanel.Children.Add(dontSaveButton);
        buttonPanel.Children.Add(cancelButton);
        stackPanel.Children.Add(buttonPanel);

        dialog.Content = stackPanel;

        // Focus Save button by default
        dialog.Opened += (s, e) => saveButton.Focus();

        // Show dialog
        await dialog.ShowDialog(mainWindow);

        return result ?? SavePromptResult.Cancel;
    }

    /// <inheritdoc/>
    public async Task<int> ShowChoicePromptAsync(string message, string title, IReadOnlyList<string> buttonLabels)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null)
            return -1;

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#2d2d2d"))
        };

        var stackPanel = new StackPanel { Margin = new Thickness(20) };
        stackPanel.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 10, 0, 20),
            Foreground = Brushes.White,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14
        });

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        var result = -1;
        for (var i = 0; i < buttonLabels.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttonLabels[i],
                Height = 32,
                Padding = new Thickness(12, 0),
                Background = new SolidColorBrush(Color.Parse(i == 0 ? "#0d6efd" : "#3d3d3d")),
                Foreground = Brushes.White
            };
            button.Click += (_, _) => { result = index; dialog.Close(); };
            buttonPanel.Children.Add(button);
        }

        stackPanel.Children.Add(buttonPanel);
        dialog.Content = stackPanel;
        await dialog.ShowDialog(desktop.MainWindow);
        return result;
    }
}
