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
    /// <summary>
    /// Adds the crossing-insertion binder, restored to the user's last saved choice and
    /// wired to persist further toggles. The binder must be materialized once at startup —
    /// see <see cref="App.ActivateStartupSingletons"/>, which resolves it explicitly instead
    /// of relying on some other constructor to reference it incidentally.
    /// </summary>
    public static IServiceCollection AddCrossingInsertionFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var binder = new CrossingInsertionCanvasBinder(
                sp.GetRequiredService<DesignCanvasViewModel>(),
                () => CreateCrossingInstance(sp));

            // Restore the user's last choice, then persist any further toggle (Settings →
            // Routing → Crossings) via the same PropertyChanged convention used for the
            // canvas' other Routing-settings flags (see AddCanvasAndPanels) — the checkbox
            // otherwise silently reverted to off on every app restart.
            var preferences = sp.GetRequiredService<UserPreferencesService>();
            binder.IsEnabled = preferences.GetCrossingInsertionEnabled();
            binder.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CrossingInsertionCanvasBinder.IsEnabled))
                    preferences.SetCrossingInsertionEnabled(binder.IsEnabled);
            };

            return binder;
        });
        return services;
    }

    /// <summary>
    /// Instantiates a fresh crossing component through the production PDK path.
    /// Returns null while no crossing template is loaded (e.g. PDK disabled) —
    /// the crossing pass then keeps detours instead of guessing.
    /// </summary>
    private static CrossingComponentInstance? CreateCrossingInstance(IServiceProvider sp)
    {
        return CrossingComponentInstance.CreateFromTemplates(
            sp.GetRequiredService<LeftPanelViewModel>().AllTemplates);
    }
}
