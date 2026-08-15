using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Tests <see cref="ChangeRoutingStyleCommand"/> (issue #862): one command restyles many
/// connections — electrical metal traces included since #854 — and a single undo restores
/// every previous style.
/// </summary>
public class ChangeRoutingStyleCommandTests
{
    [Fact]
    public void Execute_AppliesStyleToAllOpticalConnections()
    {
        var canvas = new DesignCanvasViewModel();
        var a = CreateConnectionVm(MatterType.Light);
        var b = CreateConnectionVm(MatterType.Light);

        var cmd = new ChangeRoutingStyleCommand(canvas, new[] { a, b }, WaveguideType.SBend);
        cmd.Execute();

        a.Connection.Type.ShouldBe(WaveguideType.SBend);
        b.Connection.Type.ShouldBe(WaveguideType.SBend);
        cmd.AffectedCount.ShouldBe(2);
    }

    [Fact]
    public void Execute_RestylesElectricalConnectionsToo()
    {
        var canvas = new DesignCanvasViewModel();
        var optical = CreateConnectionVm(MatterType.Light);
        var electrical = CreateConnectionVm(MatterType.Electricity);

        var cmd = new ChangeRoutingStyleCommand(canvas, new[] { optical, electrical }, WaveguideType.SBend);
        cmd.Execute();

        optical.Connection.Type.ShouldBe(WaveguideType.SBend);
        electrical.Connection.Type.ShouldBe(WaveguideType.SBend);
        cmd.AffectedCount.ShouldBe(2);
    }

    [Fact]
    public void Undo_RestoresEachConnectionsPreviousStyle()
    {
        var canvas = new DesignCanvasViewModel();
        var wasAuto = CreateConnectionVm(MatterType.Light);
        var wasBend = CreateConnectionVm(MatterType.Light);
        wasBend.Connection.Type = WaveguideType.Bend;

        var cmd = new ChangeRoutingStyleCommand(canvas, new[] { wasAuto, wasBend }, WaveguideType.SBend);
        cmd.Execute();
        cmd.Undo();

        wasAuto.Connection.Type.ShouldBe(WaveguideType.Auto);
        wasBend.Connection.Type.ShouldBe(WaveguideType.Bend);
    }

    [Fact]
    public void Execute_SwitchingToAuto_ReleasesFrozenRoute()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = CreateConnectionVm(MatterType.Light);
        vm.Connection.Type = WaveguideType.SBend;
        vm.Connection.IsRouteFrozen = true;

        var cmd = new ChangeRoutingStyleCommand(canvas, new[] { vm }, WaveguideType.Auto);
        cmd.Execute();

        vm.Connection.Type.ShouldBe(WaveguideType.Auto);
        vm.Connection.IsRouteFrozen.ShouldBeFalse();
    }

    [Fact]
    public void Undo_RestoresFrozenImportedRouteGeometry()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = CreateConnectionVm(MatterType.Light);
        var importedPath = new RoutedPath();
        importedPath.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        importedPath.Segments.Add(new StraightSegment(50, 0, 50, 30, 90));
        vm.Connection.RestoreCachedPath(importedPath);
        vm.Connection.IsRouteFrozen = true;

        var cmd = new ChangeRoutingStyleCommand(canvas, new[] { vm }, WaveguideType.SBend);
        cmd.Execute();
        cmd.Undo();

        vm.Connection.Type.ShouldBe(WaveguideType.Auto);
        vm.Connection.IsRouteFrozen.ShouldBeTrue();
        vm.Connection.RoutedPath.ShouldNotBeNull();
        vm.Connection.RoutedPath.Segments.ShouldBe(importedPath.Segments);
    }

    [Fact]
    public void Undo_RestoresManualBendEditsOfStyledRoute()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = CreateConnectionVm(MatterType.Light);
        vm.Connection.Type = WaveguideType.Bend;
        var editedPath = new RoutedPath();
        editedPath.Segments.Add(new StraightSegment(0, 0, 40, 0, 0));
        vm.Connection.RestoreCachedPath(editedPath);
        vm.Connection.IsRouteFrozen = true;
        vm.Connection.BendRadiusOverrides[0] = 25.0;
        vm.Connection.StraightShiftOffsets[0] = 3.5;

        var cmd = new ChangeRoutingStyleCommand(canvas, new[] { vm }, WaveguideType.SBend);
        cmd.Execute();
        cmd.Undo();

        vm.Connection.Type.ShouldBe(WaveguideType.Bend);
        vm.Connection.BendRadiusOverrides.ShouldBe(new Dictionary<int, double> { [0] = 25.0 });
        vm.Connection.StraightShiftOffsets.ShouldBe(new Dictionary<int, double> { [0] = 3.5 });
        vm.Connection.RoutedPath.ShouldNotBeNull();
        vm.Connection.RoutedPath.Segments.ShouldBe(editedPath.Segments);
    }

    [Fact]
    public void CommandManager_BatchChange_IsOneUndoStep()
    {
        var canvas = new DesignCanvasViewModel();
        var manager = new CommandManager();
        var connections = new[]
        {
            CreateConnectionVm(MatterType.Light),
            CreateConnectionVm(MatterType.Light),
            CreateConnectionVm(MatterType.Light),
        };

        manager.ExecuteCommand(new ChangeRoutingStyleCommand(canvas, connections, WaveguideType.Cobra));

        manager.UndoCount.ShouldBe(1);
        manager.Undo().ShouldBeTrue();
        foreach (var vm in connections)
            vm.Connection.Type.ShouldBe(WaveguideType.Auto);
        manager.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void Description_MentionsStyleAndConnectionCount()
    {
        var canvas = new DesignCanvasViewModel();
        var connections = new[] { CreateConnectionVm(MatterType.Light), CreateConnectionVm(MatterType.Light) };

        var cmd = new ChangeRoutingStyleCommand(canvas, connections, WaveguideType.SBend);

        cmd.Description.ShouldContain("SBend");
        cmd.Description.ShouldContain("2");
    }

    private static WaveguideConnectionViewModel CreateConnectionVm(MatterType matterType)
    {
        var connection = new WaveguideConnection
        {
            StartPin = CreatePin(matterType),
            EndPin = CreatePin(matterType),
        };
        return new WaveguideConnectionViewModel(connection);
    }

    private static PhysicalPin CreatePin(MatterType matterType)
    {
        return new PhysicalPin
        {
            Name = "p",
            ParentComponent = CreateComponent(),
            LogicalPin = new Pin("p", 0, matterType, RectSide.Right),
        };
    }

    private static Component CreateComponent()
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: "test",
            rotationCounterClock: DiscreteRotation.R0);
    }
}
