using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using CAP_DataAccess.Persistence;
using CAP_DataAccess.Persistence.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Round-trip tests for per-connection routing settings across design-file persistence
/// of groups (field report round 5): the DataAccess <see cref="ComponentGroupSerializer"/>
/// must carry bend style, radius, width, freeze flag and manual bend overrides, and old
/// design files without those fields must load with "Auto" defaults.
/// </summary>
public class ComponentGroupConnectionSettingsTests
{
    [Fact]
    public void RoundTrip_FrozenPathWithSettings_PreservesConnectionSettings()
    {
        // Arrange
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);
        var group = new ComponentGroup("TestGroup");
        group.AddChild(comp1);
        group.AddChild(comp2);

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            Path = path,
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0],
            ConnectionType = WaveguideType.Cobra,
            BendRadiusMicrometers = 40.0,
            WidthMicrometers = 0.8,
            IsRouteFrozen = true,
            PropagationLossDbPerCm = 1.5
        };
        frozenPath.BendRadiusOverrides[1] = 22.0;
        group.AddInternalPath(frozenPath);

        var lookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };

        // Act
        var dto = ComponentGroupSerializer.ToDto(group);
        var restored = ComponentGroupSerializer.FromDto(dto, lookup);

        // Assert
        var restoredPath = restored.InternalPaths.ShouldHaveSingleItem();
        restoredPath.ConnectionType.ShouldBe(WaveguideType.Cobra);
        restoredPath.BendRadiusMicrometers.ShouldBe(40.0);
        restoredPath.WidthMicrometers.ShouldBe(0.8);
        restoredPath.IsRouteFrozen.ShouldBeTrue();
        restoredPath.PropagationLossDbPerCm.ShouldBe(1.5);
        restoredPath.BendRadiusOverrides[1].ShouldBe(22.0);
    }

    [Fact]
    public void FromDto_LegacyDtoWithoutSettingsFields_LoadsWithAutoDefaults()
    {
        // Arrange - a DTO shaped like an old design file (no settings fields set)
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);
        var lookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };

        var dto = new ComponentGroupDto
        {
            GroupName = "LegacyGroup",
            Identifier = "group_legacy",
            ChildComponentIds = new List<string> { "comp1", "comp2" },
            InternalPaths = new List<FrozenPathDto>
            {
                new()
                {
                    PathId = Guid.NewGuid().ToString(),
                    StartComponentId = "comp1",
                    StartPinName = "o1",
                    EndComponentId = "comp2",
                    EndPinName = "o1",
                    Segments = new List<PathSegmentDto>
                    {
                        new() { Type = "straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0 }
                    }
                }
            }
        };

        // Act
        var group = ComponentGroupSerializer.FromDto(dto, lookup);

        // Assert - defaults, no crash
        var restoredPath = group.InternalPaths.ShouldHaveSingleItem();
        restoredPath.ConnectionType.ShouldBe(WaveguideType.Auto);
        restoredPath.BendRadiusMicrometers.ShouldBe(new WaveguideConnection().BendRadiusMicrometers);
        restoredPath.WidthMicrometers.ShouldBe(new WaveguideConnection().WidthMicrometers);
        restoredPath.IsRouteFrozen.ShouldBeFalse();
        restoredPath.BendRadiusOverrides.ShouldBeEmpty();
    }

    [Fact]
    public void RoundTrip_FrozenPathWithLayerTag_PreservesSourceLayer()
    {
        // Arrange — a GDS-imported route outline (pin-less) tagged with its source layer.
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);
        var group = new ComponentGroup("TestGroup");
        group.AddChild(comp1);
        group.AddChild(comp2);

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));

        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = path,
            StartPin = null,
            EndPin = null,
            Layer = 31,
            DataType = 5
        });

        var lookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };

        // Act
        var dto = ComponentGroupSerializer.ToDto(group);
        var restored = ComponentGroupSerializer.FromDto(dto, lookup);

        // Assert
        var restoredPath = restored.InternalPaths.ShouldHaveSingleItem();
        restoredPath.Layer.ShouldBe(31);
        restoredPath.DataType.ShouldBe(5);
    }

    [Fact]
    public void FromDto_LegacyDtoWithoutLayerTag_LoadsNullLayer()
    {
        // Arrange — a DTO shaped like an old design file (no layer fields set): the
        // missing tag must load as null, leaving the process-default export unchanged.
        var comp1 = CreateTestComponent("comp1", 0, 0);
        var comp2 = CreateTestComponent("comp2", 100, 0);
        var lookup = new Dictionary<string, Component>
        {
            { "comp1", comp1 },
            { "comp2", comp2 }
        };

        var dto = new ComponentGroupDto
        {
            GroupName = "LegacyGroup",
            Identifier = "group_legacy",
            ChildComponentIds = new List<string> { "comp1", "comp2" },
            InternalPaths = new List<FrozenPathDto>
            {
                new()
                {
                    PathId = Guid.NewGuid().ToString(),
                    StartComponentId = "comp1",
                    StartPinName = "o1",
                    EndComponentId = "comp2",
                    EndPinName = "o1",
                    Segments = new List<PathSegmentDto>
                    {
                        new() { Type = "straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0 }
                    }
                }
            }
        };

        // Act
        var group = ComponentGroupSerializer.FromDto(dto, lookup);

        // Assert
        var restoredPath = group.InternalPaths.ShouldHaveSingleItem();
        restoredPath.Layer.ShouldBeNull();
        restoredPath.DataType.ShouldBeNull();
    }

    /// <summary>
    /// Creates a test component with a single pin named "o1".
    /// </summary>
    private static Component CreateTestComponent(string identifier, double x, double y)
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
            new List<PhysicalPin> { pin })
        {
            PhysicalX = x,
            PhysicalY = y
        };

        pin.ParentComponent = component;
        return component;
    }
}
