using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the PDK offset editor feature: saver, Nazca preview service, and ViewModel.
/// The ViewModel is Singleton so that MainViewModel always receives the same instance —
/// this preserves editor state (loaded PDK, unsaved edits) when the user re-opens the window.
/// </summary>
internal static class PdkOffsetFeatureExtensions
{
    /// <summary>
    /// Adds PDK JSON saving, the Nazca component preview service, and the
    /// <see cref="PdkOffsetEditorViewModel"/> as a singleton.
    /// </summary>
    public static IServiceCollection AddPdkOffsetFeature(this IServiceCollection services)
    {
        services.AddSingleton<PdkJsonSaver>();
        services.AddSingleton(sp =>
        {
            var prefs = sp.GetRequiredService<UserPreferencesService>();
            // Resolution order: validated saved path → nazca-capable discovery
            // (self-correcting a stale saved value) → naive PATH fallback. See
            // PythonResolution for the rationale behind each step.
            var saved = prefs.GetCustomPythonPath();
            var python = PythonResolution.ValidatedNazcaPython(saved);
            if (python == null)
            {
                var discovered = PythonResolution.DiscoverNazcaPython();
                if (discovered != null && !string.IsNullOrEmpty(saved) && discovered != saved)
                    prefs.SetCustomPythonPath(discovered);
                python = discovered ?? PythonResolution.ResolvePythonExecutable();
            }
            var script = PythonResolution.FindScript("render_component_preview.py");
            // Share the DI-registered launch factory so the preview subprocess gets the
            // same platform-aware executable resolution / augmented PATH as everything else.
            return new NazcaComponentPreviewService(
                python, script, launchFactory: sp.GetRequiredService<ProcessLaunchFactory>());
        });
        services.AddSingleton(sp => new PdkOffsetEditorViewModel(
            sp.GetRequiredService<PdkLoader>(),
            sp.GetRequiredService<PdkJsonSaver>(),
            sp.GetRequiredService<PdkManagerViewModel>(),
            sp.GetRequiredService<NazcaComponentPreviewService>()));

        return services;
    }
}
