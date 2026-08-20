using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.Services;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Canvas;

/// <summary>
/// Tests for the per-connection process bend-floor wiring (issue #937): at the start of
/// every routing pass the <see cref="RoutingOrchestrator"/> builds the pass-scoped provider
/// through <see cref="RoutingOrchestrator.BuildConnectionProcessFloorProvider"/> and pushes
/// it onto the router, so the router floors each connection by its own endpoints' process.
/// An unwired factory clears the provider and the canvas-wide floor governs.
/// </summary>
public class RoutingOrchestratorProcessFloorTests
{
    [Fact]
    public async Task RecalculateRoutesAsync_FactoryWired_PushesProviderOntoTheRouter()
    {
        var router = new WaveguideRouter();
        var orchestrator = CreateOrchestrator(router);
        orchestrator.BuildConnectionProcessFloorProvider = () => (_, _) => 30.0;

        await orchestrator.RecalculateRoutesAsync();

        router.ConnectionProcessFloorProvider.ShouldNotBeNull();
        router.ConnectionProcessFloorProvider!(null!, null!).ShouldBe(30.0);
    }

    [Fact]
    public async Task RecalculateRoutesAsync_FactoryNotWired_ClearsStaleProvider()
    {
        var router = new WaveguideRouter
        {
            ConnectionProcessFloorProvider = (_, _) => 5.0,
        };
        var orchestrator = CreateOrchestrator(router);

        await orchestrator.RecalculateRoutesAsync();

        router.ConnectionProcessFloorProvider.ShouldBeNull();
    }

    [Fact]
    public async Task RecalculateRoutesAsync_FactoryReturnsNull_ClearsProvider()
    {
        var router = new WaveguideRouter
        {
            ConnectionProcessFloorProvider = (_, _) => 5.0,
        };
        var orchestrator = CreateOrchestrator(router);
        orchestrator.BuildConnectionProcessFloorProvider = () => null;

        await orchestrator.RecalculateRoutesAsync();

        router.ConnectionProcessFloorProvider.ShouldBeNull();
    }

    private static RoutingOrchestrator CreateOrchestrator(WaveguideRouter router) =>
        new(router,
            new WaveguideConnectionManager(router),
            new ObservableCollection<ComponentViewModel>(),
            new ObservableCollection<WaveguideConnectionViewModel>());
}
