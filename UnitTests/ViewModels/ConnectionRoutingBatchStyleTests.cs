using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Tests the multi-select batch routing-style change (issue #862): with several connections in
/// the rubber-band selection, picking a style in <see cref="ConnectionRoutingViewModel"/>
/// restyles ALL of them as ONE undo step, and a single Ctrl+Z restores every previous style.
/// </summary>
public class ConnectionRoutingBatchStyleTests
{
    [Fact]
    public async Task PickingStyle_WithMultiSelection_RestylesAllConnectionsAsOneUndoStep()
    {
        var (canvas, connections) = await CreateCanvasWithConnectionsAsync(count: 3);
        var manager = new CommandManager();
        var routingVm = new ConnectionRoutingViewModel(canvas, manager);

        foreach (var conn in connections)
            canvas.Selection.SelectedConnections.Add(conn);

        routingVm.SelectedStyle = WaveguideType.SBend;
        await canvas.RecalculateRoutesAsync();

        foreach (var conn in connections)
        {
            conn.Connection.Type.ShouldBe(WaveguideType.SBend);
            conn.Connection.IsRouteFrozen.ShouldBeTrue();
        }
        manager.UndoCount.ShouldBe(1);

        manager.Undo().ShouldBeTrue();
        await canvas.RecalculateRoutesAsync();
        foreach (var conn in connections)
        {
            conn.Connection.Type.ShouldBe(WaveguideType.Auto);
            conn.Connection.IsRouteFrozen.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task MultiSelection_UpdatesTargetCountAndBatchFlag()
    {
        var (canvas, connections) = await CreateCanvasWithConnectionsAsync(count: 2);
        var routingVm = new ConnectionRoutingViewModel(canvas);

        routingVm.TargetConnectionCount.ShouldBe(0);
        routingVm.IsBatchSelection.ShouldBeFalse();

        foreach (var conn in connections)
            canvas.Selection.SelectedConnections.Add(conn);

        routingVm.TargetConnectionCount.ShouldBe(2);
        routingVm.IsBatchSelection.ShouldBeTrue();

        canvas.Selection.ClearConnectionSelection();
        routingVm.TargetConnectionCount.ShouldBe(0);
        routingVm.IsBatchSelection.ShouldBeFalse();
    }

    [Fact]
    public async Task SingleClickedConnection_StillWorks_AndIsUndoable()
    {
        var (canvas, connections) = await CreateCanvasWithConnectionsAsync(count: 1);
        var manager = new CommandManager();
        var routingVm = new ConnectionRoutingViewModel(canvas, manager);

        routingVm.SelectedConnection = connections[0];
        routingVm.SelectedStyle = WaveguideType.Bend;
        await canvas.RecalculateRoutesAsync();

        connections[0].Connection.Type.ShouldBe(WaveguideType.Bend);
        manager.UndoCount.ShouldBe(1);

        manager.Undo();
        connections[0].Connection.Type.ShouldBe(WaveguideType.Auto);
    }

    [Fact]
    public async Task MultiSelection_TakesPrecedenceOverSingleSelectedConnection()
    {
        var (canvas, connections) = await CreateCanvasWithConnectionsAsync(count: 3);
        var routingVm = new ConnectionRoutingViewModel(canvas, new CommandManager());

        // The single selection points at [0]; the batch selection holds [1] and [2].
        routingVm.SelectedConnection = connections[0];
        canvas.Selection.SelectedConnections.Add(connections[1]);
        canvas.Selection.SelectedConnections.Add(connections[2]);

        routingVm.SelectedStyle = WaveguideType.SBend;
        await canvas.RecalculateRoutesAsync();

        connections[0].Connection.Type.ShouldBe(WaveguideType.Auto);
        connections[1].Connection.Type.ShouldBe(WaveguideType.SBend);
        connections[2].Connection.Type.ShouldBe(WaveguideType.SBend);
    }

    [Fact]
    public async Task EffectiveStyleText_ShownForUniformDirectStyles_EmptyForMixedBatch()
    {
        var (canvas, connections) = await CreateCanvasWithConnectionsAsync(count: 2);
        var routingVm = new ConnectionRoutingViewModel(canvas);

        foreach (var conn in connections)
        {
            conn.Connection.RoutedPath!.IsDirectStyledRoute = true;
            conn.Connection.RoutedPath!.DirectStyle = WaveguideType.SBend;
        }

        // Uniform batch: every route resolved to the same direct style — the label is truthful.
        canvas.Selection.SelectedConnections.Add(connections[0]);
        canvas.Selection.SelectedConnections.Add(connections[1]);
        routingVm.EffectiveStyleText.ShouldContain(nameof(WaveguideType.SBend));

        // Mixed batch: one A* route among the targets — no single truthful label, stay empty.
        connections[1].Connection.RoutedPath!.IsDirectStyledRoute = false;
        canvas.Selection.ClearConnectionSelection();
        canvas.Selection.SelectedConnections.Add(connections[0]);
        canvas.Selection.SelectedConnections.Add(connections[1]);
        routingVm.EffectiveStyleText.ShouldBe("");

        // Single selection (the #868 behavior) still shows the effective style.
        canvas.Selection.ClearConnectionSelection();
        routingVm.SelectedConnection = connections[0];
        routingVm.EffectiveStyleText.ShouldContain(nameof(WaveguideType.SBend));
    }

    /// <summary>
    /// Builds a canvas with <paramref name="count"/> vertically stacked connection pairs,
    /// each already routed with the automatic A* route.
    /// </summary>
    private static async Task<(DesignCanvasViewModel canvas, List<WaveguideConnectionViewModel> connections)>
        CreateCanvasWithConnectionsAsync(int count)
    {
        var canvas = new DesignCanvasViewModel();
        var connections = new List<WaveguideConnectionViewModel>();

        for (int i = 0; i < count; i++)
        {
            var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            startComp.WidthMicrometers = 250;
            startComp.HeightMicrometers = 250;
            startComp.PhysicalX = 0;
            startComp.PhysicalY = i * 600;

            var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            endComp.WidthMicrometers = 250;
            endComp.HeightMicrometers = 250;
            endComp.PhysicalX = 400;
            endComp.PhysicalY = i * 600 + 300;

            canvas.AddComponent(startComp);
            canvas.AddComponent(endComp);

            var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
            var endPin = endComp.PhysicalPins.First(p => p.Name == "in");
            var connVm = await canvas.ConnectPinsAsync(startPin, endPin);
            connVm.ShouldNotBeNull();
            connections.Add(connVm!);
        }

        return (canvas, connections);
    }
}
