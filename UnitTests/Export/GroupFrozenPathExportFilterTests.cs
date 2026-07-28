using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// A frozen waveguide path inside a <see cref="ComponentGroup"/> must be filtered by the same
/// <see cref="ExportableConnections.IsExportable(RoutedPath?)"/> predicate as a live connection
/// — freezing (grouping) a placeholder/invalid connection must not bypass the export filter,
/// and the skip must still be reported. Mirrors
/// <c>SimpleNazcaExporterSkipsBrokenRoutesTests</c>/<c>GdsFactoryExporterSkipsBrokenRoutesTests</c>
/// for live connections.
/// </summary>
public class GroupFrozenPathExportFilterTests
{
    [Fact]
    public void NazcaExport_GroupWithPlaceholderFrozenPath_OmitsItAndReportsSkip()
    {
        var canvas = CanvasWithGroup(out _);

        var skipped = new List<string>();
        var script = new SimpleNazcaExporter().Export(canvas, skippedConnections: skipped);

        script.ShouldContain("nd.strt(length=200.00");     // valid frozen path (Child1 -> Child2)
        script.ShouldNotContain("nd.strt(length=300.00");  // placeholder frozen path (Child3 -> Child4)
        skipped.Count.ShouldBe(1);
        skipped[0].ShouldBe("Child3.out → Child4.in");
    }

    [Fact]
    public void GdsFactoryExport_GroupWithPlaceholderFrozenPath_OmitsItAndReportsSkip()
    {
        var canvas = CanvasWithGroup(out _);

        var skipped = new List<string>();
        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            skippedConnections: skipped);

        script.ShouldContain("gf.components.straight(length=200.00");
        script.ShouldNotContain("gf.components.straight(length=300.00");
        skipped.Count.ShouldBe(1);
        skipped[0].ShouldBe("Child3.out → Child4.in");
    }

    [Fact]
    public void NazcaExport_GroupWithOnlyValidFrozenPaths_NothingSkipped()
    {
        var canvas = CanvasWithGroup(out var group, secondPathIsPlaceholder: false);

        var skipped = new List<string>();
        var script = new SimpleNazcaExporter().Export(canvas, skippedConnections: skipped);

        script.ShouldContain("nd.strt(length=200.00");
        script.ShouldContain("nd.strt(length=300.00");
        skipped.ShouldBeEmpty();
        group.InternalPaths.Count.ShouldBe(2);
    }

    [Fact]
    public void NazcaExport_GroupWithZeroSegmentFrozenPath_ExportsPinToPinFallback()
    {
        // CreateGroupCommand freezes a connection that was never routed as `new RoutedPath()`
        // (0 segments, not null) — must render the same pin-to-pin fallback an ungrouped
        // routeless connection gets, not vanish silently.
        var canvas = CanvasWithZeroSegmentFrozenPath();

        var skipped = new List<string>();
        var script = new SimpleNazcaExporter().Export(canvas, skippedConnections: skipped);

        script.ShouldContain("ic.sbend_p2p");
        skipped.ShouldBeEmpty();
    }

    [Fact]
    public void GdsFactoryExport_GroupWithZeroSegmentFrozenPath_ExportsPinToPinFallback()
    {
        var canvas = CanvasWithZeroSegmentFrozenPath();

        var skipped = new List<string>();
        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            skippedConnections: skipped);

        script.ShouldContain("# routeless connection: direct pin-to-pin straight");
        skipped.ShouldBeEmpty();
    }

    private static DesignCanvasViewModel CanvasWithZeroSegmentFrozenPath()
    {
        var group = new ComponentGroup("Group");
        var child1 = ChildAt("Child1", 0, "out");
        var child2 = ChildAt("Child2", 200, "in");
        group.AddChild(child1);
        group.AddChild(child2);

        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = new RoutedPath(),
            StartPin = child1.PhysicalPins.Single(),
            EndPin = child2.PhysicalPins.Single(),
        });

        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(group));
        return canvas;
    }

    /// <summary>
    /// A group with 4 children and two frozen internal paths: Child1→Child2 (valid, pins
    /// 200µm apart) and Child3→Child4 (placeholder by default, pins 300µm apart) — distinct
    /// lengths let each frozen path's exported line be identified unambiguously. Both
    /// exporters' single-straight-segment formatter reads the PIN positions directly rather
    /// than the stored segment, so the crafted segment's own coordinates are irrelevant here
    /// (mirrors <c>SimpleNazcaExporterSkipsBrokenRoutesTests</c>' live-connection helper).
    /// </summary>
    private static DesignCanvasViewModel CanvasWithGroup(
        out ComponentGroup group, bool secondPathIsPlaceholder = true)
    {
        group = new ComponentGroup("Group");
        var child1 = ChildAt("Child1", 0, "out");
        var child2 = ChildAt("Child2", 200, "in");
        var child3 = ChildAt("Child3", 1000, "out");
        var child4 = ChildAt("Child4", 1300, "in");
        group.AddChild(child1);
        group.AddChild(child2);
        group.AddChild(child3);
        group.AddChild(child4);

        var validPath = new RoutedPath();
        validPath.Segments.Add(new StraightSegment(0, 0, 1, 0, 0));
        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = validPath,
            StartPin = child1.PhysicalPins.Single(),
            EndPin = child2.PhysicalPins.Single(),
        });

        var secondPath = new RoutedPath { IsPlaceholderGeometry = secondPathIsPlaceholder };
        secondPath.Segments.Add(new StraightSegment(0, 0, 1, 0, 0));
        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = secondPath,
            StartPin = child3.PhysicalPins.Single(),
            EndPin = child4.PhysicalPins.Single(),
        });

        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(group));
        return canvas;
    }

    private static Component ChildAt(string identifier, double x, string pinName)
    {
        var comp = TestComponentFactory.CreateBasicComponent();
        comp.Identifier = identifier;
        comp.PhysicalX = x;
        comp.PhysicalY = 0;
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = pinName,
            ParentComponent = comp,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = 0,
        });
        return comp;
    }
}
