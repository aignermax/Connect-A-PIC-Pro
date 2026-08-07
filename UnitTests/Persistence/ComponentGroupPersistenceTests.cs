using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_DataAccess.Persistence;
using CAP_DataAccess.Persistence.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Unit tests for ComponentGroup serialization and deserialization.
/// </summary>
public class ComponentGroupPersistenceTests
{
    [Fact]
    public void ToDto_SimpleGroup_SerializesCorrectly()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 10, 20);
        var comp2 = CreateTestComponent("comp2", 30, 40);

        var group = new ComponentGroup("TestGroup")
        {
            Description = "Test description",
            PhysicalX = 5,
            PhysicalY = 15
        };
        group.AddChild(comp1);
        group.AddChild(comp2);

        // Act
        var dto = ComponentGroupSerializer.ToDto(group);

        // Assert
        dto.GroupName.ShouldBe("TestGroup");
        dto.Description.ShouldBe("Test description");
        dto.PhysicalX.ShouldBe(5);
        dto.PhysicalY.ShouldBe(15);
        dto.ChildComponentIds.Count.ShouldBe(2);
        dto.ChildComponentIds.ShouldContain("comp1");
        dto.ChildComponentIds.ShouldContain("comp2");
    }

    [Fact]
    public void ToDto_GroupWithFrozenPath_SerializesPath()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);

        var group = new ComponentGroup("TestGroup");
        group.AddChild(comp1);
        group.AddChild(comp2);

        // Create a frozen path
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(60, 10, 100, 10, 90));

        var frozenPath = new FrozenWaveguidePath
        {
            Path = path,
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };
        group.AddInternalPath(frozenPath);

        // Act
        var dto = ComponentGroupSerializer.ToDto(group);

        // Assert
        dto.InternalPaths.Count.ShouldBe(1);
        var pathDto = dto.InternalPaths[0];
        pathDto.StartComponentId.ShouldBe("comp1");
        pathDto.EndComponentId.ShouldBe("comp2");
        pathDto.Segments.Count.ShouldBe(3);
        pathDto.Segments[0].Type.ShouldBe("straight");
        pathDto.Segments[1].Type.ShouldBe("arc");
        pathDto.Segments[2].Type.ShouldBe("straight");
    }

    [Fact]
    public void ToDto_GroupWithExternalPins_SerializesPins()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var group = new ComponentGroup("TestGroup");
        group.AddChild(comp1);

        var groupPin = new GroupPin
        {
            Name = "ExternalPin1",
            InternalPin = comp1.PhysicalPins[0],
            RelativeX = 10,
            RelativeY = 20,
            AngleDegrees = 90
        };
        group.AddExternalPin(groupPin);

        // Act
        var dto = ComponentGroupSerializer.ToDto(group);

        // Assert
        dto.ExternalPins.Count.ShouldBe(1);
        var pinDto = dto.ExternalPins[0];
        pinDto.Name.ShouldBe("ExternalPin1");
        pinDto.InternalComponentId.ShouldBe("comp1");
        pinDto.RelativeX.ShouldBe(10);
        pinDto.RelativeY.ShouldBe(20);
        pinDto.AngleDegrees.ShouldBe(90);
    }

    [Fact]
    public void FromDto_SimpleGroup_ReconstructsCorrectly()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 10, 20);
        var comp2 = CreateTestComponent("comp2", 30, 40);

        var componentLookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };

        var dto = new ComponentGroupDto
        {
            GroupName = "ReconstructedGroup",
            Description = "Test",
            Identifier = "group_test",
            PhysicalX = 5,
            PhysicalY = 15,
            Rotation90CounterClock = 1,
            ChildComponentIds = new List<string> { "comp1", "comp2" }
        };

        // Act
        var group = ComponentGroupSerializer.FromDto(dto, componentLookup);

        // Assert
        group.GroupName.ShouldBe("ReconstructedGroup");
        group.Description.ShouldBe("Test");
        group.Identifier.ShouldBe("group_test");
        group.PhysicalX.ShouldBe(5);
        group.PhysicalY.ShouldBe(15);
        group.Rotation90CounterClock.ShouldBe(DiscreteRotation.R90);
        group.ChildComponents.Count.ShouldBe(2);
        group.ChildComponents.ShouldContain(comp1);
        group.ChildComponents.ShouldContain(comp2);
    }

    [Fact]
    public void FromDto_GroupWithFrozenPath_ReconstructsPath()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);

        var componentLookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };

        var dto = new ComponentGroupDto
        {
            GroupName = "TestGroup",
            Identifier = "group_test",
            ChildComponentIds = new List<string> { "comp1", "comp2" },
            InternalPaths = new List<FrozenPathDto>
            {
                new FrozenPathDto
                {
                    PathId = Guid.NewGuid().ToString(),
                    StartComponentId = "comp1",
                    StartPinName = "o1",
                    EndComponentId = "comp2",
                    EndPinName = "o1",
                    Segments = new List<PathSegmentDto>
                    {
                        new PathSegmentDto
                        {
                            Type = "straight",
                            StartX = 0,
                            StartY = 0,
                            EndX = 50,
                            EndY = 0,
                            StartAngleDegrees = 0,
                            EndAngleDegrees = 0
                        },
                        new PathSegmentDto
                        {
                            Type = "arc",
                            CenterX = 50,
                            CenterY = 10,
                            RadiusMicrometers = 10,
                            StartAngleDegrees = 0,
                            SweepAngleDegrees = 90
                        }
                    }
                }
            }
        };

        // Act
        var group = ComponentGroupSerializer.FromDto(dto, componentLookup);

        // Assert
        group.InternalPaths.Count.ShouldBe(1);
        var frozenPath = group.InternalPaths[0];
        frozenPath.StartPin.ShouldBe(comp1.PhysicalPins[0]);
        frozenPath.EndPin.ShouldBe(comp2.PhysicalPins[0]);
        frozenPath.Path.Segments.Count.ShouldBe(2);
        frozenPath.Path.Segments[0].ShouldBeOfType<StraightSegment>();
        frozenPath.Path.Segments[1].ShouldBeOfType<BendSegment>();
    }

    [Fact]
    public void PinLessFrozenPath_RoundTripsWithEmptyPinReferences()
    {
        // A GDS-imported route outline has no endpoint pins: it serializes with
        // empty component/pin references and must come back pin-less (not dropped,
        // not resolved against a wrong component).
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);

        var group = new ComponentGroup("ImportGroup");
        group.AddChild(comp1);
        group.AddChild(comp2);

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(10, 2, 20, 2, 0));
        path.Segments.Add(new StraightSegment(20, 2, 20, 3, 90));
        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = path,
            StartPin = null,
            EndPin = null,
        });

        // Act: DTO shape, then the reconstruction from that DTO.
        var dto = ComponentGroupSerializer.ToDto(group);
        var pathDto = dto.InternalPaths.ShouldHaveSingleItem();
        pathDto.StartComponentId.ShouldBeEmpty();
        pathDto.StartComponentGuid.ShouldBeNull();
        pathDto.StartPinName.ShouldBeEmpty();
        pathDto.EndComponentId.ShouldBeEmpty();
        pathDto.EndComponentGuid.ShouldBeNull();
        pathDto.EndPinName.ShouldBeEmpty();
        pathDto.Segments.Count.ShouldBe(2);

        var componentLookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };
        var reconstructed = ComponentGroupSerializer.FromDto(dto, componentLookup);

        var frozenPath = reconstructed.InternalPaths.ShouldHaveSingleItem();
        frozenPath.StartPin.ShouldBeNull();
        frozenPath.EndPin.ShouldBeNull();
        frozenPath.Path.Segments.Count.ShouldBe(2);
        frozenPath.Path.Segments[0].StartPoint.ShouldBe((10.0, 2.0));
        frozenPath.Path.Segments[0].EndPoint.ShouldBe((20.0, 2.0));
        frozenPath.Path.Segments[1].StartPoint.ShouldBe((20.0, 2.0));
        frozenPath.Path.Segments[1].EndPoint.ShouldBe((20.0, 3.0));
    }

    [Fact]
    public void FromDto_PartiallyReferencedFrozenPath_StillFailsLoudly()
    {
        // Only an ALL-empty reference triple is pin-less geometry; a path that
        // references a component but loses its pin is corrupt data and must keep
        // failing loudly instead of silently degrading to pin-less.
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var componentLookup = new Dictionary<string, Component> { { "comp1", comp1 } };

        var dto = new ComponentGroupDto
        {
            GroupName = "TestGroup",
            Identifier = "group_test",
            ChildComponentIds = new List<string> { "comp1" },
            InternalPaths = new List<FrozenPathDto>
            {
                new FrozenPathDto
                {
                    PathId = Guid.NewGuid().ToString(),
                    StartComponentId = "comp1",
                    StartPinName = "missing_pin",
                    EndComponentId = "comp1",
                    EndPinName = "o1",
                }
            }
        };

        Should.Throw<InvalidOperationException>(() =>
            ComponentGroupSerializer.FromDto(dto, componentLookup));
    }

    [Fact]
    public void FromDto_LegacyBlockedSingleStraightFrozenPath_MigratesToPlaceholderGeometry()
    {
        // A design file saved between the router's self-crossing degrade-to-blocked-fallback
        // step shipping and IsPlaceholderGeometry being introduced has exactly this shape
        // (blocked, one straight segment) with no IsPlaceholderGeometry field at all — must
        // migrate to true on load, not silently re-export the placeholder line.
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);
        var componentLookup = new Dictionary<string, Component> { { "comp1", comp1 }, { "comp2", comp2 } };

        var dto = new ComponentGroupDto
        {
            GroupName = "TestGroup",
            Identifier = "group_test",
            ChildComponentIds = new List<string> { "comp1", "comp2" },
            InternalPaths = new List<FrozenPathDto>
            {
                new FrozenPathDto
                {
                    PathId = Guid.NewGuid().ToString(),
                    StartComponentId = "comp1",
                    StartPinName = "o1",
                    EndComponentId = "comp2",
                    EndPinName = "o1",
                    IsBlockedFallback = true,
                    // IsPlaceholderGeometry intentionally left unset (null) — simulates a
                    // design file saved before the field existed.
                    Segments = new List<PathSegmentDto>
                    {
                        new PathSegmentDto
                        {
                            Type = "straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0,
                            StartAngleDegrees = 0, EndAngleDegrees = 0
                        }
                    }
                }
            }
        };

        var group = ComponentGroupSerializer.FromDto(dto, componentLookup);

        group.InternalPaths[0].Path.IsPlaceholderGeometry.ShouldBeTrue();
    }

    [Fact]
    public void FromDto_ExplicitFalsePlaceholderFrozenPath_IsNotOverriddenByMigration()
    {
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);
        var componentLookup = new Dictionary<string, Component> { { "comp1", comp1 }, { "comp2", comp2 } };

        var dto = new ComponentGroupDto
        {
            GroupName = "TestGroup",
            Identifier = "group_test",
            ChildComponentIds = new List<string> { "comp1", "comp2" },
            InternalPaths = new List<FrozenPathDto>
            {
                new FrozenPathDto
                {
                    PathId = Guid.NewGuid().ToString(),
                    StartComponentId = "comp1",
                    StartPinName = "o1",
                    EndComponentId = "comp2",
                    EndPinName = "o1",
                    IsBlockedFallback = true,
                    IsPlaceholderGeometry = false,
                    Segments = new List<PathSegmentDto>
                    {
                        new PathSegmentDto
                        {
                            Type = "straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0,
                            StartAngleDegrees = 0, EndAngleDegrees = 0
                        }
                    }
                }
            }
        };

        var group = ComponentGroupSerializer.FromDto(dto, componentLookup);

        group.InternalPaths[0].Path.IsPlaceholderGeometry.ShouldBeFalse();
    }

    [Fact]
    public void FromDto_GroupWithExternalPins_ReconstructsPins()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 0, 0);

        var componentLookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 }
        };

        var dto = new ComponentGroupDto
        {
            GroupName = "TestGroup",
            Identifier = "group_test",
            ChildComponentIds = new List<string> { "comp1" },
            ExternalPins = new List<GroupPinDto>
            {
                new GroupPinDto
                {
                    PinId = Guid.NewGuid().ToString(),
                    Name = "ExternalPin1",
                    InternalComponentId = "comp1",
                    InternalPinName = "o1",
                    RelativeX = 10,
                    RelativeY = 20,
                    AngleDegrees = 90
                }
            }
        };

        // Act
        var group = ComponentGroupSerializer.FromDto(dto, componentLookup);

        // Assert
        group.ExternalPins.Count.ShouldBe(1);
        var pin = group.ExternalPins[0];
        pin.Name.ShouldBe("ExternalPin1");
        pin.InternalPin.ShouldBe(comp1.PhysicalPins[0]);
        pin.RelativeX.ShouldBe(10);
        pin.RelativeY.ShouldBe(20);
        pin.AngleDegrees.ShouldBe(90);
    }

    [Fact]
    public void RoundTrip_SimpleGroup_PreservesData()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 10, 20);
        var comp2 = CreateTestComponent("comp2", 30, 40);

        var originalGroup = new ComponentGroup("OriginalGroup")
        {
            Description = "Original description",
            PhysicalX = 5,
            PhysicalY = 15
        };
        originalGroup.AddChild(comp1);
        originalGroup.AddChild(comp2);

        var componentLookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };

        // Act - serialize and deserialize
        var dto = ComponentGroupSerializer.ToDto(originalGroup);
        var reconstructed = ComponentGroupSerializer.FromDto(dto, componentLookup);

        // Assert
        reconstructed.GroupName.ShouldBe(originalGroup.GroupName);
        reconstructed.Description.ShouldBe(originalGroup.Description);
        reconstructed.PhysicalX.ShouldBe(originalGroup.PhysicalX);
        reconstructed.PhysicalY.ShouldBe(originalGroup.PhysicalY);
        reconstructed.ChildComponents.Count.ShouldBe(originalGroup.ChildComponents.Count);
    }

    [Fact]
    public void FromDto_MissingChildComponent_ThrowsException()
    {
        // Arrange
        var dto = new ComponentGroupDto
        {
            GroupName = "TestGroup",
            Identifier = "group_test",
            ChildComponentIds = new List<string> { "nonexistent" }
        };

        var emptyLookup = new Dictionary<string, Component>();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() =>
            ComponentGroupSerializer.FromDto(dto, emptyLookup));
    }

    [Fact]
    public void ToDto_NestedGroups_IncludesParentGroupId()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var innerGroup = new ComponentGroup("InnerGroup");
        innerGroup.AddChild(comp1);

        var outerGroup = new ComponentGroup("OuterGroup");
        outerGroup.AddChild(innerGroup);
        innerGroup.ParentGroup = outerGroup;

        // Act
        var dto = ComponentGroupSerializer.ToDto(innerGroup);

        // Assert
        dto.ParentGroupId.ShouldBe(outerGroup.Identifier);
    }

    /// <summary>
    /// Creates a test component with a single pin.
    /// </summary>
    private Component CreateTestComponent(string identifier, double x, double y)
    {
        var pin = new PhysicalPin
        {
            Name = "o1",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = 0
        };

        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test_type",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            identifier,
            new DiscreteRotation(),
            new List<PhysicalPin> { pin }
        )
        {
            PhysicalX = x,
            PhysicalY = y
        };

        pin.ParentComponent = component;
        return component;
    }
}
