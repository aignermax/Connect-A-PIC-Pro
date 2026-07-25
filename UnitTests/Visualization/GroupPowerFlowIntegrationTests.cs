using System.Numerics;
using Avalonia.Headless.XUnit;
using CAP.Avalonia.Visualization;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Visualization;

/// <summary>
/// Integration tests verifying that power flow visualization works correctly
/// for frozen paths inside component groups.
/// </summary>
public class GroupPowerFlowIntegrationTests
{
    /// <summary>
    /// Verifies that PowerFlowVisualizer collects frozen paths from groups
    /// and analyzes them correctly.
    /// </summary>
    [Fact]
    public void UpdateFromSimulation_WithComponentGroups_AnalyzesFrozenPaths()
    {
        // Arrange
        var visualizer = new PowerFlowVisualizer();

        var (group, frozenPath) = CreateTestGroupWithFrozenPath();
        var components = new List<Component> { group };
        var connections = new List<WaveguideConnection>();

        var fieldResults = CreateFieldResultsForFrozenPath(frozenPath);

        // Act
        visualizer.UpdateFromSimulation(connections, components, fieldResults);

        // Assert
        visualizer.CurrentResult.ShouldNotBeNull();
        visualizer.CurrentResult!.ConnectionFlows.ContainsKey(frozenPath.PathId).ShouldBeTrue();

        var flow = visualizer.CurrentResult.ConnectionFlows[frozenPath.PathId];
        flow.AveragePower.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Verifies that nested groups' frozen paths are also collected and analyzed.
    /// </summary>
    [Fact]
    public void UpdateFromSimulation_WithNestedGroups_AnalyzesAllFrozenPaths()
    {
        // Arrange
        var visualizer = new PowerFlowVisualizer();

        // Create outer group with one frozen path
        var (outerGroup, outerPath) = CreateTestGroupWithFrozenPath();

        // Create inner group with one frozen path
        var (innerGroup, innerPath) = CreateTestGroupWithFrozenPath();

        // Nest the inner group inside the outer group
        outerGroup.AddChild(innerGroup);

        var components = new List<Component> { outerGroup };
        var connections = new List<WaveguideConnection>();

        var outerFields = CreateFieldResultsForFrozenPath(outerPath);
        var innerFields = CreateFieldResultsForFrozenPath(innerPath);

        var allFields = new Dictionary<Guid, Complex>(outerFields);
        foreach (var kvp in innerFields)
            allFields[kvp.Key] = kvp.Value;

        // Act
        visualizer.UpdateFromSimulation(connections, components, allFields);

        // Assert
        visualizer.CurrentResult.ShouldNotBeNull();
        visualizer.CurrentResult!.ConnectionFlows.Count.ShouldBe(2);
        visualizer.CurrentResult.ConnectionFlows.ContainsKey(outerPath.PathId).ShouldBeTrue();
        visualizer.CurrentResult.ConnectionFlows.ContainsKey(innerPath.PathId).ShouldBeTrue();
    }

    /// <summary>
    /// Verifies that both regular connections and frozen paths are visualized together.
    /// </summary>
    [Fact]
    public void UpdateFromSimulation_WithMixedElements_AnalyzesBoth()
    {
        // Arrange
        var visualizer = new PowerFlowVisualizer();

        var (group, frozenPath) = CreateTestGroupWithFrozenPath();
        var (connection, connFields) = CreateTestConnection();

        var components = new List<Component> { group };
        var connections = new List<WaveguideConnection> { connection };

        var frozenFields = CreateFieldResultsForFrozenPath(frozenPath);
        var allFields = new Dictionary<Guid, Complex>(frozenFields);
        foreach (var kvp in connFields)
            allFields[kvp.Key] = kvp.Value;

        // Act
        visualizer.UpdateFromSimulation(connections, components, allFields);

        // Assert
        visualizer.CurrentResult.ShouldNotBeNull();
        visualizer.CurrentResult!.ConnectionFlows.Count.ShouldBe(2);
        visualizer.CurrentResult.ConnectionFlows.ContainsKey(frozenPath.PathId).ShouldBeTrue();
        visualizer.CurrentResult.ConnectionFlows.ContainsKey(connection.Id).ShouldBeTrue();
    }

    /// <summary>
    /// Verifies that GetFlowForConnection works for frozen path IDs.
    /// </summary>
    [Fact]
    public void GetFlowForConnection_WithFrozenPathId_ReturnsFlow()
    {
        // Arrange
        var visualizer = new PowerFlowVisualizer();

        var (group, frozenPath) = CreateTestGroupWithFrozenPath();
        var components = new List<Component> { group };
        var connections = new List<WaveguideConnection>();

        var fieldResults = CreateFieldResultsForFrozenPath(frozenPath);

        visualizer.UpdateFromSimulation(connections, components, fieldResults);

        // Act
        var flow = visualizer.GetFlowForConnection(frozenPath.PathId);

        // Assert
        flow.ShouldNotBeNull();
        flow!.ConnectionId.ShouldBe(frozenPath.PathId);
        flow.AveragePower.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Verifies that frozen paths show non-zero power when only the parent group's
    /// external pins are in fieldResults (not the frozen path's internal pins).
    /// This is the realistic scenario after grouping: internal pins are hidden inside
    /// the group's S-Matrix and do not appear in the simulation field results.
    /// </summary>
    [Fact]
    public void UpdateFromSimulation_FrozenPathPinsAbsent_UsesGroupExternalPinFallback()
    {
        // Arrange
        var visualizer = new PowerFlowVisualizer();

        // Create group with two internal components and an external pin
        var (group, frozenPath, externalLogicalPin) = CreateGroupWithInternalFrozenPathAndExternalPin();
        var components = new List<Component> { group };
        var connections = new List<WaveguideConnection>();

        // Only put the EXTERNAL group pin's amplitude in fieldResults — NOT the frozen path pins.
        // This simulates real simulation results where internal pins are hidden in the group S-Matrix.
        var fieldResults = new Dictionary<Guid, Complex>
        {
            [externalLogicalPin.IDOutFlow] = new Complex(1.0, 0)
        };

        // Act
        visualizer.UpdateFromSimulation(connections, components, fieldResults);

        // Assert: frozen path should have non-zero power via the fallback mechanism
        visualizer.CurrentResult.ShouldNotBeNull();
        visualizer.CurrentResult!.ConnectionFlows.ContainsKey(frozenPath.PathId).ShouldBeTrue();

        var flow = visualizer.CurrentResult.ConnectionFlows[frozenPath.PathId];
        flow.AveragePower.ShouldBeGreaterThan(0,
            "Frozen paths inside groups should show power even when their pin GUIDs " +
            "are absent from fieldResults — power should be estimated from the group's external pins.");
    }

    /// <summary>
    /// Verifies that grouping components preserves power visualization:
    /// the same signal that was visible in ungrouped connections should remain
    /// visible (non-zero) in frozen paths after grouping.
    /// </summary>
    [AvaloniaFact]
    public void UpdateFromSimulation_AfterGrouping_PreservesPowerVisualization()
    {
        // Arrange
        var visualizer = new PowerFlowVisualizer();

        // Step 1: Simulate "before grouping" — two connections with known field results
        var externalLogicalPin1 = new Pin("ext_pin_1", 0, MatterType.Light, RectSide.Left);
        var externalLogicalPin2 = new Pin("ext_pin_2", 1, MatterType.Light, RectSide.Right);

        var externalFields = new Dictionary<Guid, Complex>
        {
            [externalLogicalPin1.IDOutFlow] = new Complex(1.0, 0),
            [externalLogicalPin2.IDInFlow] = new Complex(0.9, 0)
        };

        // Step 2: "After grouping" — internal connections become frozen paths,
        // only external group pins remain in fieldResults
        var (group, frozenPath, groupExternalPin) = CreateGroupWithInternalFrozenPathAndExternalPin();
        var components = new List<Component> { group };
        var connections = new List<WaveguideConnection>();

        // fieldResults after grouping: only group's external pins (not internal frozen path pins)
        var groupedFieldResults = new Dictionary<Guid, Complex>
        {
            [groupExternalPin.IDOutFlow] = new Complex(1.0, 0)
        };

        // Act
        visualizer.UpdateFromSimulation(connections, components, groupedFieldResults);

        // Assert: frozen paths must show power (not gray/faded)
        visualizer.CurrentResult.ShouldNotBeNull();
        var flow = visualizer.CurrentResult.ConnectionFlows[frozenPath.PathId];
        flow.AveragePower.ShouldBeGreaterThan(0,
            "After grouping, frozen paths should still show colored light propagation.");

        var powerPen = PowerFlowRenderer.CreatePowerPen(flow, visualizer.FadeThresholdDb);
        powerPen.ShouldNotBeNull();

        // The frozen path's power fraction should be non-trivial (not faded out)
        visualizer.CurrentResult.IsFadedOut(frozenPath.PathId).ShouldBeFalse(
            "Frozen paths with active light should not be rendered as gray/faded.");
    }

    /// <summary>
    /// Verifies that when a group has no light entering (all external pin amplitudes are zero),
    /// the frozen paths correctly show no power (gray) rather than spurious color.
    /// </summary>
    [Fact]
    public void UpdateFromSimulation_GroupWithNoLight_FrozenPathsRemainFaded()
    {
        // Arrange
        var visualizer = new PowerFlowVisualizer();
        var (group, frozenPath, _) = CreateGroupWithInternalFrozenPathAndExternalPin();
        var components = new List<Component> { group };
        var connections = new List<WaveguideConnection>();

        // fieldResults with zero amplitude (no light)
        var fieldResults = new Dictionary<Guid, Complex>();

        // Act
        visualizer.UpdateFromSimulation(connections, components, fieldResults);

        // Assert: no light → frozen paths should have zero power
        visualizer.CurrentResult.ShouldNotBeNull();
        var flow = visualizer.CurrentResult.ConnectionFlows[frozenPath.PathId];
        flow.AveragePower.ShouldBe(0.0,
            "When no light enters the group, frozen paths should correctly show no power.");
    }

    /// <summary>
    /// Creates a group that realistically models what happens after CreateGroupCommand:
    /// - Two internal components connected by a frozen path (internal pins, not in fieldResults)
    /// - One external group pin pointing to a boundary pin (which IS in fieldResults)
    /// </summary>
    private static (ComponentGroup group, FrozenWaveguidePath frozenPath, Pin externalLogicalPin)
        CreateGroupWithInternalFrozenPathAndExternalPin()
    {
        var group = new ComponentGroup("TestGroup");

        // comp1 has two logical pins: one internal (used for frozen path), one external
        var internalLogicalPin1 = new Pin("int_pin1", 0, MatterType.Light, RectSide.Right);
        var externalLogicalPin1 = new Pin("ext_pin1", 1, MatterType.Light, RectSide.Left);

        var internalPhysicalPin1 = new PhysicalPin
        {
            Name = "int_pin1",
            LogicalPin = internalLogicalPin1,
            OffsetXMicrometers = 50,
            OffsetYMicrometers = 25
        };
        var externalPhysicalPin1 = new PhysicalPin
        {
            Name = "ext_pin1",
            LogicalPin = externalLogicalPin1,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25
        };

        var comp1 = new Component(
            new Dictionary<int, SMatrix>(), new List<Slider>(), "test", "",
            new Part[1, 1] { { new Part() } }, 0, "comp1", new DiscreteRotation(),
            new List<PhysicalPin> { internalPhysicalPin1, externalPhysicalPin1 })
        { PhysicalX = 0, PhysicalY = 0, WidthMicrometers = 50, HeightMicrometers = 50 };
        internalPhysicalPin1.ParentComponent = comp1;
        externalPhysicalPin1.ParentComponent = comp1;

        // comp2 has one internal logical pin (used for frozen path, not exposed externally)
        var internalLogicalPin2 = new Pin("int_pin2", 2, MatterType.Light, RectSide.Left);
        var internalPhysicalPin2 = new PhysicalPin
        {
            Name = "int_pin2",
            LogicalPin = internalLogicalPin2,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25
        };

        var comp2 = new Component(
            new Dictionary<int, SMatrix>(), new List<Slider>(), "test", "",
            new Part[1, 1] { { new Part() } }, 0, "comp2", new DiscreteRotation(),
            new List<PhysicalPin> { internalPhysicalPin2 })
        { PhysicalX = 100, PhysicalY = 0, WidthMicrometers = 50, HeightMicrometers = 50 };
        internalPhysicalPin2.ParentComponent = comp2;

        group.AddChild(comp1);
        group.AddChild(comp2);

        // Frozen path: internal pin of comp1 → internal pin of comp2
        // Neither pin is exposed as a group external pin (simulating real grouping behavior)
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(50, 25, 100, 25, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            Path = path,
            StartPin = internalPhysicalPin1,
            EndPin = internalPhysicalPin2
        };
        group.AddInternalPath(frozenPath);

        // External group pin: points to comp1's external pin (this IS in fieldResults)
        var groupPin = new GroupPin
        {
            Name = "group_ext_pin1",
            InternalPin = externalPhysicalPin1,
            RelativeX = 0,
            RelativeY = 25,
            AngleDegrees = 180
        };
        group.AddExternalPin(groupPin);

        return (group, frozenPath, externalLogicalPin1);
    }

    /// <summary>
    /// Helper method to create a test component group with a frozen path.
    /// </summary>
    private static (ComponentGroup group, FrozenWaveguidePath frozenPath)
        CreateTestGroupWithFrozenPath()
    {
        var group = new ComponentGroup("TestGroup");

        // Create two simple components
        var comp1 = CreateSimpleComponent("comp1", 0, 0);
        var comp2 = CreateSimpleComponent("comp2", 100, 0);

        group.AddChild(comp1);
        group.AddChild(comp2);

        // Create a frozen path between them
        var startPin = comp1.PhysicalPins[0];
        var endPin = comp2.PhysicalPins[0];

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            Path = path,
            StartPin = startPin,
            EndPin = endPin
        };

        group.AddInternalPath(frozenPath);

        return (group, frozenPath);
    }

    /// <summary>
    /// Helper method to create a simple component with one pin.
    /// </summary>
    private static Component CreateSimpleComponent(string id, double x, double y)
    {
        var logicalPin = new Pin("pin0", 0, MatterType.Light, RectSide.Right);
        var physicalPin = new PhysicalPin
        {
            Name = "pin0",
            LogicalPin = logicalPin,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = 0
        };

        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1] { { new Part() } },
            0,
            id,
            new DiscreteRotation(),
            new List<PhysicalPin> { physicalPin })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 50,
            HeightMicrometers = 50
        };

        physicalPin.ParentComponent = component;
        return component;
    }

    /// <summary>
    /// Helper method to create field results for a frozen path.
    /// </summary>
    private static Dictionary<Guid, Complex> CreateFieldResultsForFrozenPath(FrozenWaveguidePath path)
    {
        var fields = new Dictionary<Guid, Complex>();

        if (path.StartPin.LogicalPin != null)
        {
            fields[path.StartPin.LogicalPin.IDOutFlow] = new Complex(1.0, 0);
        }

        if (path.EndPin.LogicalPin != null)
        {
            fields[path.EndPin.LogicalPin.IDInFlow] = new Complex(0.9, 0);
        }

        return fields;
    }

    /// <summary>
    /// Helper method to create a test waveguide connection.
    /// </summary>
    private static (WaveguideConnection connection, Dictionary<Guid, Complex> fields)
        CreateTestConnection()
    {
        var startLogicalPin = new Pin("conn_start", 0, MatterType.Light, RectSide.Left);
        var endLogicalPin = new Pin("conn_end", 1, MatterType.Light, RectSide.Right);

        var startPhysicalPin = new PhysicalPin
        {
            Name = "conn_start",
            LogicalPin = startLogicalPin,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = 0
        };

        var endPhysicalPin = new PhysicalPin
        {
            Name = "conn_end",
            LogicalPin = endLogicalPin,
            OffsetXMicrometers = 200,
            OffsetYMicrometers = 0,
            AngleDegrees = 180
        };

        var connection = new WaveguideConnection
        {
            StartPin = startPhysicalPin,
            EndPin = endPhysicalPin,
            Type = WaveguideType.Auto
        };

        var fields = new Dictionary<Guid, Complex>
        {
            [startLogicalPin.IDOutFlow] = new Complex(1.0, 0),
            [endLogicalPin.IDInFlow] = new Complex(0.9, 0)
        };

        return (connection, fields);
    }
}
