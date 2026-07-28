using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Unit tests for GroupTemplateSerializer.
/// Verifies serialization round-trip preserves all group data.
/// </summary>
public class GroupTemplateSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_EmptyGroup_RoundTrips()
    {
        // Arrange
        var group = new ComponentGroup("EmptyGroup")
        {
            Description = "Test description",
            PhysicalX = 10,
            PhysicalY = 20
        };

        // Act
        var json = GroupTemplateSerializer.Serialize(group);
        var result = GroupTemplateSerializer.Deserialize(json);

        // Assert
        result.ShouldNotBeNull();
        result!.GroupName.ShouldBe("EmptyGroup");
        result.Description.ShouldBe("Test description");
        result.PhysicalX.ShouldBe(10);
        result.PhysicalY.ShouldBe(20);
    }

    [Fact]
    public void SerializeAndDeserialize_WithChildren_PreservesAllData()
    {
        // Arrange
        var group = CreateGroupWithChildren(3);

        // Act
        var json = GroupTemplateSerializer.Serialize(group);
        var result = GroupTemplateSerializer.Deserialize(json);

        // Assert
        result.ShouldNotBeNull();
        result!.ChildComponents.Count.ShouldBe(3);

        for (int i = 0; i < 3; i++)
        {
            result.ChildComponents[i].PhysicalX.ShouldBe(i * 100);
            result.ChildComponents[i].PhysicalPins.Count.ShouldBe(2);
            result.ChildComponents[i].PhysicalPins[0].Name.ShouldBe("a0");
            result.ChildComponents[i].PhysicalPins[1].Name.ShouldBe("b0");
        }
    }

    [Fact]
    public void SerializeAndDeserialize_WithFrozenPath_PreservesPathAndPinLinks()
    {
        // Arrange
        var group = CreateGroupWithChildren(2);
        var comp1 = group.ChildComponents[0];
        var comp2 = group.ChildComponents[1];

        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(50, 0, 100, 0, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            StartPin = comp1.PhysicalPins[1], // b0
            EndPin = comp2.PhysicalPins[0],   // a0
            Path = routedPath
        };
        group.AddInternalPath(frozenPath);

        // Act
        var json = GroupTemplateSerializer.Serialize(group);
        var result = GroupTemplateSerializer.Deserialize(json);

        // Assert
        result.ShouldNotBeNull();
        result!.InternalPaths.Count.ShouldBe(1);

        var loadedPath = result.InternalPaths[0];
        loadedPath.StartPin.Name.ShouldBe("b0");
        loadedPath.EndPin.Name.ShouldBe("a0");
        loadedPath.StartPin.ParentComponent.ShouldBe(result.ChildComponents[0]);
        loadedPath.EndPin.ParentComponent.ShouldBe(result.ChildComponents[1]);

        loadedPath.Path.Segments.Count.ShouldBe(1);
        var seg = loadedPath.Path.Segments[0].ShouldBeOfType<StraightSegment>();
        seg.StartPoint.X.ShouldBe(50, tolerance: 0.01);
        seg.EndPoint.X.ShouldBe(100, tolerance: 0.01);
    }

    [Fact]
    public void Deserialize_LegacyBlockedSingleStraightFrozenPath_MigratesToPlaceholderGeometry()
    {
        // A template saved between the router's self-crossing degrade-to-blocked-fallback
        // step shipping and IsPlaceholderGeometry being introduced has exactly this shape
        // (blocked, one straight segment) with no IsPlaceholderGeometry field at all — must
        // migrate to true on load, not silently re-export the placeholder line.
        var group = CreateGroupWithChildren(2);
        var comp1 = group.ChildComponents[0];
        var comp2 = group.ChildComponents[1];

        var routedPath = new RoutedPath { IsBlockedFallback = true };
        routedPath.Segments.Add(new StraightSegment(50, 0, 100, 0, 0));

        group.AddInternalPath(new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            StartPin = comp1.PhysicalPins[1],
            EndPin = comp2.PhysicalPins[0],
            Path = routedPath
        });

        var json = GroupTemplateSerializer.Serialize(group);

        // Simulate a template saved before the field existed: strip it entirely rather than
        // leaving it "false", so the migration must infer it from the route's shape.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["InternalPaths"]![0]!.AsObject().Remove("IsPlaceholderGeometry");
        var legacyJson = node.ToJsonString();

        var result = GroupTemplateSerializer.Deserialize(legacyJson);

        result.ShouldNotBeNull();
        result!.InternalPaths[0].Path.IsPlaceholderGeometry.ShouldBeTrue();
    }

    [Fact]
    public void SerializeAndDeserialize_WithArcSegment_PreservesGeometry()
    {
        // Arrange
        var group = CreateGroupWithChildren(2);
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new BendSegment(75, 15, 25, 0, 90));

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            StartPin = group.ChildComponents[0].PhysicalPins[1],
            EndPin = group.ChildComponents[1].PhysicalPins[0],
            Path = routedPath
        };
        group.AddInternalPath(frozenPath);

        // Act
        var json = GroupTemplateSerializer.Serialize(group);
        var result = GroupTemplateSerializer.Deserialize(json);

        // Assert
        var seg = result!.InternalPaths[0].Path.Segments[0]
            .ShouldBeOfType<BendSegment>();
        seg.Center.X.ShouldBe(75, tolerance: 0.01);
        seg.Center.Y.ShouldBe(15, tolerance: 0.01);
        seg.RadiusMicrometers.ShouldBe(25, tolerance: 0.01);
        seg.StartAngleDegrees.ShouldBe(0, tolerance: 0.01);
        seg.SweepAngleDegrees.ShouldBe(90, tolerance: 0.01);
    }

    [Fact]
    public void SerializeAndDeserialize_WithExternalPins_PreservesLinks()
    {
        // Arrange
        var group = CreateGroupWithChildren(2);
        var extPin = new GroupPin
        {
            PinId = Guid.NewGuid(),
            Name = "ext_a0",
            InternalPin = group.ChildComponents[0].PhysicalPins[0],
            RelativeX = 0,
            RelativeY = 0,
            AngleDegrees = 180
        };
        group.AddExternalPin(extPin);

        // Act
        var json = GroupTemplateSerializer.Serialize(group);
        var result = GroupTemplateSerializer.Deserialize(json);

        // Assert
        result.ShouldNotBeNull();
        result!.ExternalPins.Count.ShouldBe(1);

        var loadedPin = result.ExternalPins[0];
        loadedPin.Name.ShouldBe("ext_a0");
        loadedPin.AngleDegrees.ShouldBe(180);
        loadedPin.InternalPin.Name.ShouldBe("a0");
        loadedPin.InternalPin.ParentComponent.ShouldBe(result.ChildComponents[0]);
    }

    [Fact]
    public void SerializeAndDeserialize_NestedGroup_PreservesHierarchy()
    {
        // Arrange
        var innerGroup = CreateGroupWithChildren(2);
        innerGroup.GroupName = "InnerGroup";

        var outerGroup = new ComponentGroup("OuterGroup");
        outerGroup.AddChild(innerGroup);
        outerGroup.AddChild(CreateTestComponent("outer_comp", 300, 0));

        // Act
        var json = GroupTemplateSerializer.Serialize(outerGroup);
        var result = GroupTemplateSerializer.Deserialize(json);

        // Assert
        result.ShouldNotBeNull();
        result!.ChildComponents.Count.ShouldBe(2);

        var nestedGroup = result.ChildComponents[0].ShouldBeOfType<ComponentGroup>();
        nestedGroup.GroupName.ShouldBe("InnerGroup");
        nestedGroup.ChildComponents.Count.ShouldBe(2);
    }

    [Fact]
    public void Deserialize_NullOrEmpty_ReturnsNull()
    {
        GroupTemplateSerializer.Deserialize(null!).ShouldBeNull();
        GroupTemplateSerializer.Deserialize("").ShouldBeNull();
        GroupTemplateSerializer.Deserialize("  ").ShouldBeNull();
    }

    [Fact]
    public void DeepCopy_AfterDeserialization_CreatesIndependentCopy()
    {
        // Arrange
        var group = CreateGroupWithChildren(2);
        var json = GroupTemplateSerializer.Serialize(group);
        var deserialized = GroupTemplateSerializer.Deserialize(json)!;

        // Act
        var copy = deserialized.DeepCopy();

        // Assert
        copy.Identifier.ShouldNotBe(deserialized.Identifier);
        copy.ChildComponents[0].Identifier.ShouldNotBe(
            deserialized.ChildComponents[0].Identifier);
        copy.ChildComponents.Count.ShouldBe(2);
    }

    /// <summary>
    /// Creates a group with the specified number of children, each with 2 pins.
    /// </summary>
    [Fact]
    public void SerializeAndDeserialize_ElectricalPin_PreservesMatterType()
    {
        // A group template with an electrical pin must keep it electrical on reload — the
        // serializer used to hardcode MatterType.Light, silently turning every pin optical
        // and defeating the cross-kind connection guard (#519 review).
        var group = new ComponentGroup("HeaterGroup") { PhysicalX = 0, PhysicalY = 0 };
        var comp = new Component(
            new Dictionary<int, SMatrix>(), new List<Slider>(), "heater", "",
            new Part[1, 1] { { new Part() } }, -1, $"heater_{Guid.NewGuid():N}", DiscreteRotation.R0,
            new List<PhysicalPin>
            {
                new() { Name = "opt", OffsetXMicrometers = 0, OffsetYMicrometers = 0, AngleDegrees = 180,
                    LogicalPin = new Pin("opt", 0, MatterType.Light, RectSide.Left, Guid.NewGuid(), Guid.NewGuid()) },
                new() { Name = "elec", OffsetXMicrometers = 25, OffsetYMicrometers = 0, AngleDegrees = 270,
                    LogicalPin = new Pin("elec", 1, MatterType.Electricity, RectSide.Up, Guid.NewGuid(), Guid.NewGuid()) },
            })
        { PhysicalX = 0, PhysicalY = 0, WidthMicrometers = 50, HeightMicrometers = 30 };
        group.AddChild(comp);

        var result = GroupTemplateSerializer.Deserialize(GroupTemplateSerializer.Serialize(group));

        var pins = result!.ChildComponents[0].PhysicalPins;
        pins.First(p => p.Name == "opt").MatterType.ShouldBe(MatterType.Light);
        pins.First(p => p.Name == "elec").MatterType.ShouldBe(MatterType.Electricity);
    }

    private ComponentGroup CreateGroupWithChildren(int count)
    {
        var group = new ComponentGroup("TestGroup")
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        for (int i = 0; i < count; i++)
        {
            group.AddChild(CreateTestComponent(
                $"comp_{i}_{Guid.NewGuid():N}",
                i * 100, 0));
        }

        return group;
    }

    /// <summary>
    /// Creates a test component with two pins.
    /// </summary>
    private Component CreateTestComponent(string id, double x, double y)
    {
        return new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            id,
            DiscreteRotation.R0,
            new List<PhysicalPin>
            {
                new PhysicalPin
                {
                    Name = "a0",
                    OffsetXMicrometers = 0,
                    OffsetYMicrometers = 0,
                    AngleDegrees = 180
                },
                new PhysicalPin
                {
                    Name = "b0",
                    OffsetXMicrometers = 50,
                    OffsetYMicrometers = 0,
                    AngleDegrees = 0
                }
            })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 50,
            HeightMicrometers = 30
        };
    }
}
