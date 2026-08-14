using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Regression tests for issue #856: ungrouping a group that carries pin-less frozen
/// paths (GDS-imported route geometry) must transfer them to the canvas-level
/// frozen-path store instead of discarding them, with full undo/redo identity.
/// Also covers the delete/move commands operating on that store.
/// </summary>
public class CanvasFrozenPathUngroupTests
{
    [Fact]
    public void Ungroup_PinLessFrozenPath_TransfersToCanvasStore()
    {
        var canvas = CanvasWithGroup(out var group, out _);

        new UngroupCommand(canvas, group).Execute();

        canvas.CanvasFrozenPaths.Count.ShouldBe(1);
        var released = canvas.CanvasFrozenPaths[0].Path;
        released.StartPin.ShouldBeNull();
        released.EndPin.ShouldBeNull();
        released.Layer.ShouldBe(31);
        released.DataType.ShouldBe(5);
        released.Path.Segments.Count.ShouldBe(4);
        released.Path.Segments[0].StartPoint.ShouldBe((0.0, 0.0));
        canvas.Components.ShouldNotContain(c => c.Component == group);
        // The group keeps its original path so Undo can restore it untouched.
        group.InternalPaths.Count.ShouldBe(1);
    }

    [Fact]
    public void Ungroup_PinLessFrozenPath_ReleasedGeometryIsIndependentOfGroupOriginal()
    {
        var canvas = CanvasWithGroup(out var group, out var originalPath);

        new UngroupCommand(canvas, group).Execute();
        canvas.CanvasFrozenPaths[0].Path.TranslateBy(100, 50);

        // Moving the released copy must not corrupt the group's stored original.
        originalPath.Path.Segments[0].StartPoint.ShouldBe((0.0, 0.0));
    }

    [Fact]
    public void Ungroup_ThenUndo_RemovesReleasedPathAndRestoresGroup()
    {
        var canvas = CanvasWithGroup(out var group, out _);
        var cmd = new UngroupCommand(canvas, group);

        cmd.Execute();
        cmd.Undo();

        canvas.CanvasFrozenPaths.ShouldBeEmpty();
        canvas.Components.ShouldContain(c => c.Component == group);
        group.InternalPaths.Count.ShouldBe(1);
    }

    [Fact]
    public void Ungroup_UndoRedo_ReusesSameReleasedViewModelInstance()
    {
        var canvas = CanvasWithGroup(out var group, out _);
        var cmd = new UngroupCommand(canvas, group);

        cmd.Execute();
        var releasedVm = canvas.CanvasFrozenPaths[0];
        cmd.Undo();
        cmd.Execute();

        canvas.CanvasFrozenPaths.Count.ShouldBe(1);
        canvas.CanvasFrozenPaths[0].ShouldBeSameAs(releasedVm);
    }

    [Fact]
    public void Ungroup_HalfPinnedFrozenPath_StaysWithGroupNotTransferred()
    {
        var canvas = new DesignCanvasViewModel();
        var group = new ComponentGroup("Group");
        var child = CreateChild("splitter_1x2");
        group.AddChild(child);
        var halfPinned = new FrozenWaveguidePath
        {
            Path = RingPath(),
            StartPin = new PhysicalPin { Name = "in", ParentComponent = child },
            EndPin = null,
        };
        group.AddInternalPath(halfPinned);
        canvas.Components.Add(new ComponentViewModel(group));

        new UngroupCommand(canvas, group).Execute();

        canvas.CanvasFrozenPaths.ShouldBeEmpty();
        canvas.Connections.ShouldBeEmpty();
    }

    [Fact]
    public void DeleteCommand_RemovesPath_UndoReAddsSameInstance()
    {
        var canvas = new DesignCanvasViewModel();
        var pathVm = new CanvasFrozenPathViewModel(PinLessPath()) { IsSelected = true };
        canvas.CanvasFrozenPaths.Add(pathVm);
        var cmd = new DeleteCanvasFrozenPathCommand(canvas, pathVm);

        cmd.Execute();
        canvas.CanvasFrozenPaths.ShouldBeEmpty();
        pathVm.IsSelected.ShouldBeFalse();

        cmd.Undo();
        canvas.CanvasFrozenPaths.Count.ShouldBe(1);
        canvas.CanvasFrozenPaths[0].ShouldBeSameAs(pathVm);
    }

    [Fact]
    public void MoveCommand_FirstExecuteIsNoOp_UndoRedoTranslate()
    {
        var pathVm = new CanvasFrozenPathViewModel(PinLessPath());
        // The live drag already moved the geometry before the command is recorded.
        pathVm.Path.TranslateBy(5, 3);
        var cmd = new MoveCanvasFrozenPathCommand(pathVm, 5, 3);

        cmd.Execute();
        pathVm.Path.Path.Segments[0].StartPoint.ShouldBe((5.0, 3.0));

        cmd.Undo();
        pathVm.Path.Path.Segments[0].StartPoint.ShouldBe((0.0, 0.0));

        cmd.Execute();
        pathVm.Path.Path.Segments[0].StartPoint.ShouldBe((5.0, 3.0));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DesignCanvasViewModel CanvasWithGroup(
        out ComponentGroup group, out FrozenWaveguidePath pinLessPath)
    {
        var canvas = new DesignCanvasViewModel();
        group = new ComponentGroup("ImportGroup");
        group.AddChild(CreateChild("splitter_1x2"));
        pinLessPath = PinLessPath();
        group.AddInternalPath(pinLessPath);
        canvas.Components.Add(new ComponentViewModel(group));
        return canvas;
    }

    private static FrozenWaveguidePath PinLessPath() => new()
    {
        Path = RingPath(),
        StartPin = null,
        EndPin = null,
        Layer = 31,
        DataType = 5,
    };

    /// <summary>A closed 10×1 µm rectangle outline — the shape GDS import traces
    /// from a top-cell route polygon.</summary>
    private static RoutedPath RingPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        path.Segments.Add(new StraightSegment(10, 0, 10, 1, 90));
        path.Segments.Add(new StraightSegment(10, 1, 0, 1, 180));
        path.Segments.Add(new StraightSegment(0, 1, 0, 0, -90));
        return path;
    }

    private static Component CreateChild(string nazcaFunctionName)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: nazcaFunctionName,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: nazcaFunctionName,
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
        };
    }
}
