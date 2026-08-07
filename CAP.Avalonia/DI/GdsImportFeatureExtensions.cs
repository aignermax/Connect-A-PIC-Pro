using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.GdsImport.DesignScope;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Panels;
using CAP_DataAccess.Components.AddCustomComponent;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the GDS import feature: the design-scoped component store
/// (imported components live in the open .lun design, issue #830), the import
/// orchestration service, the canvas placement executor, and the ViewModel
/// behind the import entry points (library-panel button, toolbar button,
/// File→Open .gds route).
/// </summary>
internal static class GdsImportFeatureExtensions
{
    /// <summary>Adds the design-scope store, GDS import service, placement executor, and button ViewModel.</summary>
    public static IServiceCollection AddGdsImportFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var leftPanel = sp.GetRequiredService<LeftPanelViewModel>();
            return new DesignScopedGdsComponentService(
                leftPanel.RegisterDesignScopedPdk,
                leftPanel.RemoveDesignScopedPdk,
                userPdkStore: sp.GetRequiredService<UserPdkStore>());
        });
        services.AddSingleton(sp =>
        {
            var leftPanel = sp.GetRequiredService<LeftPanelViewModel>();
            return new GdsImportService(
                sp.GetRequiredService<DesignScopedGdsComponentService>(),
                () => leftPanel.AllTemplates.ToList());
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
