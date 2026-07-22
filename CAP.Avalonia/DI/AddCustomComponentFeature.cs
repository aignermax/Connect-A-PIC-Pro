using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the "add custom component → user PDK" feature (issue #656): the per-process
/// <see cref="UserPdkStore"/>, the <see cref="ComponentGeometryExtractor"/> that renders a
/// component reference's geometry via the Nazca and gdsfactory preview services already
/// registered for the per-instance override feature (issue #637, see
/// <see cref="PdkOffsetFeatureExtensions.AddPdkOffsetFeature"/>), and the
/// <see cref="AddCustomComponentDependencies"/> bundle <c>LeftPanelViewModel</c> consumes.
/// </summary>
internal static class AddCustomComponentFeature
{
    /// <summary>Adds the feature's services and its <see cref="AddCustomComponentDependencies"/> bundle.</summary>
    public static IServiceCollection AddAddCustomComponentFeature(this IServiceCollection services)
    {
        services.AddSingleton(_ => UserPdkStore.CreateDefault());
        services.AddSingleton(sp => new ComponentGeometryExtractor(
            new ComponentPreviewRendererAdapter(sp.GetRequiredService<NazcaComponentPreviewService>()),
            new ComponentPreviewRendererAdapter(sp.GetRequiredService<GdsFactoryComponentPreviewService>())));
        services.AddSingleton(sp => new AddCustomComponentDependencies(
            sp.GetRequiredService<ComponentGeometryExtractor>(),
            sp.GetRequiredService<IFdtdSMatrixService>(),
            sp.GetRequiredService<UserPdkStore>(),
            sp.GetService<CAP_Core.ErrorConsoleService>()));
        return services;
    }
}
