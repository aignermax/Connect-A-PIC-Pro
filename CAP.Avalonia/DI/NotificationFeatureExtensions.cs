using CAP.Avalonia.Services.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the transient toast/notification feature (issue #586): a single
/// <see cref="NotificationService"/> that MainWindow attaches its
/// WindowNotificationManager to on load.
/// </summary>
internal static class NotificationFeatureExtensions
{
    /// <summary>Adds <see cref="INotificationService"/> backed by one shared <see cref="NotificationService"/>.</summary>
    public static IServiceCollection AddNotificationFeature(this IServiceCollection services)
    {
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        return services;
    }
}
