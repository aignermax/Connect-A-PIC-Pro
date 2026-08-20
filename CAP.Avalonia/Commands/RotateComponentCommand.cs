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
        ComponentPoseTransform.Rotate90CounterClockwise(comp);

        // Notify the view model of dimension changes
        _component.NotifyDimensionsChanged();

        // Update obstacle in pathfinding grid
        _canvas.Router.UpdateComponentObstacle(comp);

        // Recalculate paths asynchronously (pin angles change with rotation)
        _ = _canvas.RecalculateRoutesAsync();
    }
}
