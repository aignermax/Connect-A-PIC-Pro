using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the adaptive crossing-insertion feature (Issue #553): the
/// <see cref="CrossingInsertionCanvasBinder"/> attaches a crossing-insertion
/// service to the design canvas whose factory instantiates the real
/// "Crossing 4-Port" (ebeam_crossing4) PDK component from the loaded library.
/// </summary>
internal static class CrossingInsertionFeatureExtensions
{
    /// <summary>Nazca function name of the PDK crossing component used for insertion.</summary>
    private const string CrossingNazcaFunction = "ebeam_crossing4";

    /// <summary>
    /// Adds the crossing-insertion binder. The binder is activated by
    /// <see cref="ViewModels.MainViewModel"/> so the wiring exists as soon as
    /// the application composes its root view-model.
    /// </summary>
    public static IServiceCollection AddCrossingInsertionFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var binder = new CrossingInsertionCanvasBinder(
                sp.GetRequiredService<DesignCanvasViewModel>(),
                () => CreateCrossingInstance(sp));

            // Restore the user's last choice, then persist any further toggle (Settings →
            // Routing → Crossings) — the checkbox otherwise silently reverted to off on
            // every app restart.
            var preferences = sp.GetRequiredService<UserPreferencesService>();
            binder.IsEnabled = preferences.GetCrossingInsertionEnabled();
            binder.EnabledChanged += preferences.SetCrossingInsertionEnabled;

            return binder;
        });
        return services;
    }

    /// <summary>
    /// Instantiates a fresh crossing component through the production PDK path
    /// (PDK JSON → <see cref="ComponentTemplate"/> → <see cref="ComponentTemplates.CreateFromTemplate"/>).
    /// Returns null while no crossing template is loaded (e.g. PDK disabled) —
    /// the crossing pass then keeps detours instead of guessing.
    /// </summary>
    private static CrossingComponentInstance? CreateCrossingInstance(IServiceProvider sp)
    {
        var templates = sp.GetRequiredService<LeftPanelViewModel>().AllTemplates;
        var template = templates.FirstOrDefault(t => string.Equals(
            t.NazcaFunctionName, CrossingNazcaFunction, StringComparison.OrdinalIgnoreCase));
        if (template == null) return null;

        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        return new CrossingComponentInstance(component, template.Name, template.PdkSource);
    }
}
