using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Model-level tests for the per-connection routing settings carried by
/// <see cref="FrozenWaveguidePath"/> (field report round 5, finding a/b):
/// bend style, bend radius, width, freeze flag and manual per-bend overrides
/// must survive the connection → frozen path → connection round-trip.
/// </summary>
public class FrozenWaveguidePathSettingsTests
{
    [Fact]
    public void CaptureSettingsFrom_CopiesAllConnectionSettings()
    {
        // Arrange
        var connection = CreateConfiguredConnection();
        var frozenPath = new FrozenWaveguidePath { Path = new RoutedPath() };

        // Act
        frozenPath.CaptureSettingsFrom(connection);

        // Assert
        frozenPath.ConnectionType.ShouldBe(WaveguideType.SBend);
        frozenPath.BendRadiusMicrometers.ShouldBe(25.0);
        frozenPath.WidthMicrometers.ShouldBe(1.2);
        frozenPath.IsRouteFrozen.ShouldBeTrue();
        frozenPath.PropagationLossDbPerCm.ShouldBe(2.5);
        frozenPath.BendRadiusOverrides.Count.ShouldBe(2);
        frozenPath.BendRadiusOverrides[0].ShouldBe(12.5);
        frozenPath.BendRadiusOverrides[2].ShouldBe(30.0);
    }

    [Fact]
    public void ApplySettingsTo_RestoresAllSettingsOnDefaultConnection()
    {
        // Arrange
        var source = CreateConfiguredConnection();
        var frozenPath = new FrozenWaveguidePath { Path = new RoutedPath() };
        frozenPath.CaptureSettingsFrom(source);

        var target = new WaveguideConnection(); // fresh default: Auto, 10 µm, 0.5 µm

        // Act
        frozenPath.ApplySettingsTo(target);

        // Assert
        target.Type.ShouldBe(WaveguideType.SBend);
        target.BendRadiusMicrometers.ShouldBe(25.0);
        target.WidthMicrometers.ShouldBe(1.2);
        target.IsRouteFrozen.ShouldBeTrue();
        target.PropagationLossDbPerCm.ShouldBe(2.5);
        target.BendRadiusOverrides.Count.ShouldBe(2);
        target.BendRadiusOverrides[0].ShouldBe(12.5);
        target.BendRadiusOverrides[2].ShouldBe(30.0);
    }

    [Fact]
    public void CaptureSettingsFrom_OverwritesPreviouslyStoredOverrides()
    {
        // Arrange
        var frozenPath = new FrozenWaveguidePath { Path = new RoutedPath() };
        frozenPath.BendRadiusOverrides[7] = 99.0;

        var connection = new WaveguideConnection();
        connection.BendRadiusOverrides[1] = 15.0;

        // Act
        frozenPath.CaptureSettingsFrom(connection);

        // Assert - stale entries are gone, only the connection's overrides remain
        frozenPath.BendRadiusOverrides.Count.ShouldBe(1);
        frozenPath.BendRadiusOverrides[1].ShouldBe(15.0);
    }

    [Fact]
    public void Defaults_MatchWaveguideConnectionDefaults()
    {
        // A frozen path without captured settings must behave like a default connection
        // so that legacy groups (created before settings persistence) keep their behavior.
        var frozenPath = new FrozenWaveguidePath { Path = new RoutedPath() };
        var defaultConnection = new WaveguideConnection();

        frozenPath.ConnectionType.ShouldBe(WaveguideType.Auto);
        frozenPath.BendRadiusMicrometers.ShouldBe(defaultConnection.BendRadiusMicrometers);
        frozenPath.WidthMicrometers.ShouldBe(defaultConnection.WidthMicrometers);
        frozenPath.IsRouteFrozen.ShouldBeFalse();
        frozenPath.BendRadiusOverrides.ShouldBeEmpty();
    }

    [Fact]
    public void Clone_PreservesConnectionSettings()
    {
        // Arrange
        var connection = CreateConfiguredConnection();
        var frozenPath = new FrozenWaveguidePath { Path = new RoutedPath() };
        frozenPath.CaptureSettingsFrom(connection);

        // Act
        var clone = (FrozenWaveguidePath)frozenPath.Clone();

        // Assert
        clone.ConnectionType.ShouldBe(WaveguideType.SBend);
        clone.BendRadiusMicrometers.ShouldBe(25.0);
        clone.WidthMicrometers.ShouldBe(1.2);
        clone.IsRouteFrozen.ShouldBeTrue();
        clone.PropagationLossDbPerCm.ShouldBe(2.5);
        clone.BendRadiusOverrides.Count.ShouldBe(2);
        clone.BendRadiusOverrides[0].ShouldBe(12.5);
    }

    [Fact]
    public void Clone_OverridesAreIndependentOfSource()
    {
        // Arrange
        var frozenPath = new FrozenWaveguidePath { Path = new RoutedPath() };
        frozenPath.BendRadiusOverrides[0] = 12.5;

        // Act
        var clone = (FrozenWaveguidePath)frozenPath.Clone();
        clone.BendRadiusOverrides[0] = 99.0;

        // Assert - mutating the clone must not leak back into the source
        frozenPath.BendRadiusOverrides[0].ShouldBe(12.5);
    }

    [Fact]
    public void DeepCopy_GroupWithConfiguredPath_PreservesConnectionSettings()
    {
        // Template instantiation clones the template group (GroupLibraryManager
        // → DeepCopy); the clone must keep the per-connection settings.
        var group = new ComponentGroup("SettingsGroup");
        var comp1 = CreateComponentWithPin("c1", 0, 0);
        var comp2 = CreateComponentWithPin("c2", 100, 0);
        group.AddChild(comp1);
        group.AddChild(comp2);

        var connection = CreateConfiguredConnection();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        var frozenPath = new FrozenWaveguidePath
        {
            Path = path,
            StartPin = comp1.PhysicalPins[0],
            EndPin = comp2.PhysicalPins[0]
        };
        frozenPath.CaptureSettingsFrom(connection);
        group.AddInternalPath(frozenPath);

        // Act
        var copy = group.DeepCopy();

        // Assert
        var copiedPath = copy.InternalPaths.ShouldHaveSingleItem();
        copiedPath.ConnectionType.ShouldBe(WaveguideType.SBend);
        copiedPath.BendRadiusMicrometers.ShouldBe(25.0);
        copiedPath.WidthMicrometers.ShouldBe(1.2);
        copiedPath.IsRouteFrozen.ShouldBeTrue();
        copiedPath.PropagationLossDbPerCm.ShouldBe(2.5);
        copiedPath.BendRadiusOverrides[0].ShouldBe(12.5);
        copiedPath.BendRadiusOverrides[2].ShouldBe(30.0);
    }

    /// <summary>
    /// Creates a connection with every user-editable routing setting set to a non-default value.
    /// </summary>
    private static WaveguideConnection CreateConfiguredConnection()
    {
        var connection = new WaveguideConnection
        {
            Type = WaveguideType.SBend,
            BendRadiusMicrometers = 25.0,
            WidthMicrometers = 1.2,
            IsRouteFrozen = true,
            PropagationLossDbPerCm = 2.5
        };
        connection.BendRadiusOverrides[0] = 12.5;
        connection.BendRadiusOverrides[2] = 30.0;
        return connection;
    }

    /// <summary>
    /// Creates a minimal component with a single pin for path wiring.
    /// </summary>
    private static Component CreateComponentWithPin(string id, double x, double y)
    {
        var pin = new PhysicalPin
        {
            Name = "o1",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = 0
        };

        var component = new Component(
            new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            id,
            new DiscreteRotation(),
            new List<PhysicalPin> { pin })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 50,
            HeightMicrometers = 30
        };
        pin.ParentComponent = component;
        return component;
    }
}
