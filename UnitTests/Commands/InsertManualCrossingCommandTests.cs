using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Tests for <see cref="InsertManualCrossingCommand"/>: executing
/// splits a connection into two halves docked onto a centered crossing, undo
/// restores the original connection object with its fine-tuning intact.
/// </summary>
public class InsertManualCrossingCommandTests
{
    private const double IntersectionX = 200.0;
    private const double IntersectionY = 100.0;
    private const double FineTunedBendRadius = 25.0;

    [Fact]
    public void Execute_SplitsConnectionAndCentersCrossingOnIntersection()
    {
        var (canvas, command, original, crossing) = CreateArrangedCommand();

        command.Execute();

        canvas.Components.ShouldHaveSingleItem().Component.ShouldBeSameAs(crossing.Component);
        double half = CrossingTestCircuit.CrossingEdgeMicrometers / 2.0;
        crossing.Component.PhysicalX.ShouldBe(IntersectionX - half, 1e-9);
        crossing.Component.PhysicalY.ShouldBe(IntersectionY - half, 1e-9);

        canvas.ConnectionManager.Connections.ShouldNotContain(original);
        canvas.ConnectionManager.Connections.Count.ShouldBe(2);
        canvas.Connections.Count.ShouldBe(2);
    }

    [Fact]
    public void Execute_DocksHalvesOntoWestAndEastPortsForLeftToRightTravel()
    {
        var (canvas, command, original, crossing) = CreateArrangedCommand();

        command.Execute();

        var subs = canvas.ConnectionManager.Connections;
        subs[0].StartPin.ShouldBeSameAs(original.StartPin);
        subs[0].EndPin.Name.ShouldBe("port 1");
        subs[1].StartPin.Name.ShouldBe("port 2");
        subs[1].EndPin.ShouldBeSameAs(original.EndPin);
        subs[0].EndPin.ParentComponent.ShouldBeSameAs(crossing.Component);
    }

    [Fact]
    public void Execute_CopiesWaveguideSettingsOntoBothHalves()
    {
        var (canvas, command, original, _) = CreateArrangedCommand();
        original.WidthMicrometers = 0.8;

        command.Execute();

        foreach (var sub in canvas.ConnectionManager.Connections)
        {
            sub.WidthMicrometers.ShouldBe(0.8);
            sub.BendRadiusMicrometers.ShouldBe(FineTunedBendRadius);
        }
    }

    [Fact]
    public void Undo_RestoresSameOriginalConnectionObjectWithFineTuning()
    {
        var (canvas, command, original, _) = CreateArrangedCommand();
        original.BendRadiusOverrides[0] = 42.0;

        command.Execute();
        command.Undo();

        canvas.Components.ShouldBeEmpty();
        var restored = canvas.ConnectionManager.Connections.ShouldHaveSingleItem();
        restored.ShouldBeSameAs(original);
        restored.BendRadiusMicrometers.ShouldBe(FineTunedBendRadius);
        restored.BendRadiusOverrides[0].ShouldBe(42.0);
        canvas.Connections.ShouldHaveSingleItem().Connection.ShouldBeSameAs(original);
    }

    [Fact]
    public void ExecuteUndoRedo_CycleIsStable()
    {
        var (canvas, command, original, _) = CreateArrangedCommand();

        command.Execute();
        command.Undo();
        command.Execute();

        canvas.Components.Count.ShouldBe(1);
        canvas.ConnectionManager.Connections.Count.ShouldBe(2);
        canvas.ConnectionManager.Connections.ShouldNotContain(original);

        command.Undo();
        canvas.Components.ShouldBeEmpty();
        canvas.ConnectionManager.Connections.ShouldHaveSingleItem().ShouldBeSameAs(original);
    }

    [Fact]
    public void Execute_CrossingWithoutRequiredPorts_IsSafeNoOp()
    {
        var canvas = new DesignCanvasViewModel();
        var original = CreateOriginalConnection(canvas);
        var portlessTerminal = CrossingTestCircuit.CreateTerminal("noports", 0, 0, 0);
        var instance = new CrossingComponentInstance(portlessTerminal.Component, "Broken", "Test PDK");

        var command = new InsertManualCrossingCommand(canvas, CreateCandidate(original), instance);
        command.Execute();

        canvas.Components.ShouldBeEmpty();
        canvas.ConnectionManager.Connections.ShouldHaveSingleItem().ShouldBeSameAs(original);
    }

    private static (DesignCanvasViewModel Canvas, InsertManualCrossingCommand Command,
        WaveguideConnection Original, CrossingComponentInstance Crossing) CreateArrangedCommand()
    {
        var canvas = new DesignCanvasViewModel();
        var original = CreateOriginalConnection(canvas);
        var crossing = new CrossingComponentInstance(
            CrossingTestCircuit.CreateCrossingComponent(), "Crossing 4-Port", "Test PDK");
        var command = new InsertManualCrossingCommand(canvas, CreateCandidate(original), crossing);
        return (canvas, command, original, crossing);
    }

    /// <summary>Horizontal net (10,100)→(390,100), registered with the canvas.</summary>
    private static WaveguideConnection CreateOriginalConnection(DesignCanvasViewModel canvas)
    {
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var connection = new WaveguideConnection
        {
            StartPin = left.PhysicalPin,
            EndPin = right.PhysicalPin,
            BendRadiusMicrometers = FineTunedBendRadius,
        };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(10, 100, 390, 100, 0));
        connection.RestoreCachedPath(path);

        canvas.ConnectionManager.AddExistingConnection(connection);
        canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
        return connection;
    }

    private static ManualCrossingCandidate CreateCandidate(WaveguideConnection connection)
    {
        var segment = (StraightSegment)connection.GetPathSegments()[0];
        var guideTerminal = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        var guide = new PinGuideLine(
            guideTerminal.PhysicalPin, (IntersectionX, 50.0), (0.0, 1.0), IsHorizontal: false);
        return new ManualCrossingCandidate(
            connection, segment, guide, (IntersectionX, IntersectionY),
            SegmentIsHorizontal: true, SegmentDirection: (1.0, 0.0));
    }
}
