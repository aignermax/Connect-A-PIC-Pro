using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Unit tests for the batched ComponentGroup mutation methods
/// (AddChildren / RemoveChildren / AddInternalPaths) that replace per-item
/// calls in bulk operations such as group edit mode at GDS-import scale.
/// They must behave exactly like their per-item counterparts.
/// </summary>
public class ComponentGroupBulkMutationTests
{
    private static Component CreateChildAt(double x, double y)
    {
        var child = TestComponentFactory.CreateBasicComponent();
        child.PhysicalX = x;
        child.PhysicalY = y;
        child.WidthMicrometers = 100;
        child.HeightMicrometers = 100;
        return child;
    }

    private static FrozenWaveguidePath CreatePathBetween(double sx, double sy, double ex, double ey)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
        return new FrozenWaveguidePath { Path = path };
    }

    [Fact]
    public void AddChildren_SetsParentAndUpdatesBoundsOnce()
    {
        var group = new ComponentGroup("Bulk") { PhysicalX = 0, PhysicalY = 0 };
        var children = new[] { CreateChildAt(0, 0), CreateChildAt(300, 0), CreateChildAt(0, 300) };

        group.AddChildren(children);

        group.ChildComponents.Count.ShouldBe(3);
        foreach (var child in children)
            child.ParentGroup.ShouldBe(group);
        group.WidthMicrometers.ShouldBe(400);
        group.HeightMicrometers.ShouldBe(400);
    }

    [Fact]
    public void AddChildren_DuplicateChild_Throws()
    {
        var group = new ComponentGroup("Bulk");
        var child = CreateChildAt(0, 0);
        group.AddChild(child);

        Should.Throw<InvalidOperationException>(() => group.AddChildren(new[] { child }));
    }

    [Fact]
    public void AddChildren_EmptyCollection_IsNoOp()
    {
        var group = new ComponentGroup("Bulk");
        group.AddChild(CreateChildAt(0, 0));

        group.AddChildren(Array.Empty<Component>());

        group.ChildComponents.Count.ShouldBe(1);
    }

    [Fact]
    public void RemoveChildren_ClearsParentAndUpdatesBounds()
    {
        var group = new ComponentGroup("Bulk") { PhysicalX = 0, PhysicalY = 0 };
        var keep = CreateChildAt(0, 0);
        var remove1 = CreateChildAt(300, 0);
        var remove2 = CreateChildAt(0, 300);
        group.AddChildren(new[] { keep, remove1, remove2 });

        group.RemoveChildren(new[] { remove1, remove2 });

        group.ChildComponents.ShouldBe(new[] { keep });
        remove1.ParentGroup.ShouldBeNull();
        remove2.ParentGroup.ShouldBeNull();
        keep.ParentGroup.ShouldBe(group);
        group.WidthMicrometers.ShouldBe(100);
        group.HeightMicrometers.ShouldBe(100);
    }

    [Fact]
    public void RemoveChildren_NonChildEntries_AreIgnored()
    {
        var group = new ComponentGroup("Bulk");
        var child = CreateChildAt(0, 0);
        group.AddChild(child);

        group.RemoveChildren(new[] { CreateChildAt(50, 50) });

        group.ChildComponents.ShouldBe(new[] { child });
    }

    [Fact]
    public void AddInternalPaths_AddsAllAndIncludesGeometryInBounds()
    {
        var group = new ComponentGroup("Bulk") { PhysicalX = 0, PhysicalY = 0 };
        group.AddChild(CreateChildAt(0, 0));

        group.AddInternalPaths(new[]
        {
            CreatePathBetween(0, 0, 500, 0),
            CreatePathBetween(0, 0, 0, 500)
        });

        group.InternalPaths.Count.ShouldBe(2);
        // Straight-segment bounds include 2 µm waveguide-width padding on each side.
        group.WidthMicrometers.ShouldBe(504);
        group.HeightMicrometers.ShouldBe(504);
    }

    [Fact]
    public void AddInternalPaths_EmptyCollection_IsNoOp()
    {
        var group = new ComponentGroup("Bulk");
        group.AddChild(CreateChildAt(0, 0));

        group.AddInternalPaths(Array.Empty<FrozenWaveguidePath>());

        group.InternalPaths.ShouldBeEmpty();
        group.WidthMicrometers.ShouldBe(100);
    }
}
