using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Unit tests for ComponentGroup deep cloning functionality.
/// Verifies that Clone() properly copies child components, internal paths, and external pins.
/// </summary>
public class ComponentGroupCloningTests
{
    /// <summary>
    /// Verifies that cloning an empty group creates a new instance with no children.
    /// </summary>
    [Fact]
    public void Clone_EmptyGroup_CreatesNewInstance()
    {
        var original = TestComponentFactory.CreateComponentGroup("EmptyGroup", addChildren: false);

        var cloned = (ComponentGroup)original.Clone();

        cloned.ShouldNotBeNull("Cloned group should not be null");
        cloned.ShouldNotBeSameAs(original, "Cloned group should be a different instance");
        cloned.GroupName.ShouldBe("EmptyGroup", "Group name should be copied");
        cloned.ChildComponents.Count.ShouldBe(0, "Cloned empty group should have no children");
    }

    /// <summary>
    /// Verifies that cloning a group with children creates deep copies of all children.
    /// </summary>
    [Fact]
    public void Clone_GroupWithChildren_CreatesDeepCopyOfChildren()
    {
        var original = TestComponentFactory.CreateComponentGroup("ParentGroup", addChildren: true);

        var cloned = (ComponentGroup)original.Clone();

        cloned.ShouldNotBeNull("Cloned group should not be null");
        cloned.ShouldNotBeSameAs(original, "Cloned group should be a different instance");
        cloned.ChildComponents.Count.ShouldBe(original.ChildComponents.Count,
            "Cloned group should have same number of children");

        // Verify children are cloned (not the same instances)
        for (int i = 0; i < original.ChildComponents.Count; i++)
        {
            var originalChild = original.ChildComponents[i];
            var clonedChild = cloned.ChildComponents[i];

            clonedChild.ShouldNotBeSameAs(originalChild,
                $"Child {i} should be a different instance");
            clonedChild.Identifier.ShouldNotBe(originalChild.Identifier,
                $"Child {i} should have a different identifier");
            clonedChild.PhysicalX.ShouldBe(originalChild.PhysicalX,
                $"Child {i} X position should be preserved");
            clonedChild.PhysicalY.ShouldBe(originalChild.PhysicalY,
                $"Child {i} Y position should be preserved");
        }
    }

    /// <summary>
    /// Verifies that cloning preserves pose metadata (non-cardinal rotation, mirroring,
    /// unrotated dimensions) on child components, so a later save/load does not rotate
    /// the already-transformed pins a second time.
    /// </summary>
    [Fact]
    public void Clone_GroupWithChildren_PreservesChildPoseMetadata()
    {
        var original = TestComponentFactory.CreateComponentGroup("ParentGroup", addChildren: true);
        var originalChild = original.ChildComponents[0];
        originalChild.RotationDegrees = 30.0;
        originalChild.IsMirroredHorizontally = true;
        originalChild.UnrotatedWidthMicrometers = 120.0;
        originalChild.UnrotatedHeightMicrometers = 40.0;

        var cloned = (ComponentGroup)original.Clone();

        var clonedChild = cloned.ChildComponents[0];
        clonedChild.RotationDegrees.ShouldBe(30.0);
        clonedChild.IsMirroredHorizontally.ShouldBeTrue();
        clonedChild.UnrotatedWidthMicrometers.ShouldBe(120.0);
        clonedChild.UnrotatedHeightMicrometers.ShouldBe(40.0);
    }

    /// <summary>
    /// Verifies that cloned children have their ParentGroup reference set correctly.
    /// </summary>
    [Fact]
    public void Clone_GroupWithChildren_SetsParentGroupReferences()
    {
        var original = TestComponentFactory.CreateComponentGroup("ParentGroup", addChildren: true);

        var cloned = (ComponentGroup)original.Clone();

        foreach (var child in cloned.ChildComponents)
        {
            child.ParentGroup.ShouldBe(cloned,
                "Cloned child should reference the cloned parent group");
        }
    }

    /// <summary>
    /// Verifies that cloning a group with internal paths creates deep copies of those paths.
    /// </summary>
    [Fact]
    public void Clone_GroupWithInternalPaths_CreatesDeepCopyOfPaths()
    {
        var group = new ComponentGroup("ConnectedGroup")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        // Create components with physical pins using ComponentWithPins helper
        var child1 = CreateComponentWithPins("Child1", 100, 100);
        var child2 = CreateComponentWithPins("Child2", 400, 100);

        group.AddChild(child1);
        group.AddChild(child2);

        // Create a frozen path between children
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

        var cloned = (ComponentGroup)group.Clone();

        cloned.InternalPaths.Count.ShouldBe(1, "Cloned group should have one internal path");
        var clonedPath = cloned.InternalPaths[0];

        clonedPath.ShouldNotBeSameAs(frozenPath, "Cloned path should be a different instance");
        clonedPath.Path.ShouldNotBeSameAs(frozenPath.Path,
            "Cloned path's RoutedPath should be a different instance");

        // Verify the cloned path references the cloned components
        clonedPath.StartPin!.ParentComponent.ShouldBe(cloned.ChildComponents[0],
            "Cloned path should reference cloned child components");
        clonedPath.EndPin!.ParentComponent.ShouldBe(cloned.ChildComponents[1],
            "Cloned path should reference cloned child components");
    }

    /// <summary>
    /// Helper to create a component with physical pins for testing.
    /// </summary>
    private static Component CreateComponentWithPins(string identifier, double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>
        {
            new("out", 0, MatterType.Light, RectSide.Right)
        });

        var physicalPins = new List<PhysicalPin>
        {
            new()
            {
                Name = "out",
                OffsetXMicrometers = 250,
                OffsetYMicrometers = 125,
                AngleDegrees = 0
            }
        };

        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            parts,
            0,
            identifier,
            DiscreteRotation.R0,
            physicalPins);

        component.WidthMicrometers = 250;
        component.HeightMicrometers = 250;
        component.PhysicalX = x;
        component.PhysicalY = y;

        return component;
    }

    /// <summary>
    /// Verifies that cloning a group with external pins creates deep copies of those pins.
    /// </summary>
    [Fact]
    public void Clone_GroupWithExternalPins_CreatesDeepCopyOfPins()
    {
        var group = new ComponentGroup("GroupWithPins")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        var child1 = CreateComponentWithPins("Child1", 100, 100);
        group.AddChild(child1);

        // Add an external pin
        var externalPin = new GroupPin
        {
            Name = "group_input",
            InternalPin = child1.PhysicalPins[0],
            RelativeX = 0,
            RelativeY = 125,
            AngleDegrees = 180
        };
        group.AddExternalPin(externalPin);

        var cloned = (ComponentGroup)group.Clone();

        cloned.ExternalPins.Count.ShouldBe(1, "Cloned group should have one external pin");
        var clonedPin = cloned.ExternalPins[0];

        clonedPin.ShouldNotBeSameAs(externalPin, "Cloned pin should be a different instance");
        clonedPin.Name.ShouldBe("group_input", "Pin name should be preserved");
        clonedPin.RelativeX.ShouldBe(0, "Pin relative X should be preserved");
        clonedPin.RelativeY.ShouldBe(125, "Pin relative Y should be preserved");

        // Verify the cloned pin references the cloned component
        clonedPin.InternalPin.ParentComponent.ShouldBe(cloned.ChildComponents[0],
            "Cloned external pin should reference cloned child component");
    }

    /// <summary>
    /// Verifies that cloning a nested group (group within group) works correctly.
    /// </summary>
    [Fact]
    public void Clone_NestedGroup_CreatesDeepCopyRecursively()
    {
        var outerGroup = TestComponentFactory.CreateComponentGroup("OuterGroup", addChildren: false);
        var innerGroup = TestComponentFactory.CreateComponentGroup("InnerGroup", addChildren: true);

        outerGroup.AddChild(innerGroup);

        var clonedOuter = (ComponentGroup)outerGroup.Clone();

        clonedOuter.ChildComponents.Count.ShouldBe(1, "Outer group should have one child");
        var clonedInner = clonedOuter.ChildComponents[0] as ComponentGroup;

        clonedInner.ShouldNotBeNull("Child should be a ComponentGroup");
        clonedInner.ShouldNotBeSameAs(innerGroup, "Inner group should be cloned");
        clonedInner.ChildComponents.Count.ShouldBe(2,
            "Inner group should have its children cloned");

        // Verify parent references
        clonedInner.ParentGroup.ShouldBe(clonedOuter,
            "Cloned inner group should reference cloned outer group");
    }

    /// <summary>
    /// Verifies that cloning preserves group physical properties (position, size, rotation).
    /// </summary>
    [Fact]
    public void Clone_PreservesPhysicalProperties()
    {
        var original = TestComponentFactory.CreateComponentGroup("PhysicalGroup", addChildren: true);
        original.PhysicalX = 500;
        original.PhysicalY = 300;

        var cloned = (ComponentGroup)original.Clone();

        cloned.PhysicalX.ShouldBe(500, "X position should be preserved");
        cloned.PhysicalY.ShouldBe(300, "Y position should be preserved");
        cloned.WidthMicrometers.ShouldBe(original.WidthMicrometers, "Width should be preserved");
        cloned.HeightMicrometers.ShouldBe(original.HeightMicrometers, "Height should be preserved");
        cloned.Rotation90CounterClock.ShouldBe(original.Rotation90CounterClock,
            "Rotation should be preserved");
    }

    /// <summary>
    /// Verifies that cloning creates new unique identifiers for the group.
    /// </summary>
    [Fact]
    public void Clone_CreatesNewIdentifier()
    {
        var original = TestComponentFactory.CreateComponentGroup("UniqueGroup", addChildren: false);
        var originalId = original.Identifier;

        var cloned = (ComponentGroup)original.Clone();

        cloned.Identifier.ShouldNotBe(originalId,
            "Cloned group should have a different identifier");
    }

    /// <summary>
    /// Verifies that multiple clones are independent (modifying one doesn't affect others).
    /// </summary>
    [Fact]
    public void Clone_MultipleTimes_CreatesIndependentCopies()
    {
        var original = TestComponentFactory.CreateComponentGroup("OriginalGroup", addChildren: true);

        var clone1 = (ComponentGroup)original.Clone();
        var clone2 = (ComponentGroup)original.Clone();

        clone1.ShouldNotBeSameAs(clone2, "Each clone should be a different instance");
        clone1.Identifier.ShouldNotBe(clone2.Identifier,
            "Each clone should have a unique identifier");

        // Modify clone1
        clone1.PhysicalX = 999;

        // Verify clone2 is unaffected
        clone2.PhysicalX.ShouldBe(original.PhysicalX,
            "Modifying one clone should not affect another");
    }
}
