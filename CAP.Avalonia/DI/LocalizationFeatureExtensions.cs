using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the multi-language UI feature (issue #744): the process-wide
/// <see cref="LocalizationService"/> used by AXAML <c>{loc:Localize}</c> bindings
/// and the Language settings page. Only UI strings are localized — all
/// machine-facing output (GDS/Nazca export, serialization) stays invariant.
/// </summary>
internal static class LocalizationFeatureExtensions
{
    /// <summary>Adds the localization service and the Language settings page.</summary>
    public static IServiceCollection AddLocalizationFeature(this IServiceCollection services)
    {
        // The XAML markup extension must use the same instance as DI consumers.
        services.AddSingleton(LocalizationService.Instance);
        services.AddTransient<LanguageSettingsViewModel>();
        services.AddTransient<ISettingsPage, LanguageSettingsPage>();
        return services;
    }
}
