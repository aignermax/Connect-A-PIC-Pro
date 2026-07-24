using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Round-5 review [3]: UI-thread commands (ungroup, undo/redo) mutate
/// <see cref="WaveguideConnectionManager.Connections"/> while a fire-and-forget routing
/// pass enumerates it on a background thread. Before the fix the enumeration threw
/// "Collection was modified" and the pass died with an unobserved exception, leaving
/// stale/unrouted waveguides on the canvas. The manager now locks all list mutations
/// and routes over a snapshot.
/// </summary>
public class ConnectionManagerConcurrencyTests
{
    [Fact]
    public async Task RoutingPass_SurvivesConcurrentConnectionMutations()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter())
        {
            // No pathfinding grid in this test — RecalculateAllTransmissions takes the
            // simple branch, which iterates the connection list exactly like the
            // incremental pass does. The race is the same: enumerate vs. Add/Remove.
            UseSequentialRouting = false
        };
        var comp1 = CreateComponentWithPin("C1", 100, 100, pinOffsetX: 50, pinAngleDegrees: 0);
        var comp2 = CreateComponentWithPin("C2", 300, 100, pinOffsetX: 0, pinAngleDegrees: 180);
        var pin1 = comp1.PhysicalPins[0];
        var pin2 = comp2.PhysicalPins[0];

        // Base load so every pass has a non-trivial enumeration window.
        for (int i = 0; i < 25; i++)
            manager.AddConnectionDeferred(pin1, pin2);

        using var cts = new CancellationTokenSource();
        var routingLoop = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
                manager.RecalculateAllTransmissions(cancellationToken: cts.Token);
        });

        // "UI thread": keep adding/removing while the routing loop enumerates.
        var deadline = DateTime.UtcNow.AddMilliseconds(750);
        while (DateTime.UtcNow < deadline)
        {
            var conn = manager.AddConnectionDeferred(pin1, pin2);
            manager.RemoveConnectionDeferred(conn);
        }
        cts.Cancel();

        // Pre-fix this rethrows InvalidOperationException ("Collection was modified").
        await routingLoop;
        manager.Connections.Count.ShouldBe(25);
    }

    private static Component CreateComponentWithPin(
        string identifier, double x, double y, double pinOffsetX, double pinAngleDegrees)
    {
        var sMatrix = new SMatrix(new List<Guid>(), new List<(Guid sliderID, double value)>());
        var pins = new List<PhysicalPin>
        {
            new()
            {
                Name = "Pin1",
                OffsetXMicrometers = pinOffsetX,
                OffsetYMicrometers = 15,
                AngleDegrees = pinAngleDegrees
            }
        };

        return new Component(
            new Dictionary<int, SMatrix> { { 1550, sMatrix } },
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            identifier,
            new DiscreteRotation(),
            pins)
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 50,
            HeightMicrometers = 30
        };
    }
}
