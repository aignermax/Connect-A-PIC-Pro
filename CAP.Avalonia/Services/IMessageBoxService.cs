namespace CAP.Avalonia.Services;

/// <summary>
/// Result from a message box with Save/Don't Save/Cancel options.
/// </summary>
public enum SavePromptResult
{
    Save,
    DontSave,
    Cancel
}

/// <summary>
/// Service for showing message boxes and confirmation dialogs.
/// </summary>
public interface IMessageBoxService
{
    /// <summary>
    /// Shows a confirmation dialog with Save/Don't Save/Cancel options.
    /// Used when user wants to perform an action that would lose unsaved changes.
    /// </summary>
    /// <param name="message">Message to display</param>
    /// <param name="title">Dialog title</param>
    /// <returns>User's choice</returns>
    Task<SavePromptResult> ShowSavePromptAsync(string message, string title);

    /// <summary>
    /// Shows a dialog with one button per entry in <paramref name="buttonLabels"/>.
    /// </summary>
    /// <param name="message">Message to display.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="buttonLabels">Button captions, left to right.</param>
    /// <returns>Index of the clicked button, or -1 if the dialog was closed.</returns>
    Task<int> ShowChoicePromptAsync(string message, string title, IReadOnlyList<string> buttonLabels);
}
