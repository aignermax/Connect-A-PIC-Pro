using CAP.Avalonia;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.ViewModels.Properties;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_Core.Solvers.ModeSolver;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Architecture;

/// <summary>
/// Builds the real production DI container (<see cref="App.ConfigureServices"/>) and
/// resolves the services that the vertical-slice refactor moved into per-feature
/// extension methods. A missing or misplaced registration would otherwise only surface
/// as a crash on app start. Other tests exercise the same container via
/// <see cref="ProductionContainerTestHelper"/> for narrower composition-root guarantees
/// (e.g. <c>RoutingSettingsPersistenceTests</c>); <c>UiScreenshotTests</c> deliberately use
/// a test-only app + VM helper instead.
///
/// Only POCO/solver/preview services are resolved here: they have no Avalonia-runtime
/// dependency, so they construct cleanly in a headless test.
/// </summary>
public class AppDiContainerTests
{
    private static string NewTempPreferencesPath() =>
        Path.Combine(Path.GetTempPath(), $"lunima-di-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void Container_ResolvesRedistributedSolverAndPreviewServices()
    {
        using var sp = ProductionContainerTestHelper.BuildWithTempPreferences(NewTempPreferencesPath());

        sp.GetRequiredService<IFdtdSMatrixService>().ShouldNotBeNull();
        sp.GetRequiredService<IModeSolverService>().ShouldNotBeNull();
        sp.GetRequiredService<GdsPreviewRenderService>().ShouldNotBeNull();
        sp.GetRequiredService<NazcaComponentPreviewService>().ShouldNotBeNull();
        sp.GetRequiredService<ComponentEditorFactory>().ShouldNotBeNull();
    }

    [Fact]
    public void Container_ResolvesFdtdBackendsAndRegistry()
    {
        using var sp = ProductionContainerTestHelper.BuildWithTempPreferences(NewTempPreferencesPath());

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
}
