using System.Diagnostics;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit.Abstractions;

namespace UnitTests.ViewModels;

/// <summary>
/// Scale guard for group edit mode at GDS-import scale:
/// entering/leaving edit mode on a group with hundreds of children and
/// hundreds of segment-rich frozen paths must stay interactive.
/// </summary>
public class GroupEditModeScaleTests
{
    private const int ChildCount = 160;
    private const int SegmentsPerPath = 30;

    private readonly ITestOutputHelper _output;

    public GroupEditModeScaleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Builds an import-scale group: <see cref="ChildCount"/> two-pin children laid out
    /// in a grid, with two frozen multi-segment paths between each consecutive pair
    /// (≈ 2 × children internal paths) plus pin-less GDS route outlines.
    /// </summary>
    private static ComponentGroup CreateImportScaleGroup()
    {
        var group = new ComponentGroup("ImportedTop") { PhysicalX = 0, PhysicalY = 0 };
        var children = new List<Component>(ChildCount);

        const int columns = 16;
        const double pitchX = 300;
        const double pitchY = 300;

        for (int i = 0; i < ChildCount; i++)
        {
            var child = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins($"scale_{i}");
            child.PhysicalX = (i % columns) * pitchX;
            child.PhysicalY = (i / columns) * pitchY;
            children.Add(child);
            group.AddChild(child);
        }

        for (int i = 0; i + 1 < ChildCount; i++)
        {
            AddFrozenPath(group, children[i].PhysicalPins[2], children[i + 1].PhysicalPins[0]);
            AddFrozenPath(group, children[i].PhysicalPins[3], children[i + 1].PhysicalPins[1]);
        }

        // Pin-less GDS-imported route outlines (no live connections).
        for (int i = 0; i < 40; i++)
        {
            group.AddInternalPath(new FrozenWaveguidePath
            {
                Path = CreateZigZagPath(i * 10, 0, i * 10 + 200, 100)
            });
        }

        return group;
    }

    private static void AddFrozenPath(ComponentGroup group, PhysicalPin startPin, PhysicalPin endPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        group.AddInternalPath(new FrozenWaveguidePath
        {
            StartPin = startPin,
            EndPin = endPin,
            Path = CreateZigZagPath(sx, sy, ex, ey)
        });
    }

    /// <summary>Creates a segment-rich straight-segment path between two points.</summary>
    private static RoutedPath CreateZigZagPath(double sx, double sy, double ex, double ey)
    {
        var path = new RoutedPath();
        double stepX = (ex - sx) / SegmentsPerPath;
        double stepY = (ey - sy) / SegmentsPerPath;
        for (int s = 0; s < SegmentsPerPath; s++)
        {
            path.Segments.Add(new StraightSegment(
                sx + s * stepX, sy + s * stepY,
                sx + (s + 1) * stepX, sy + (s + 1) * stepY, 0));
        }
        return path;
    }

    [Fact]
    public void EnterAndExitGroupEditMode_ImportScaleGroup_CompletesQuickly()
    {
        var canvas = new DesignCanvasViewModel();
        // Real MainWindow wiring: the hierarchy panel rebuilds its tree on every
        // Components change — per-item adds made this quadratic at import scale.
        var hierarchy = new CAP.Avalonia.ViewModels.Hierarchy.HierarchyPanelViewModel(canvas);
        var group = CreateImportScaleGroup();
        canvas.AddComponent(group);

        var enterWatch = Stopwatch.StartNew();
        canvas.EnterGroupEditMode(group);
        enterWatch.Stop();

        canvas.Components.Count.ShouldBe(ChildCount);
        int liveConnections = canvas.Connections.Count;

        var exitWatch = Stopwatch.StartNew();
        canvas.ExitGroupEditMode();
        exitWatch.Stop();

        _output.WriteLine($"Enter: {enterWatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Exit:  {exitWatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Live connections in edit mode: {liveConnections}");

        // Behavior must be preserved
        canvas.IsInGroupEditMode.ShouldBeFalse();
        canvas.Components.Count.ShouldBe(1);
        group.ChildComponents.Count.ShouldBe(ChildCount);
        group.InternalPaths.Count.ShouldBe(2 * (ChildCount - 1) + 40);

        // Interactivity guard: generous CI budget — the pre-fix
        // per-item quadratics took far longer than this at import scale.
        enterWatch.ElapsedMilliseconds.ShouldBeLessThan(10_000);
        exitWatch.ElapsedMilliseconds.ShouldBeLessThan(10_000);
    }
}
