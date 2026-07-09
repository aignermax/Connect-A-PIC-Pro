using System.Net.Http;
using System.Net.Http.Headers;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Update;
using CAP.Avalonia.ViewModels.Update;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the HTTP client and application update feature services.
/// </summary>
internal static class UpdateFeatureExtensions
{
    /// <summary>
    /// Adds a shared <see cref="HttpClient"/>, update checking, and update downloading.
    /// </summary>
    public static IServiceCollection AddUpdateFeature(this IServiceCollection services)
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ConnectAPICPro", "1.0"));
        services.AddSingleton(httpClient);

        services.AddSingleton(sp => new UpdateChecker(
            sp.GetRequiredService<HttpClient>(),
            owner: "aignermax",
            // Repo was renamed Connect-A-PIC-Pro -> Lunima; query the current name directly rather
            // than relying on GitHub's rename redirect (which breaks if the old name is reclaimed).
            repo: "Lunima"));
        services.AddSingleton(sp => new UpdateDownloader(
            sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IUrlLauncher, PlatformShellLauncher>();
        services.AddSingleton<DetachedUpdaterLauncher>();
        services.AddSingleton<IInstaller, SelfUpdateInstaller>();
        services.AddSingleton<UpdateViewModel>();

        return services;
    }
}
