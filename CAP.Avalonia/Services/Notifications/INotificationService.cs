namespace CAP.Avalonia.Services.Notifications;

/// <summary>
/// Shows transient, auto-dismissing toast notifications for informational,
/// non-error events (e.g. "FDTD recompute cancelled", "S-matrix applied").
/// Actual errors and warnings that need to persist belong in the error
/// console, not here.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows a neutral informational toast.</summary>
    /// <param name="message">Body text of the toast.</param>
    /// <param name="title">Optional headline; a sensible default is used when null.</param>
    void ShowInfo(string message, string? title = null);

    /// <summary>Shows a success toast (completed action feedback).</summary>
    /// <param name="message">Body text of the toast.</param>
    /// <param name="title">Optional headline; a sensible default is used when null.</param>
    void ShowSuccess(string message, string? title = null);

    /// <summary>
    /// Shows a transient warning toast. Use only for minor, self-explanatory
    /// hiccups — persistent problems should go to the error console instead.
    /// </summary>
    /// <param name="message">Body text of the toast.</param>
    /// <param name="title">Optional headline; a sensible default is used when null.</param>
    void ShowWarning(string message, string? title = null);
}
