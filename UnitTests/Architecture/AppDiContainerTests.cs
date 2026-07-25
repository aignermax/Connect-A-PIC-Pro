using CAP.Avalonia;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Properties;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_Core.Solvers.ModeSolver;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace UnitTests.Architecture;

/// <summary>
/// Builds the real production DI container (<see cref="App.ConfigureServices"/>) and
/// resolves the services that the vertical-slice refactor moved into per-feature
/// extension methods. A missing or misplaced registration would otherwise only surface
/// as a crash on app start — no other test exercises the production container
/// (UiScreenshotTests deliberately use a test-only app + VM helper).
///
/// Only POCO/solver/preview services are resolved here: they have no Avalonia-runtime
/// dependency, so they construct cleanly in a headless test.
/// </summary>
public class AppDiContainerTests
{
    [Fact]
    public void Container_ResolvesRedistributedSolverAndPreviewServices()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        OverrideUserPreferencesWithTempFile(services);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IFdtdSMatrixService>().ShouldNotBeNull();
        sp.GetRequiredService<IModeSolverService>().ShouldNotBeNull();
        sp.GetRequiredService<GdsPreviewRenderService>().ShouldNotBeNull();
        sp.GetRequiredService<NazcaComponentPreviewService>().ShouldNotBeNull();
        sp.GetRequiredService<ComponentEditorFactory>().ShouldNotBeNull();
    }

    [Fact]
    public void Container_ResolvesFdtdBackendsAndRegistry()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        OverrideUserPreferencesWithTempFile(services);
        using var sp = services.BuildServiceProvider();

        // The NewComponent editor path keeps the local/free backend.
        sp.GetRequiredService<IFdtdSMatrixService>()
            .ShouldBeOfType<CAP.Avalonia.Services.Solvers.DockerFdtdSMatrixService>();
        sp.GetRequiredService<CAP.Avalonia.Services.Solvers.Tidy3dSMatrixService>().ShouldNotBeNull();

        var registry = sp.GetRequiredService<CAP.Avalonia.Services.Solvers.FdtdBackendRegistry>();
        registry.GetService(FdtdBackendType.MeepDocker)
            .ShouldBeOfType<CAP.Avalonia.Services.Solvers.DockerFdtdSMatrixService>();
        registry.GetService(FdtdBackendType.Tidy3D)
            .ShouldBeOfType<CAP.Avalonia.Services.Solvers.Tidy3dSMatrixService>();
    }

    // Resolving the FDTD services constructs the production UserPreferencesService,
    // whose default path is the REAL user profile (creates directories / may rename
    // the real prefs file). Re-register it against a throwaway temp file — the last
    // registration wins for single-service resolution.
    private static void OverrideUserPreferencesWithTempFile(IServiceCollection services) =>
        services.AddSingleton(new UserPreferencesService(
            Path.Combine(Path.GetTempPath(), $"lunima-di-test-{Guid.NewGuid():N}.json")));
}
