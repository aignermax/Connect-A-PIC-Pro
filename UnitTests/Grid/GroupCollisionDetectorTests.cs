using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Grid;

/// <summary>
/// Tests for GroupCollisionDetector which provides precise collision detection
/// for ComponentGroups by checking individual child components and frozen paths
/// instead of using the group's bounding box.
/// </summary>
public class GroupCollisionDetectorTests
{
    private readonly GroupCollisionDetector _detector = new();

    #region Helper Methods

    private static Component CreateComponent(string id, double x, double y, double width = 50, double height = 50)
    {
        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1] { { new Part() } },
            0,
            id,
            new DiscreteRotation(),
            new List<PhysicalPin>
            {
                new()
                {
                    Name = "pin0",
                    OffsetXMicrometers = 0,
                    OffsetYMicrometers = 0,
                    AngleDegrees = 0
                }
            })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = width,
            HeightMicrometers = height
        };

        return component;
    }

    private static ComponentGroup CreateGroup(string name, double x, double y)
    {
        var group = new ComponentGroup(name)
        {
            PhysicalX = x,
            PhysicalY = y
        };
        return group;
    }

    private static FrozenWaveguidePath CreateFrozenPath(
        PhysicalPin startPin,
        PhysicalPin endPin,
        params (double startX, double startY, double endX, double endY)[] segments)
    {
        var path = new RoutedPath();
        foreach (var seg in segments)
        {
            path.Segments.Add(new StraightSegment(seg.startX, seg.startY, seg.endX, seg.endY, 0));
        }

        return new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            Path = path,
            StartPin = startPin,
            EndPin = endPin
        };
    }

    #endregion

    [Fact]
    public void CanPlaceGroup_EmptyGroup_ShouldAllowPlacement()
    {
        // Arrange
        var emptyGroup = CreateGroup("Empty", 0, 0);
        var allComponents = new List<Component> { emptyGroup };

        // Act
        bool canPlace = _detector.CanPlaceGroup(emptyGroup, 100, 100, allComponents);

        // Assert
        canPlace.ShouldBeTrue();
    }

    [Fact]
    public void CanPlaceGroup_NoCollision_ShouldAllowPlacement()
    {
        // Arrange
        var group = CreateGroup("TestGroup", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 50, 50);
        var child2 = CreateComponent("child2", 100, 10, 50, 50);
        group.AddChild(child1);
        group.AddChild(child2);

        var otherComponent = CreateComponent("other", 300, 300, 50, 50);
        var allComponents = new List<Component> { group, child1, child2, otherComponent };

        // Act - Move group to (50, 50) - no collision
        bool canPlace = _detector.CanPlaceGroup(group, 50, 50, allComponents);

        // Assert
        canPlace.ShouldBeTrue();
    }

    [Fact]
    public void CanPlaceGroup_ChildComponentCollision_ShouldBlockPlacement()
    {
        // Arrange
        var group = CreateGroup("TestGroup", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 50, 50); // At (10,10) with 50x50
        group.AddChild(child1);

        // Other component positioned where child1 would move to
        var otherComponent = CreateComponent("other", 100, 100, 50, 50); // At (100,100) with 50x50
        var allComponents = new List<Component> { group, child1, otherComponent };

        // Act - Move group from (0,0) to (90,90)
        // This would move child1 from (10,10) to (100,100) - direct collision with otherComponent
        bool canPlace = _detector.CanPlaceGroup(group, 90, 90, allComponents);

        // Assert
        canPlace.ShouldBeFalse();
    }

    [Fact]
    public void CanPlaceGroup_BoundingBoxOverlapButNoChildCollision_ShouldAllowPlacement()
    {
        // Arrange - Create group with two widely spaced children
        var group = CreateGroup("TestGroup", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 30, 30);
        var child2 = CreateComponent("child2", 200, 10, 30, 30);
        group.AddChild(child1);
        group.AddChild(child2);

        // Place another component in the empty space between children
        var otherComponent = CreateComponent("other", 100, 100, 30, 30);
        var allComponents = new List<Component> { group, child1, child2, otherComponent };

        // Act - Move group so bounding box overlaps but children don't collide
        // Group bounding box covers (10,10) to (230,40)
        // Move group so otherComponent (100,100) falls within bounding box but not touching children
        bool canPlace = _detector.CanPlaceGroup(group, 0, 0, allComponents);

        // Assert - Should allow because children don't collide (only bounding box overlaps)
        canPlace.ShouldBeTrue();
    }

    [Fact]
    public void CanPlaceGroup_FrozenPathCollision_ShouldBlockPlacement()
    {
        // Arrange
        var group = CreateGroup("TestGroup", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 50, 50);
        var child2 = CreateComponent("child2", 100, 10, 50, 50);
        group.AddChild(child1);
        group.AddChild(child2);

        // Create frozen path between children (runs from 60,35 to 100,35)
        var frozenPath = CreateFrozenPath(
            child1.PhysicalPins[0],
            child2.PhysicalPins[0],
            (60, 35, 100, 35));
        group.AddInternalPath(frozenPath);

        // Place obstacle where frozen path would pass after moving
        var obstacle = CreateComponent("obstacle", 150, 125, 50, 50); // At (150,125) to (200,175)
        var allComponents = new List<Component> { group, child1, child2, obstacle };

        // Act - Move group from (0,0) to (90,90)
        // Frozen path would move from (60,35)-(100,35) to (150,125)-(190,125)
        // This overlaps with obstacle at (150,125)
        bool canPlace = _detector.CanPlaceGroup(group, 90, 90, allComponents);

        // Assert
        canPlace.ShouldBeFalse();
    }

    [Fact]
    public void CanPlaceGroup_ExcludedComponents_ShouldIgnoreInCollisionCheck()
    {
        // Arrange
        var group = CreateGroup("TestGroup", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 50, 50);
        group.AddChild(child1);

        var excludedComponent = CreateComponent("excluded", 100, 100, 50, 50);
        var allComponents = new List<Component> { group, child1, excludedComponent };
        var excludeSet = new HashSet<Component> { excludedComponent };

        // Act - Move child1 to same position as excludedComponent (normally would collide)
        bool canPlace = _detector.CanPlaceGroup(group, 90, 90, allComponents, excludeSet);

        // Assert - Should allow because excludedComponent is in exclusion set
        canPlace.ShouldBeTrue();
    }

    [Fact]
    public void CanPlaceGroup_NestedGroup_ShouldCheckNestedChildren()
    {
        // Arrange - Create nested group structure
        var outerGroup = CreateGroup("Outer", 0, 0);
        var innerGroup = CreateGroup("Inner", 10, 10);
        var nestedChild = CreateComponent("nested", 20, 20, 50, 50);
        innerGroup.AddChild(nestedChild);
        outerGroup.AddChild(innerGroup);

        var obstacle = CreateComponent("obstacle", 110, 110, 50, 50);
        var allComponents = new List<Component> { outerGroup, innerGroup, nestedChild, obstacle };

        // Act - Move outer group so nested child would collide with obstacle
        // nestedChild at (20,20), move outerGroup from (0,0) to (90,90)
        // nestedChild would move to (110,110) - collides with obstacle
        bool canPlace = _detector.CanPlaceGroup(outerGroup, 90, 90, allComponents);

        // Assert
        canPlace.ShouldBeFalse();
    }

    [Fact]
    public void CanPlaceGroup_GroupCollidingWithAnotherGroup_ShouldCheckChildComponents()
    {
        // Arrange - Two groups
        var group1 = CreateGroup("Group1", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 50, 50);
        group1.AddChild(child1);

        var group2 = CreateGroup("Group2", 200, 0);
        var child2 = CreateComponent("child2", 210, 10, 50, 50);
        group2.AddChild(child2);

        var allComponents = new List<Component> { group1, child1, group2, child2 };

        // Act - Move group1 so child1 would collide with child2 from group2
        // child1 at (10,10), move group1 from (0,0) to (200,0)
        // child1 would move to (210,10) - collides with child2
        bool canPlace = _detector.CanPlaceGroup(group1, 200, 0, allComponents);

        // Assert
        canPlace.ShouldBeFalse();
    }

    [Fact]
    public void CanPlaceGroup_GroupCollidingWithAnotherGroupNoChildOverlap_ShouldAllow()
    {
        // Arrange - Two groups with non-overlapping children
        var group1 = CreateGroup("Group1", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 30, 30);
        group1.AddChild(child1);

        var group2 = CreateGroup("Group2", 200, 0);
        var child2 = CreateComponent("child2", 250, 10, 30, 30); // Far right
        group2.AddChild(child2);

        var allComponents = new List<Component> { group1, child1, group2, child2 };

        // Act - Move group1 close to group2 but children don't overlap
        // child1 would move to (210,10), child2 is at (250,10) - no collision
        bool canPlace = _detector.CanPlaceGroup(group1, 200, 0, allComponents);

        // Assert
        canPlace.ShouldBeTrue();
    }

    [Fact]
    public void CanPlaceGroup_MultipleChildren_OnlyOneCollides_ShouldBlockPlacement()
    {
        // Arrange
        var group = CreateGroup("TestGroup", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 50, 50);
        var child2 = CreateComponent("child2", 100, 10, 50, 50);
        var child3 = CreateComponent("child3", 10, 100, 50, 50);
        group.AddChild(child1);
        group.AddChild(child2);
        group.AddChild(child3);

        // Place obstacle where only child2 would collide
        var obstacle = CreateComponent("obstacle", 190, 100, 50, 50);
        var allComponents = new List<Component> { group, child1, child2, child3, obstacle };

        // Act - Move group so child2 collides with obstacle
        // child2 at (100,10), move group from (0,0) to (90,90)
        // child2 would move to (190,100) - collides with obstacle
        bool canPlace = _detector.CanPlaceGroup(group, 90, 90, allComponents);

        // Assert - Should block because at least one child collides
        canPlace.ShouldBeFalse();
    }

    [Fact]
    public void CanPlaceGroup_FrozenPathWithBendSegment_ShouldDetectCollision()
    {
        // Arrange
        var group = CreateGroup("TestGroup", 0, 0);
        var child1 = CreateComponent("child1", 10, 10, 50, 50);
        var child2 = CreateComponent("child2", 100, 10, 50, 50);
        group.AddChild(child1);
        group.AddChild(child2);

        // Create frozen path with bend segment
        var path = new RoutedPath();
        path.Segments.Add(new BendSegment(75, 35, 20, 0, 90)); // Arc centered at (75,35) with radius 20
        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            Path = path,
            StartPin = child1.PhysicalPins[0],
            EndPin = child2.PhysicalPins[0]
        };
        group.AddInternalPath(frozenPath);

        // Place obstacle at arc center
        var obstacle = CreateComponent("obstacle", 165, 125, 10, 10); // At moved arc center (165,125)
        var allComponents = new List<Component> { group, child1, child2, obstacle };

        // Act - Move group so arc center would be at (165,125)
        bool canPlace = _detector.CanPlaceGroup(group, 90, 90, allComponents);

        // Assert - Arc should collide with obstacle
        canPlace.ShouldBeFalse();
    }

    #region Pre-drag overlap grandfathering

    [Fact]
    public void CanPlaceGroup_PreDragOverlapPartners_AreGrandfathered()
    {
        // Arrange - the moved group's child is stacked onto an external component
        // (deliberate overlap, as an exact GDS import leaves it).
        var group = CreateGroup("TestGroup", 0, 0);
        var child = CreateComponent("child", 100, 100, 50, 50);
        group.AddChild(child);
        var stacked = CreateComponent("stacked", 120, 100, 50, 50); // overlaps child
        var allComponents = new List<Component> { group, stacked };

        // Strict check: re-dropping at the same spot collides (5 µm gap rule).
        _detector.CanPlaceGroup(group, 0, 0, allComponents).ShouldBeFalse();

        // Act - the pair already overlapped when the drag began.
        var partners = new Dictionary<Component, HashSet<Component>>
        {
            [child] = new() { stacked },
        };
        bool canPlace = _detector.CanPlaceGroup(group, 0, 0, allComponents, preDragOverlapPartners: partners);

        // Assert
        canPlace.ShouldBeTrue("re-dropping onto the pre-drag overlap partner is grandfathered");
    }

    [Fact]
    public void CanPlaceGroup_PreDragOverlapPartners_DoNotExcuseNewOverlaps()
    {
        // Arrange
        var group = CreateGroup("TestGroup", 0, 0);
        var child = CreateComponent("child", 100, 100, 50, 50);
        group.AddChild(child);
        var stacked = CreateComponent("stacked", 120, 100, 50, 50);
        var other = CreateComponent("other", 400, 400, 50, 50);
        var allComponents = new List<Component> { group, stacked, other };
        var partners = new Dictionary<Component, HashSet<Component>>
        {
            [child] = new() { stacked },
        };

        // Act - moving the group so the child lands on 'other' (a NEW overlap)…
        bool ontoOther = _detector.CanPlaceGroup(group, 300, 300, allComponents, preDragOverlapPartners: partners);
        // …and moving it to genuinely free space.
        bool ontoFree = _detector.CanPlaceGroup(group, -50, 300, allComponents, preDragOverlapPartners: partners);

        // Assert
        ontoOther.ShouldBeFalse("'other' never overlapped the child before the drag");
        ontoFree.ShouldBeTrue();
    }

    [Fact]
    public void CanPlaceGroup_PreDragOverlapPartners_ApplyAcrossGroupChildren()
    {
        // Arrange - the moved group's child is stacked onto a child of ANOTHER group.
        var movedGroup = CreateGroup("Moved", 0, 0);
        var movedChild = CreateComponent("movedChild", 100, 100, 50, 50);
        movedGroup.AddChild(movedChild);

        var hostGroup = CreateGroup("Host", 0, 0);
        var hostChild = CreateComponent("hostChild", 120, 100, 50, 50); // overlaps movedChild
        hostGroup.AddChild(hostChild);

        var allComponents = new List<Component> { movedGroup, hostGroup };

        _detector.CanPlaceGroup(movedGroup, 0, 0, allComponents).ShouldBeFalse();

        // Act
        var partners = new Dictionary<Component, HashSet<Component>>
        {
            [movedChild] = new() { hostChild },
        };
        bool canPlace = _detector.CanPlaceGroup(movedGroup, 0, 0, allComponents, preDragOverlapPartners: partners);

        // Assert
        canPlace.ShouldBeTrue("the moved child may re-overlap the OTHER group's child it was stacked with");
    }

    #endregion
}
