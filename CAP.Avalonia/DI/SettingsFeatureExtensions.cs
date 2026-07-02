using CAP.Avalonia.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers settings page implementations and the settings window ViewModel.
/// To add a new settings page: add one <c>AddTransient&lt;ISettingsPage, YourPage&gt;()</c> here.
/// </summary>
internal static class SettingsFeatureExtensions
{
    /// <summary>
    /// Adds all <see cref="ISettingsPage"/> implementations and the
    /// <see cref="SettingsWindowViewModel"/> that enumerates them.
    /// </summary>
    public static IServiceCollection AddSettingsFeature(this IServiceCollection services)
    {
        services.AddTransient<ISettingsPage, GeneralSettingsPage>();
        services.AddTransient<ISettingsPage, GridSnapSettingsPage>();
        services.AddTransient<ISettingsPage, UpdateSettingsPage>();
        services.AddTransient<ISettingsPage, GdsExportSettingsPage>();
        services.AddTransient<ISettingsPage, PythonEnvironmentsSettingsPage>();
        services.AddTransient<ISettingsPage, ChipSizeSettingsPage>();
        services.AddTransient<ISettingsPage, AiAssistantSettingsPage>();
        services.AddTransient<SettingsWindowViewModel>();

        return services;
    }
}
