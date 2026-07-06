using Avalonia.Controls.Notifications;

namespace CAP.Avalonia.Services.Notifications;

/// <summary>
/// Default <see cref="INotificationService"/> backed by an Avalonia
/// <see cref="INotificationManager"/> (in production a
/// <see cref="WindowNotificationManager"/> attached to the main window).
///
/// The manager only exists once the main window is loaded, so the service is
/// created detached: toasts raised before <see cref="Attach"/> are buffered
/// (up to <see cref="MaxPendingNotifications"/>) and flushed on attach.
/// Must be called from the UI thread, like all Avalonia controls.
/// </summary>
public class NotificationService : INotificationService
{
    /// <summary>Maximum toasts buffered while no manager is attached.</summary>
    public const int MaxPendingNotifications = 10;

    /// <summary>How long a toast stays visible before auto-dismissing.</summary>
    public static readonly TimeSpan DefaultExpiration = TimeSpan.FromSeconds(5);

    private const string DefaultTitle = "Lunima";

    private readonly List<Notification> _pending = new();
    private Action<Notification>? _show;

    /// <summary>
    /// Connects the service to a live notification manager and flushes any
    /// toasts that were raised before the main window existed.
    /// </summary>
    /// <param name="manager">The window-attached manager that renders toasts.</param>
    public void Attach(INotificationManager manager) => Attach(manager.Show);

    /// <summary>
    /// Delegate-based overload of <see cref="Attach(INotificationManager)"/>.
    /// Avalonia's <see cref="INotificationManager"/> cannot be implemented by
    /// user code, so tests attach a recording delegate instead.
    /// </summary>
    /// <param name="show">Callback invoked once per toast to render it.</param>
    public void Attach(Action<Notification> show)
    {
        _show = show;
        foreach (var notification in _pending)
            show(notification);
        _pending.Clear();
    }

    /// <inheritdoc />
    public void ShowInfo(string message, string? title = null) =>
        Show(message, title, NotificationType.Information);

    /// <inheritdoc />
    public void ShowSuccess(string message, string? title = null) =>
        Show(message, title, NotificationType.Success);

    /// <inheritdoc />
    public void ShowWarning(string message, string? title = null) =>
        Show(message, title, NotificationType.Warning);

    private void Show(string message, string? title, NotificationType type)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var notification = new Notification(
            title ?? DefaultTitle, message, type, DefaultExpiration);

        if (_show != null)
        {
            _show(notification);
            return;
        }

        if (_pending.Count < MaxPendingNotifications)
            _pending.Add(notification);
    }
}
