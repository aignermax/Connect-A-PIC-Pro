using System.Text.Json.Nodes;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Round-trip tests for per-connection routing settings across the user-group template
/// path (field report round 5, finding b): bend style, radius, width, freeze flag and
/// manual bend overrides must survive save → instantiate, and templates written before
/// these fields existed must still load with "Auto" defaults.
/// </summary>
public class GroupTemplateConnectionSettingsTests
{
    [Fact]
    public void SerializeAndDeserialize_PreservesConnectionSettings()
    {
        // Arrange
        var group = CreateGroupWithConfiguredPath();

        // Act
        var json = GroupTemplateSerializer.Serialize(group);
        var result = GroupTemplateSerializer.Deserialize(json);

        // Assert
        result.ShouldNotBeNull();
        var path = result!.InternalPaths.ShouldHaveSingleItem();
        path.ConnectionType.ShouldBe(WaveguideType.SBend);
        path.BendRadiusMicrometers.ShouldBe(25.0);
        path.WidthMicrometers.ShouldBe(1.2);
        path.IsRouteFrozen.ShouldBeTrue();
        path.PropagationLossDbPerCm.ShouldBe(2.5);
        path.BendRadiusOverrides.Count.ShouldBe(2);
        path.BendRadiusOverrides[0].ShouldBe(12.5);
        path.BendRadiusOverrides[2].ShouldBe(30.0);
    }

    [Fact]
    public void Deserialize_TemplateInstantiation_KeepsSettingsThroughDeepCopy()
    {
        // The library instantiates templates via DeepCopy (GroupLibraryManager);
        // the placed instance must still carry the connection settings.
        var group = CreateGroupWithConfiguredPath();
        var json = GroupTemplateSerializer.Serialize(group);
        var template = GroupTemplateSerializer.Deserialize(json)!;

        // Act
        var instance = template.DeepCopy();

        // Assert
        var path = instance.InternalPaths.ShouldHaveSingleItem();
        path.ConnectionType.ShouldBe(WaveguideType.SBend);
        path.BendRadiusMicrometers.ShouldBe(25.0);
        path.WidthMicrometers.ShouldBe(1.2);
        path.IsRouteFrozen.ShouldBeTrue();
        path.BendRadiusOverrides[0].ShouldBe(12.5);
    }

    [Fact]
    public void Deserialize_LegacyTemplateWithoutSettingsFields_LoadsWithAutoDefaults()
    {
        // Arrange - simulate a template written before the settings fields existed
        // by stripping them from freshly serialized JSON.
        var group = CreateGroupWithConfiguredPath();
        var json = GroupTemplateSerializer.Serialize(group);
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var pathNode in root["InternalPaths"]!.AsArray())
        {
            var pathObject = pathNode!.AsObject();
            pathObject.Remove("ConnectionType");
            pathObject.Remove("BendRadiusMicrometers");
            pathObject.Remove("WidthMicrometers");
            pathObject.Remove("IsRouteFrozen");
            pathObject.Remove("PropagationLossDbPerCm");
            pathObject.Remove("BendRadiusOverrides");
        }
        var legacyJson = root.ToJsonString();

        // Act
        var result = GroupTemplateSerializer.Deserialize(legacyJson);

        // Assert - loads without error and falls back to the model defaults
        result.ShouldNotBeNull();
        var path = result!.InternalPaths.ShouldHaveSingleItem();
        path.ConnectionType.ShouldBe(WaveguideType.Auto);
        path.BendRadiusMicrometers.ShouldBe(new WaveguideConnection().BendRadiusMicrometers);
        path.WidthMicrometers.ShouldBe(new WaveguideConnection().WidthMicrometers);
        path.IsRouteFrozen.ShouldBeFalse();
        path.BendRadiusOverrides.ShouldBeEmpty();
    }

    [Fact]
    public void Deserialize_UnknownConnectionType_FallsBackToAuto()
    {
        // Arrange - a future/unknown style name must not crash the load
        var group = CreateGroupWithConfiguredPath();
        var json = GroupTemplateSerializer.Serialize(group);
        var root = JsonNode.Parse(json)!.AsObject();
        root["InternalPaths"]![0]!["ConnectionType"] = "HyperbolicSpline";

        // Act
        var result = GroupTemplateSerializer.Deserialize(root.ToJsonString());

        // Assert
        result.ShouldNotBeNull();
        result!.InternalPaths[0].ConnectionType.ShouldBe(WaveguideType.Auto);
    }

    /// <summary>
    /// Creates a two-component group whose single internal path carries
    /// non-default connection settings.
    /// </summary>
    private static ComponentGroup CreateGroupWithConfiguredPath()
    {
        var group = new ComponentGroup("SettingsGroup") { PhysicalX = 0, PhysicalY = 0 };
        var comp1 = CreateTestComponent($"c1_{Guid.NewGuid():N}", 0, 0);
        var comp2 = CreateTestComponent($"c2_{Guid.NewGuid():N}", 100, 0);
        group.AddChild(comp1);
        group.AddChild(comp2);

        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(50, 0, 100, 0, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            StartPin = comp1.PhysicalPins[1],
            EndPin = comp2.PhysicalPins[0],
            Path = routedPath,
            ConnectionType = WaveguideType.SBend,
            BendRadiusMicrometers = 25.0,
            WidthMicrometers = 1.2,
            IsRouteFrozen = true,
            PropagationLossDbPerCm = 2.5
        };
        frozenPath.BendRadiusOverrides[0] = 12.5;
        frozenPath.BendRadiusOverrides[2] = 30.0;
        group.AddInternalPath(frozenPath);

        return group;
    }

    /// <summary>
    /// Creates a test component with an input pin (a0) and an output pin (b0).
    /// </summary>
    private static Component CreateTestComponent(string id, double x, double y)
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
                new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 0, AngleDegrees = 180 },
                new() { Name = "b0", OffsetXMicrometers = 50, OffsetYMicrometers = 0, AngleDegrees = 0 }
            })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 50,
            HeightMicrometers = 30
        };
    }
}
