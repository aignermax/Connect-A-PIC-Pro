using CAP.Avalonia.Services.ComponentRegistry;
using CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.ComponentRegistry.RegistryClient;
using CAP_DataAccess.Components.AddCustomComponent;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the photonic component registry feature (issues #655/#656/#773):
/// the read-only <see cref="RegistryClient"/> against the public registry, the
/// <see cref="RegistryDownloadService"/> adopting registry components into a
/// local process-bound user PDK, and the browser ViewModel hosted in the
/// "Component Registry" tool window.
/// </summary>
internal static class ComponentRegistryFeatureExtensions
{
    /// <summary>
    /// Adds the registry client (shared <see cref="HttpClient"/>, default
    /// per-user cache, public registry URL), the download service and the
    /// <see cref="RegistryBrowserViewModel"/> as singletons.
    /// </summary>
    public static IServiceCollection AddComponentRegistryFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp => new RegistryClient(sp.GetRequiredService<HttpClient>()));
        // The library registration callback resolves LeftPanelViewModel LAZILY:
        // LeftPanel's ctor itself takes the registry browser VM, so an eager
        // resolution here would be a circular dependency.
        services.AddSingleton(sp => new RegistryDownloadService(
            sp.GetRequiredService<RegistryClient>(),
            sp.GetRequiredService<UserPdkStore>(),
            path => sp.GetRequiredService<LeftPanelViewModel>().RegisterCreatedPdk(path)));
        services.AddSingleton(sp => new RegistryBrowserViewModel(
            sp.GetRequiredService<RegistryClient>(),
            sp.GetRequiredService<RegistryDownloadService>()));
        return services;
    }
}
