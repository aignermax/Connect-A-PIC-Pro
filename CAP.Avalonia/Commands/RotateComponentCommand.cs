using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for rotating a component 90° counter-clockwise.
/// Rotation is rejected when the rotated footprint would overlap another component.
/// </summary>
public class RotateComponentCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly ComponentViewModel _component;
    private bool _applied;

    public RotateComponentCommand(DesignCanvasViewModel canvas, ComponentViewModel component)
    {
        _canvas = canvas;
        _component = component;
    }

    public string Description => $"Rotate {_component.Name}";

    /// <summary>
    /// Whether the last Execute() call actually applied the rotation.
    /// False if the rotation was blocked due to a collision.
    /// </summary>
    public bool WasApplied => _applied;

    public void Execute()
    {
        var comp = _component.Component;

        // Don't rotate locked components
        if (comp.IsLocked)
        {
            _applied = false;
            return;
        }

        // After 90° CCW rotation, width and height dimensions swap.
        double rotatedWidth = comp.HeightMicrometers;
        double rotatedHeight = comp.WidthMicrometers;

        if (!_canvas.CanPlaceComponent(_component.X, _component.Y, rotatedWidth, rotatedHeight, _component))
        {
            _applied = false;
            return;
        }

        _applied = true;
        RotateComponent90();
    }

    public void Undo()
    {
        if (!_applied) return;

        // Rotate 3 times to undo (270° = -90°)
        RotateComponent90();
        RotateComponent90();
        RotateComponent90();
    }

    private void RotateComponent90()
    {
        var comp = _component.Component;
        ApplyModelRotation90(comp);

        // Notify the view model of dimension changes
        _component.NotifyDimensionsChanged();

        // Update obstacle in pathfinding grid
        _canvas.Router.UpdateComponentObstacle(comp);

        // Recalculate paths asynchronously (pin angles change with rotation)
        _ = _canvas.RecalculateRoutesAsync();
    }

    /// <summary>
    /// Model-level 90° counter-clockwise rotation around the component center:
    /// physical pin offsets, width/height swap, discrete rotation and
    /// <c>RotationDegrees</c>. The top-left corner (<c>PhysicalX</c>/<c>PhysicalY</c>)
    /// is deliberately left untouched. No ViewModel/canvas notifications — shared
    /// with programmatic placement (GDS import), which rotates the component
    /// before it is added to the canvas.
    /// </summary>
    internal static void ApplyModelRotation90(Component comp)
    {
        var width = comp.WidthMicrometers;
        var height = comp.HeightMicrometers;

        // Rotate each physical pin's offset around the component center
        // Pin angles stay relative to the component - GetAbsoluteAngle() adds RotationDegrees
        foreach (var pin in comp.PhysicalPins)
        {
            // Rotate offset 90° counter-clockwise around center
            // Center is at (width/2, height/2)
            var cx = width / 2;
            var cy = height / 2;

            // Translate to origin
            var x = pin.OffsetXMicrometers - cx;
            var y = pin.OffsetYMicrometers - cy;

            // Rotate 90° counter-clockwise: (x, y) -> (-y, x)
            var newX = -y;
            var newY = x;

            // Translate back (but to new center after dimension swap)
            pin.OffsetXMicrometers = newX + cy; // cy becomes new cx
            pin.OffsetYMicrometers = newY + cx; // cx becomes new cy

            // NOTE: Pin angles are stored relative to the component.
            // GetAbsoluteAngle() adds component.RotationDegrees to get world-space angle.
            // Do NOT modify pin.AngleDegrees here.
        }

        // Swap dimensions
        comp.WidthMicrometers = height;
        comp.HeightMicrometers = width;

        // Update the component's discrete rotation and RotationDegrees
        comp.RotateBy90CounterClockwise();
    }
}
