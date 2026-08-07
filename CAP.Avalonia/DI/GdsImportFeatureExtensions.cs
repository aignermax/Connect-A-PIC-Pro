using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Panels;
using CAP_DataAccess.Components.AddCustomComponent;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the GDS import feature: the import orchestration service, the
/// canvas placement executor, and the ViewModel behind the import entry points
/// (library-panel button, toolbar button, File→Open .gds route). Reuses the
/// <see cref="UserPdkStore"/> singleton from
/// <see cref="AddCustomComponentFeature.AddAddCustomComponentFeature"/> so
/// imported PDK definitions land in the same store the component library reads.
/// </summary>
internal static class GdsImportFeatureExtensions
{
    /// <summary>Adds the GDS import service, placement executor, and button ViewModel.</summary>
    public static IServiceCollection AddGdsImportFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var leftPanel = sp.GetRequiredService<LeftPanelViewModel>();
            return new GdsImportService(
                sp.GetRequiredService<UserPdkStore>(),
                () => leftPanel.AllTemplates.ToList(),
                // Lambda, not the method group: the optional savedViaBundledFork
                // parameter keeps it from matching the 3-argument Action directly.
                (draft, pdkName, filePath) => leftPanel.RegisterSavedCustomComponent(draft, pdkName, filePath),
                leftPanel.BeginBatchRegistration);
        });
        services.AddSingleton(sp =>
        {
            var leftPanel = sp.GetRequiredService<LeftPanelViewModel>();
            return new GdsPlacementExecutor(
                sp.GetRequiredService<DesignCanvasViewModel>(),
                sp.GetRequiredService<Commands.CommandManager>(),
                () => leftPanel.AllTemplates.ToList());
        });
        services.AddSingleton(sp => new GdsImportButtonViewModel(
            sp.GetRequiredService<GdsImportService>(),
            sp.GetRequiredService<GdsPlacementExecutor>(),
            sp.GetService<CAP_Core.ErrorConsoleService>()));
        return services;
    }
}
