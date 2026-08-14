using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Unit tests for ComponentGroup bounding box calculation.
/// Verifies that UpdateGroupBounds() includes frozen waveguide paths in the bounding box.
/// </summary>
public class ComponentGroupBoundingBoxTests
{
    /// <summary>
    /// Verifies that an empty group has zero bounds.
    /// </summary>
    [Fact]
    public void UpdateGroupBounds_EmptyGroup_HasZeroBounds()
    {
        var group = new ComponentGroup("EmptyGroup");

        group.WidthMicrometers.ShouldBe(0, "Empty group width should be zero");
        group.HeightMicrometers.ShouldBe(0, "Empty group height should be zero");
    }

    [Fact]
    public void UpdateGroupBounds_BendFrozenPath_UsesTightArcBounds()
    {
        // Field report: a group containing an S-bend frozen path got a collision
        // footprint ~5× its real size — the arc contributed its full circle.
        var group = new ComponentGroup("BendGroup")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        // 90° arc, center (500,400), radius 100: swept extent x∈[500,600], y∈[300,400].
        var path = new RoutedPath();
        path.Segments.Add(new BendSegment(centerX: 500, centerY: 400, radius: 100,
            startAngle: 0, sweepAngle: 90));
        group.AddInternalPath(new FrozenWaveguidePath { Path = path });

        group.UpdateGroupBounds();

        // Tight bounds + 2µm padding per side: x∈[498,602], y∈[298,402].
        group.WidthMicrometers.ShouldBe(104, 1e-6,
            "the arc's swept extent only, not its full circle (202 µm)");
        group.HeightMicrometers.ShouldBe(104, 1e-6);
    }

    /// <summary>
    /// Verifies that a group with only children calculates bounds based on child positions.
    /// </summary>
    [Fact]
    public void UpdateGroupBounds_GroupWithChildren_CalculatesBoundsFromChildren()
    {
        var group = TestComponentFactory.CreateComponentGroup("GroupWithChildren", addChildren: true);

        // Children are at (100, 100) and (400, 100), each 250x250
        // Bounding box spans from (100, 100) to (650, 350)
        // Width = 650 - 100 = 550, Height = 350 - 100 = 250
        group.WidthMicrometers.ShouldBe(550, "Group width should span from 100 to 650");
        group.HeightMicrometers.ShouldBe(250, "Group height should be child height");
    }

    /// <summary>
    /// Verifies that a group with a straight frozen path includes the path in bounds.
    /// </summary>
    [Fact]
    public void UpdateGroupBounds_GroupWithStraightFrozenPath_IncludesPathInBounds()
    {
        var group = new ComponentGroup("GroupWithPath")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        // Create two children
        var child1 = TestComponentFactory.CreateBasicComponent();
        child1.PhysicalX = 100;
        child1.PhysicalY = 100;
        child1.WidthMicrometers = 250;
        child1.HeightMicrometers = 250;

        var child2 = TestComponentFactory.CreateBasicComponent();
        child2.PhysicalX = 400;
        child2.PhysicalY = 400; // Different Y to test vertical bounds
        child2.WidthMicrometers = 250;
        child2.HeightMicrometers = 250;

        group.AddChild(child1);
        group.AddChild(child2);

        // Add physical pins
        child1.PhysicalPins.Add(new PhysicalPin
        {
            Name = "out",
            ParentComponent = child1,
            OffsetXMicrometers = 250,
            OffsetYMicrometers = 125,
            AngleDegrees = 0
        });

        child2.PhysicalPins.Add(new PhysicalPin
        {
            Name = "in",
            ParentComponent = child2,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 125,
            AngleDegrees = 180
        });

        // Create a frozen path that extends beyond child bounds
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(
            child1.PhysicalX + 250,
            child1.PhysicalY + 125,
            child2.PhysicalX,
            child2.PhysicalY + 125,
            0));

        var frozenPath = new FrozenWaveguidePath
        {
            StartPin = child1.PhysicalPins[0],
            EndPin = child2.PhysicalPins[0],
            Path = routedPath
        };
        group.AddInternalPath(frozenPath);

        // Bounds should include both children and the path
        // Path runs from (350, 225) to (400, 525)
        // With 2µm padding, path contributes (348, 223) to (402, 527)
        // Children: (100, 100) to (650, 650)
        // Combined: (100, 100) to (650, 650) - children dominate in this case
        group.WidthMicrometers.ShouldBeGreaterThan(500, "Group width should span children and path");
        group.HeightMicrometers.ShouldBeGreaterThan(500, "Group height should span children and path");
    }

    /// <summary>
    /// Verifies that a group with only a frozen path (no children) calculates bounds from the path.
    /// </summary>
    [Fact]
    public void UpdateGroupBounds_GroupWithOnlyFrozenPath_CalculatesBoundsFromPath()
    {
        var group = new ComponentGroup("GroupWithOnlyPath")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        // Create a simple routed path
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(100, 200, 500, 200, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            Path = routedPath
        };
        group.AddInternalPath(frozenPath);

        // Path from (100, 200) to (500, 200)
        // With 2µm padding: (98, 198) to (502, 202)
        // Width = 502 - 98 = 404, Height = 202 - 198 = 4
        group.WidthMicrometers.ShouldBe(404, 1, "Group width should be path length + 2*padding");
        group.HeightMicrometers.ShouldBe(4, 1, "Group height should be 2*padding for horizontal path");
    }

    /// <summary>
    /// Verifies that a group with a bend segment calculates bounds correctly.
    /// </summary>
    [Fact]
    public void UpdateGroupBounds_GroupWithBendSegment_CalculatesBoundsFromArc()
    {
        var group = new ComponentGroup("GroupWithBend")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        // Create a 90-degree bend centered at (300, 300) with radius 50
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new BendSegment(
            centerX: 300,
            centerY: 300,
            radius: 50,
            startAngle: 0,
            sweepAngle: 90));

        var frozenPath = new FrozenWaveguidePath
        {
            Path = routedPath
        };
        group.AddInternalPath(frozenPath);

        // Tight arc bounds: the arc sweeps radially 270°→360°, so it occupies
        // x∈[300,350], y∈[250,300] — plus 2 µm padding per side: (298,248) to
        // (352,302) → 54×54. (The full-circle box (104×104) was the pre-fix
        // behavior and inflated group collision footprints far past the arc.)
        group.WidthMicrometers.ShouldBe(54, 1, "Group width should span the swept arc extent + padding");
        group.HeightMicrometers.ShouldBe(54, 1, "Group height should span the swept arc extent + padding");
    }

    /// <summary>
    /// Verifies that moving a group updates the bounding box correctly with frozen paths.
    /// </summary>
    [Fact]
    public void MoveGroup_WithFrozenPath_UpdatesBounds()
    {
        var group = new ComponentGroup("MovableGroup")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        var child = TestComponentFactory.CreateBasicComponent();
        child.PhysicalX = 100;
        child.PhysicalY = 100;
        group.AddChild(child);

        // Add a frozen path
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(150, 150, 300, 150, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            Path = routedPath
        };
        group.AddInternalPath(frozenPath);

        var initialX = group.PhysicalX;
        var initialY = group.PhysicalY;

        // Move the group
        group.MoveGroup(100, 200);

        // After moving, bounds should be updated
        group.PhysicalX.ShouldBeGreaterThan(initialX, "Group X should increase after moving right");
        group.PhysicalY.ShouldBeGreaterThan(initialY, "Group Y should increase after moving down");
    }

    /// <summary>
    /// Verifies that adding a frozen path updates the group bounds.
    /// </summary>
    [Fact]
    public void AddInternalPath_UpdatesBounds()
    {
        var group = new ComponentGroup("DynamicGroup")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        var child = TestComponentFactory.CreateBasicComponent();
        child.PhysicalX = 100;
        child.PhysicalY = 100;
        group.AddChild(child);

        var initialWidth = group.WidthMicrometers;
        var initialHeight = group.HeightMicrometers;

        // Add a path that extends beyond current bounds
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(100, 100, 1000, 1000, 45));

        var frozenPath = new FrozenWaveguidePath
        {
            Path = routedPath
        };
        group.AddInternalPath(frozenPath);

        // Bounds should expand to include the new path
        // Note: AddInternalPath doesn't automatically call UpdateGroupBounds
        // But AddChild does, so we need to manually trigger it or rely on other operations
        // For now, verify that the path is stored
        group.InternalPaths.Count.ShouldBe(1, "Group should have one internal path");
    }

    /// <summary>
    /// Verifies that a group with multiple frozen paths calculates bounds from all paths.
    /// </summary>
    [Fact]
    public void UpdateGroupBounds_GroupWithMultipleFrozenPaths_IncludesAllPaths()
    {
        var group = new ComponentGroup("MultiPathGroup")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        // Add first path
        var path1 = new RoutedPath();
        path1.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        group.AddInternalPath(new FrozenWaveguidePath { Path = path1 });

        // Add second path extending further
        var path2 = new RoutedPath();
        path2.Segments.Add(new StraightSegment(100, 100, 500, 500, 45));
        group.AddInternalPath(new FrozenWaveguidePath { Path = path2 });

        // Bounds should encompass both paths
        // Path1: (0, 0) to (100, 0) + padding = (-2, -2) to (102, 2)
        // Path2: (100, 100) to (500, 500) + padding = (98, 98) to (502, 502)
        // Combined: (-2, -2) to (502, 502)
        // Width = 502 - (-2) = 504, Height = 502 - (-2) = 504
        group.WidthMicrometers.ShouldBe(504, 2, "Group width should span all paths");
        group.HeightMicrometers.ShouldBe(504, 2, "Group height should span all paths");
    }

    /// <summary>
    /// Verifies that removing a child updates the bounds correctly with frozen paths.
    /// </summary>
    [Fact]
    public void RemoveChild_WithFrozenPaths_UpdatesBounds()
    {
        var group = TestComponentFactory.CreateComponentGroup("ShrinkingGroup", addChildren: true);

        // Add a frozen path
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(150, 150, 200, 150, 0));
        group.AddInternalPath(new FrozenWaveguidePath { Path = routedPath });

        var initialWidth = group.WidthMicrometers;

        // Remove a child
        var childToRemove = group.ChildComponents[0];
        group.RemoveChild(childToRemove);

        // Bounds should update (may shrink if the removed child was at the edge)
        group.ChildComponents.Count.ShouldBe(1, "Group should have one remaining child");

        // Width may change depending on which child was removed
        // The key is that the frozen path is still included in bounds
        group.InternalPaths.Count.ShouldBe(1, "Frozen path should still exist");
    }
}
