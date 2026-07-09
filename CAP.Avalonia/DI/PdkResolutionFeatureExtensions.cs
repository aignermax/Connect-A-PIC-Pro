using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.PdkResolution;
using CAP_Core.Export;
using CAP_Core.Export.PdkResolution;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the PDK consistency check feature (issue #515): a Python-subprocess
/// service verifying nazcaFunction strings, plus its dialog ViewModel.
/// </summary>
internal static class PdkResolutionFeatureExtensions
{
    /// <summary>
    /// Adds <see cref="PdkFunctionResolutionService"/> and
    /// <see cref="PdkResolutionCheckViewModel"/>.
    /// </summary>
    public static IServiceCollection AddPdkResolutionFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var prefs = sp.GetRequiredService<UserPreferencesService>();
            // Same Python resolution order as the PDK Offset Editor preview:
            // validated saved path → nazca-capable discovery → naive PATH fallback.
            var python = PythonResolution.ValidatedNazcaPython(prefs.GetCustomPythonPath())
                ?? PythonResolution.DiscoverNazcaPython()
                ?? PythonResolution.ResolvePythonExecutable();
            var script = PythonResolution.FindScript("list_pdk_resolution.py");
            return new PdkFunctionResolutionService(
                python, script, launchFactory: sp.GetRequiredService<ProcessLaunchFactory>());
        });
        services.AddTransient(sp => new PdkResolutionCheckViewModel(
            sp.GetRequiredService<PdkLoader>(),
            sp.GetRequiredService<PdkFunctionResolutionService>()));
        return services;
    }
}
