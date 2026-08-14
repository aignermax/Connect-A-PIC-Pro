using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Tests for <see cref="RotateComponentCommand"/> collision detection and path recalculation.
/// </summary>
public class RotateComponentCommandTests
{
    /// <summary>
    /// Verifies that rotation is blocked when the rotated footprint would overlap another component.
    /// </summary>
    [Fact]
    public void Execute_WhenRotatedFootprintCollidesWithNeighbour_RotationIsBlocked()
    {
        var canvas = new DesignCanvasViewModel();

        // Component A: 100µm × 50µm at (100, 100) — occupies x:[100,200], y:[100,150]
        var compA = CreateComponent(widthMicrometers: 100, heightMicrometers: 50, x: 100, y: 100);
        var vmA = canvas.AddComponent(compA);

        // Component B placed so rotated A (50µm × 100µm at (100,100)) would overlap it.
        // Rotated A would reach y=200. B at y=160 overlaps that range.
        var compB = CreateComponent(widthMicrometers: 100, heightMicrometers: 100, x: 130, y: 160);
        canvas.AddComponent(compB);

        var cmd = new RotateComponentCommand(canvas, vmA);

        cmd.Execute();

        cmd.WasApplied.ShouldBeFalse("Rotation must be rejected when the rotated footprint overlaps another component");
        compA.WidthMicrometers.ShouldBe(100, "Width must remain unchanged when rotation is blocked");
        compA.HeightMicrometers.ShouldBe(50, "Height must remain unchanged when rotation is blocked");
    }

    /// <summary>
    /// Verifies that rotation succeeds when there is no collision in the rotated footprint.
    /// </summary>
    [Fact]
    public void Execute_WhenRotatedFootprintIsFree_RotationSucceeds()
    {
        var canvas = new DesignCanvasViewModel();

        // Component A: 100µm × 50µm — rotates to 50µm × 100µm
        var compA = CreateComponent(widthMicrometers: 100, heightMicrometers: 50, x: 100, y: 100);
        var vmA = canvas.AddComponent(compA);

        // Component B is far away — no overlap with rotated A
        var compB = CreateComponent(widthMicrometers: 100, heightMicrometers: 100, x: 1000, y: 1000);
        canvas.AddComponent(compB);

        var cmd = new RotateComponentCommand(canvas, vmA);
        double originalWidth = compA.WidthMicrometers;
        double originalHeight = compA.HeightMicrometers;

        cmd.Execute();

        cmd.WasApplied.ShouldBeTrue("Rotation must succeed when space is free");
        compA.WidthMicrometers.ShouldBe(originalHeight, "Width must equal the original height after 90° rotation");
        compA.HeightMicrometers.ShouldBe(originalWidth, "Height must equal the original width after 90° rotation");
    }

    /// <summary>
    /// Verifies that after a successful rotation, pin positions are updated to reflect
    /// the new orientation, which triggers path recalculation for connected waveguides.
    /// </summary>
    [Fact]
    public void Execute_AfterSuccessfulRotation_PinPositionsAndRotationStateAreUpdated()
    {
        var canvas = new DesignCanvasViewModel();

        // Component with a pin at the left edge midpoint (0, 25) of a 100µm × 50µm footprint
        var physicalPins = new List<PhysicalPin>
        {
            new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 25 },
        };
        var compA = CreateComponent(widthMicrometers: 100, heightMicrometers: 50, x: 200, y: 200,
            physicalPins: physicalPins);
        var vmA = canvas.AddComponent(compA);

        double pinOffsetXBefore = compA.PhysicalPins[0].OffsetXMicrometers;
        double pinOffsetYBefore = compA.PhysicalPins[0].OffsetYMicrometers;

        var cmd = new RotateComponentCommand(canvas, vmA);
        cmd.Execute();

        cmd.WasApplied.ShouldBeTrue("Rotation must succeed in free space");

        // RotationDegrees must reflect the 90° CCW rotation
        compA.RotationDegrees.ShouldBe(90, "RotationDegrees must be updated to 90° after one CCW rotation");

        // Pin offsets must have changed — new positions drive path recalculation
        double pinOffsetXAfter = compA.PhysicalPins[0].OffsetXMicrometers;
        double pinOffsetYAfter = compA.PhysicalPins[0].OffsetYMicrometers;

        bool pinMoved = Math.Abs(pinOffsetXAfter - pinOffsetXBefore) > 0.001
                     || Math.Abs(pinOffsetYAfter - pinOffsetYBefore) > 0.001;
        pinMoved.ShouldBeTrue("Physical pin offsets must change after rotation to reflect new port positions");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    [Fact]
    public void RotateByDegrees_RecordsUnrotatedDimensionsOnce()
    {
        var comp = CreateComponent(widthMicrometers: 20, heightMicrometers: 10);

        ComponentPoseTransform.RotateByDegrees(comp, 30);

        comp.UnrotatedWidthMicrometers.ShouldBe(20, 1e-9);
        comp.UnrotatedHeightMicrometers.ShouldBe(10, 1e-9);

        // Further rotations (and undo/redo cycles) must never overwrite the frame.
        ComponentPoseTransform.RotateByDegrees(comp, 15);
        ComponentPoseTransform.Rotate90CounterClockwise(comp);

        comp.UnrotatedWidthMicrometers.ShouldBe(20, 1e-9);
        comp.UnrotatedHeightMicrometers.ShouldBe(10, 1e-9);
    }

    private static Component CreateComponent(
        double widthMicrometers,
        double heightMicrometers,
        double x = 0,
        double y = 0,
        List<PhysicalPin>? physicalPins = null)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>
        {
            new("west0", 0, MatterType.Light, RectSide.Left),
            new("east0", 1, MatterType.Light, RectSide.Right),
        });

        var component = new Component(
            new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            parts,
            0,
            "TestComp",
            DiscreteRotation.R0,
            physicalPins);

        component.WidthMicrometers = widthMicrometers;
        component.HeightMicrometers = heightMicrometers;
        component.PhysicalX = x;
        component.PhysicalY = y;

        return component;
    }
}
