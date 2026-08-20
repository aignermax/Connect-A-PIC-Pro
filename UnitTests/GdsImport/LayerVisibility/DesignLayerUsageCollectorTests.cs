using CAP.Avalonia.Services.GdsImport.LayerVisibility;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.GdsImport.LayerVisibility;

/// <summary>
/// The layer census behind the Imported Layers panel (issue #858): outline
/// polygons and tagged frozen paths are counted per (layer, datatype) pair,
/// recursing into groups; untagged geometry is ignored.
/// </summary>
public class DesignLayerUsageCollectorTests
{
    [Fact]
    public void Collect_CountsOutlinePolygons_PerPair_Ordered()
    {
        var components = new[]
        {
            LayerVisibilityTestComponents.CreateWithOutlines((11, 0), (1, 0)),
            LayerVisibilityTestComponents.CreateWithOutlines((11, 0)),
        };

        var usages = DesignLayerUsageCollector.Collect(components);

        usages.Select(u => (u.Layer, u.DataType, u.ShapeCount))
            .ShouldBe(new[] { (1, 0, 1), (11, 0, 2) });
    }

    [Fact]
    public void Collect_RecursesIntoGroups_AndCountsTaggedFrozenPaths()
    {
        var group = new ComponentGroup("G");
        group.AddChild(LayerVisibilityTestComponents.CreateWithOutlines((1, 0)));
        group.AddInternalPath(LayerVisibilityTestComponents.CreateFrozenPath(31, 5));

        var usages = DesignLayerUsageCollector.Collect(new[] { (Component)group });

        usages.Select(u => (u.Layer, u.DataType, u.ShapeCount))
            .ShouldBe(new[] { (1, 0, 1), (31, 5, 1) });
    }

    [Fact]
    public void Collect_IgnoresUntaggedFrozenPaths()
    {
        var group = new ComponentGroup("G");
        group.AddInternalPath(LayerVisibilityTestComponents.CreateFrozenPath(null, null));

        DesignLayerUsageCollector.Collect(new[] { (Component)group }).ShouldBeEmpty();
    }

    [Fact]
    public void Collect_ComponentsWithoutOutlines_YieldNoUsages()
    {
        var plain = LayerVisibilityTestComponents.CreateWithOutlines();

        DesignLayerUsageCollector.Collect(new[] { plain }).ShouldBeEmpty();
    }

    [Fact]
    public void Collect_CountsCanvasFrozenPaths_PerPair()
    {
        var paths = new[]
        {
            new CAP.Avalonia.ViewModels.Canvas.CanvasFrozenPathViewModel(
                LayerVisibilityTestComponents.CreateFrozenPath(31, 5)),
            new CAP.Avalonia.ViewModels.Canvas.CanvasFrozenPathViewModel(
                LayerVisibilityTestComponents.CreateFrozenPath(31, 5)),
            new CAP.Avalonia.ViewModels.Canvas.CanvasFrozenPathViewModel(
                LayerVisibilityTestComponents.CreateFrozenPath(11, 0)),
        };

        var usages = DesignLayerUsageCollector.Collect(Array.Empty<Component>(), paths);

        usages.Select(u => (u.Layer, u.DataType, u.ShapeCount))
            .ShouldBe(new[] { (11, 0, 1), (31, 5, 2) });
    }

    [Fact]
    public void Collect_CombinesComponentsAndCanvasFrozenPaths()
    {
        var component = LayerVisibilityTestComponents.CreateWithOutlines((1, 0));
        var path = new CAP.Avalonia.ViewModels.Canvas.CanvasFrozenPathViewModel(
            LayerVisibilityTestComponents.CreateFrozenPath(1, 0));

        var usages = DesignLayerUsageCollector.Collect(new[] { component }, new[] { path });

        usages.Select(u => (u.Layer, u.DataType, u.ShapeCount))
            .ShouldBe(new[] { (1, 0, 2) });
    }

    [Fact]
    public void Collect_IgnoresUntaggedCanvasFrozenPaths()
    {
        var path = new CAP.Avalonia.ViewModels.Canvas.CanvasFrozenPathViewModel(
            LayerVisibilityTestComponents.CreateFrozenPath(null, null));

        DesignLayerUsageCollector.Collect(Array.Empty<Component>(), new[] { path }).ShouldBeEmpty();
    }
}
