using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Round-trip tests for per-connection routing settings across grouping (field report
/// round 5, finding a): a connection configured with an explicit bend style, radius,
/// width, freeze flag and manual bend overrides must show those exact values again in
/// the group-edit sub-canvas (the editor UI reads <c>Connection.Type</c>) and after
/// ungrouping — not fall back to "Auto" defaults.
/// </summary>
public class GroupConnectionSettingsRoundtripTests
{
    [Fact]
    public void CreateGroup_CapturesConnectionSettingsInFrozenPath()
    {
        // Arrange
        var (canvas, vm1, vm2, connection) = CreateCanvasWithConfiguredConnection();

        // Act
        var command = new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 });
        command.Execute();

        // Assert
        var group = GetCreatedGroup(canvas);
        var frozenPath = group.InternalPaths.ShouldHaveSingleItem();
        frozenPath.ConnectionType.ShouldBe(WaveguideType.SBend);
        frozenPath.BendRadiusMicrometers.ShouldBe(25.0);
        frozenPath.WidthMicrometers.ShouldBe(1.2);
        frozenPath.IsRouteFrozen.ShouldBeTrue();
        frozenPath.PropagationLossDbPerCm.ShouldBe(2.5);
        frozenPath.BendRadiusOverrides[0].ShouldBe(12.5);
    }

    [Fact]
    public void EnterGroupEditMode_ConnectionShowsStoredSettings_NotAutoDefaults()
    {
        // Arrange - group two components whose connection has explicit settings
        var (canvas, vm1, vm2, _) = CreateCanvasWithConfiguredConnection();
        new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 }).Execute();
        var group = GetCreatedGroup(canvas);

        // Act - open the group editor (loads internal paths as live connections)
        canvas.EnterGroupEditMode(group);

        // Assert - the editor connection carries the stored settings
        var editConnection = canvas.Connections.ShouldHaveSingleItem().Connection;
        editConnection.Type.ShouldBe(WaveguideType.SBend);
        editConnection.BendRadiusMicrometers.ShouldBe(25.0);
        editConnection.WidthMicrometers.ShouldBe(1.2);
        editConnection.IsRouteFrozen.ShouldBeTrue();
        editConnection.PropagationLossDbPerCm.ShouldBe(2.5);
        editConnection.BendRadiusOverrides[0].ShouldBe(12.5);
    }

    [Fact]
    public void EnterGroupEditMode_RoutingPanelShowsStoredStyle()
    {
        // Arrange
        var (canvas, vm1, vm2, _) = CreateCanvasWithConfiguredConnection();
        new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 }).Execute();
        canvas.EnterGroupEditMode(GetCreatedGroup(canvas));

        // Act - select the connection in the routing panel (as the UI does)
        var routingPanel = new ConnectionRoutingViewModel(canvas)
        {
            SelectedConnection = canvas.Connections.Single()
        };

        // Assert - the dropdown shows the stored style, not "Auto"
        routingPanel.SelectedStyle.ShouldBe(WaveguideType.SBend);
    }

    [Fact]
    public void ExitGroupEditMode_KeepsConnectionSettingsInFrozenPath()
    {
        // Arrange
        var (canvas, vm1, vm2, _) = CreateCanvasWithConfiguredConnection();
        new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 }).Execute();
        var group = GetCreatedGroup(canvas);
        canvas.EnterGroupEditMode(group);

        // Act - leave the editor without touching anything
        canvas.ExitGroupEditMode();

        // Assert - the re-frozen path still carries the settings
        var frozenPath = group.InternalPaths.ShouldHaveSingleItem();
        frozenPath.ConnectionType.ShouldBe(WaveguideType.SBend);
        frozenPath.BendRadiusMicrometers.ShouldBe(25.0);
        frozenPath.WidthMicrometers.ShouldBe(1.2);
        frozenPath.IsRouteFrozen.ShouldBeTrue();
        frozenPath.PropagationLossDbPerCm.ShouldBe(2.5);
        frozenPath.BendRadiusOverrides[0].ShouldBe(12.5);
    }

    [Fact]
    public async Task Ungroup_RestoresConnectionSettings()
    {
        // Arrange
        var (canvas, vm1, vm2, _) = CreateCanvasWithConfiguredConnection();
        new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 }).Execute();
        var group = GetCreatedGroup(canvas);

        // Act - ungroup, then let a full routing pass complete (settings must survive it)
        new UngroupCommand(canvas, group).Execute();
        await canvas.RecalculateRoutesAsync();

        // Assert - the restored connection keeps the configured settings
        var restored = canvas.Connections.ShouldHaveSingleItem().Connection;
        restored.Type.ShouldBe(WaveguideType.SBend);
        restored.BendRadiusMicrometers.ShouldBe(25.0);
        restored.WidthMicrometers.ShouldBe(1.2);
        restored.PropagationLossDbPerCm.ShouldBe(2.5);
    }

    [Fact]
    public async Task Ungroup_FrozenAutoRoute_KeepsGeometryAndOverrides()
    {
        // Arrange - an Auto connection whose route the user froze and hand-edited
        var (canvas, vm1, vm2, connection) = CreateCanvasWithConfiguredConnection();
        connection.Type = WaveguideType.Auto;

        new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 }).Execute();
        var group = GetCreatedGroup(canvas);

        // Act - ungroup, then let a full routing pass complete (frozen geometry must survive it)
        new UngroupCommand(canvas, group).Execute();
        await canvas.RecalculateRoutesAsync();

        // Assert - freeze flag, overrides and cached geometry survive
        var restored = canvas.Connections.ShouldHaveSingleItem().Connection;
        restored.IsRouteFrozen.ShouldBeTrue();
        restored.BendRadiusOverrides[0].ShouldBe(12.5);
        restored.RoutedPath.ShouldNotBeNull();
        restored.RoutedPath!.Segments.ShouldNotBeEmpty();
    }

    [Fact]
    public void EnterGroupEditMode_TransmissionReflectsStoredLoss_NotTheManagerDefault()
    {
        // Round-5 review [5]: AddConnectionWithCachedRoute computes the transmission with
        // the manager DEFAULT loss before the stored settings are applied; without the
        // recompute a simulation in edit mode silently uses 0.5 dB/cm instead of 2.5.
        var (canvas, vm1, vm2, _) = CreateCanvasWithConfiguredConnection();
        new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 }).Execute();

        canvas.EnterGroupEditMode(GetCreatedGroup(canvas));

        var editConnection = canvas.Connections.ShouldHaveSingleItem().Connection;
        var lengthCm = editConnection.RoutedPath!.TotalLengthMicrometers / 10_000.0;
        editConnection.TotalLossDb.ShouldBe(2.5 * lengthCm, 1e-9,
            "the transmission must be computed from the restored 2.5 dB/cm, not the 0.5 dB/cm default");
    }

    [Fact]
    public async Task Ungroup_RestoredGeometryIsIndependent_OfTheGroupsStoredUndoState()
    {
        // Round-5 review [4]: RestoreCachedPath used to alias the group's stored
        // InternalPaths — a later in-place canvas edit (bend handles mutate segments)
        // would corrupt the geometry the group re-renders after Undo.
        var (canvas, vm1, vm2, connection) = CreateCanvasWithConfiguredConnection();
        connection.Type = WaveguideType.Auto;
        new CreateGroupCommand(canvas, new List<ComponentViewModel> { vm1, vm2 }).Execute();
        var group = GetCreatedGroup(canvas);
        var storedPath = group.InternalPaths.ShouldHaveSingleItem().Path;
        var storedEndBefore = storedPath.Segments[^1].EndPoint;

        new UngroupCommand(canvas, group).Execute();
        await canvas.RecalculateRoutesAsync();

        var restored = canvas.Connections.ShouldHaveSingleItem().Connection;
        restored.RoutedPath.ShouldNotBeSameAs(storedPath,
            "the live connection must not share the RoutedPath object kept for Undo");

        // Simulate an in-place canvas edit of the live geometry.
        restored.RoutedPath!.Segments[^1].EndPoint = (999, 999);

        storedPath.Segments[^1].EndPoint.ShouldBe(storedEndBefore,
            "the group's stored undo geometry must stay untouched by live edits");
    }

    /// <summary>
    /// Builds a canvas with two pinned components joined by a connection that has
    /// every user-editable routing setting set to a non-default value, including a
    /// routed path so the freeze/override state is meaningful.
    /// </summary>
    private static (DesignCanvasViewModel Canvas, ComponentViewModel Vm1,
        ComponentViewModel Vm2, WaveguideConnection Connection) CreateCanvasWithConfiguredConnection()
    {
        var canvas = new DesignCanvasViewModel();
        // Pin on the right edge of Comp1 facing east, pin on the left edge of Comp2
        // facing west: the straight frozen path between them crosses free space only,
        // so the collision-unfreeze pass has no reason to discard it.
        var comp1 = CreateComponentWithPin("Comp1", 100, 100, pinOffsetX: 50, pinAngleDegrees: 0);
        var comp2 = CreateComponentWithPin("Comp2", 300, 100, pinOffsetX: 0, pinAngleDegrees: 180);
        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        var connectionVm = canvas.ConnectPins(comp1.PhysicalPins[0], comp2.PhysicalPins[0]);
        var connection = connectionVm!.Connection;
        connection.Type = WaveguideType.SBend;
        connection.BendRadiusMicrometers = 25.0;
        connection.WidthMicrometers = 1.2;
        connection.PropagationLossDbPerCm = 2.5;

        // Give the connection a concrete routed path and hand-edited state, mirroring
        // a user who froze the route and dragged a bend-radius handle.
        var path = new CAP_Core.Routing.RoutedPath();
        path.Segments.Add(new CAP_Core.Routing.StraightSegment(
            comp1.PhysicalPins[0].GetAbsolutePosition().Item1,
            comp1.PhysicalPins[0].GetAbsolutePosition().Item2,
            comp2.PhysicalPins[0].GetAbsolutePosition().Item1,
            comp2.PhysicalPins[0].GetAbsolutePosition().Item2,
            0));
        connection.RestoreCachedPath(path);
        connection.IsRouteFrozen = true;
        connection.BendRadiusOverrides[0] = 12.5;

        return (canvas, vm1, vm2, connection);
    }

    /// <summary>
    /// Returns the single ComponentGroup created on the canvas.
    /// </summary>
    private static ComponentGroup GetCreatedGroup(DesignCanvasViewModel canvas)
    {
        return (ComponentGroup)canvas.Components
            .Single(c => c.Component is ComponentGroup).Component;
    }

    /// <summary>
    /// Creates a component with one pin at the vertical center of one edge.
    /// </summary>
    private static Component CreateComponentWithPin(
        string identifier, double x, double y, double pinOffsetX, double pinAngleDegrees)
    {
        var sMatrix = new SMatrix(new List<Guid>(), new List<(Guid sliderID, double value)>());
        var pins = new List<PhysicalPin>
        {
            new()
            {
                Name = "Pin1",
                OffsetXMicrometers = pinOffsetX,
                OffsetYMicrometers = 15,
                AngleDegrees = pinAngleDegrees
            }
        };

        return new Component(
            new Dictionary<int, SMatrix> { { 1550, sMatrix } },
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            identifier,
            new DiscreteRotation(),
            pins)
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 50,
            HeightMicrometers = 30
        };
    }
}
