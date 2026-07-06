using CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;
using CAP_Core.ComponentRegistry.RegistryClient;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the photonic component registry feature (issues #655/#656):
/// the read-only <see cref="RegistryClient"/> against the public registry and
/// the registry browser ViewModel shown in the right panel.
/// </summary>
internal static class ComponentRegistryFeatureExtensions
{
    /// <summary>
    /// Adds the registry client (shared <see cref="HttpClient"/>, default
    /// per-user cache, public registry URL) and the
    /// <see cref="RegistryBrowserViewModel"/> as singletons.
    /// </summary>
    public static IServiceCollection AddComponentRegistryFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp => new RegistryClient(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new RegistryBrowserViewModel(sp.GetRequiredService<RegistryClient>()));
        return services;
    }
}
