using CAP.Avalonia.Selection;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Selection;

/// <summary>
/// Tests <see cref="ConnectionBoxSelector"/> (issue #862): rubber-band selection collects the
/// optical connections whose path crosses the box, excludes electrical metal traces, and
/// supports Alt-removal.
/// </summary>
public class ConnectionBoxSelectorTests
{
    [Fact]
    public void SelectInRectangle_AddsConnectionCrossingTheBox()
    {
        var selection = new SelectionManager();
        var conn = CreateConnectionVm(MatterType.Light, startX: 0, startY: 50, endX: 200, endY: 50);

        ConnectionBoxSelector.SelectInRectangle(selection, new[] { conn }, 80, 0, 120, 100);

        selection.SelectedConnections.ShouldContain(conn);
        conn.IsSelected.ShouldBeTrue();
    }

    [Fact]
    public void SelectInRectangle_SegmentCrossingWithoutEndpointInside_IsSelected()
    {
        var selection = new SelectionManager();
        // Straight line passes entirely through the box; neither endpoint is inside.
        var conn = CreateConnectionVm(MatterType.Light, startX: -100, startY: 10, endX: 300, endY: 10);

        ConnectionBoxSelector.SelectInRectangle(selection, new[] { conn }, 0, 0, 50, 50);

        selection.SelectedConnections.ShouldContain(conn);
    }

    [Fact]
    public void SelectInRectangle_ConnectionOutsideBox_IsNotSelected()
    {
        var selection = new SelectionManager();
        var conn = CreateConnectionVm(MatterType.Light, startX: 0, startY: 0, endX: 100, endY: 0);

        ConnectionBoxSelector.SelectInRectangle(selection, new[] { conn }, 0, 50, 100, 100);

        selection.SelectedConnections.ShouldBeEmpty();
        conn.IsSelected.ShouldBeFalse();
    }

    [Fact]
    public void SelectInRectangle_ExcludesElectricalConnections()
    {
        var selection = new SelectionManager();
        var electrical = CreateConnectionVm(MatterType.Electricity, startX: 0, startY: 50, endX: 200, endY: 50);

        ConnectionBoxSelector.SelectInRectangle(selection, new[] { electrical }, 0, 0, 200, 100);

        selection.SelectedConnections.ShouldBeEmpty();
    }

    [Fact]
    public void SelectInRectangle_RemoveFlag_RemovesHitConnections()
    {
        var selection = new SelectionManager();
        var conn = CreateConnectionVm(MatterType.Light, startX: 0, startY: 50, endX: 200, endY: 50);
        selection.SelectedConnections.Add(conn);
        conn.IsSelected = true;

        ConnectionBoxSelector.SelectInRectangle(selection, new[] { conn }, 0, 0, 200, 100, removeFromSelection: true);

        selection.SelectedConnections.ShouldBeEmpty();
        conn.IsSelected.ShouldBeFalse();
    }

    [Fact]
    public void ClearSelection_AlsoClearsSelectedConnections()
    {
        var selection = new SelectionManager();
        var conn = CreateConnectionVm(MatterType.Light, startX: 0, startY: 0, endX: 100, endY: 0);
        selection.SelectedConnections.Add(conn);
        conn.IsSelected = true;

        selection.ClearSelection();

        selection.SelectedConnections.ShouldBeEmpty();
        conn.IsSelected.ShouldBeFalse();
    }

    /// <summary>
    /// Creates an unrouted connection whose fallback hit-test geometry is the straight line
    /// between the given absolute pin positions.
    /// </summary>
    private static WaveguideConnectionViewModel CreateConnectionVm(
        MatterType matterType, double startX, double startY, double endX, double endY)
    {
        var connection = new WaveguideConnection
        {
            StartPin = CreatePin(matterType, startX, startY),
            EndPin = CreatePin(matterType, endX, endY),
        };
        return new WaveguideConnectionViewModel(connection);
    }

    private static PhysicalPin CreatePin(MatterType matterType, double x, double y)
    {
        var component = CreateComponent();
        component.PhysicalX = x;
        component.PhysicalY = y;
        return new PhysicalPin
        {
            Name = "p",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            LogicalPin = new Pin("p", 0, matterType, RectSide.Right),
        };
    }

    private static Component CreateComponent()
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: "test",
            rotationCounterClock: DiscreteRotation.R0);
    }
}
