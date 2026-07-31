using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for placing a new component on the canvas.
/// Returns null from TryCreate if no valid placement position exists.
/// </summary>
public class PlaceComponentCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly Component? _component;
    private readonly ComponentTemplate _template;
    private readonly double _x;
    private readonly double _y;
    private readonly bool _isValid;
    private ComponentViewModel? _createdViewModel;

    private PlaceComponentCommand(
        DesignCanvasViewModel canvas,
        ComponentTemplate template,
        double x,
        double y,
        bool isValid,
        int quarterTurnsCounterClockwise = 0)
    {
        _canvas = canvas;
        _template = template;
        _x = x;
        _y = y;
        _isValid = isValid;

        if (isValid)
        {
            _component = ComponentTemplates.CreateFromTemplate(template, _x, _y);
            // Rotate at the model level BEFORE the component is added to the canvas:
            // rotation keeps the top-left corner invariant, so (x, y) is the top-left
            // of the already-rotated bounding box. Doing it pre-placement also avoids
            // the canvas collision guard, which enforces a minimum inter-component
            // gap that GDS-abutting neighbours legitimately violate.
            for (var i = 0; i < quarterTurnsCounterClockwise; i++)
                RotateComponentCommand.ApplyModelRotation90(_component);
        }
    }

    /// <summary>
    /// Tries to create a placement command. Returns null if no valid position exists.
    /// </summary>
    /// <param name="canvas">Canvas the component is placed onto.</param>
    /// <param name="template">PDK template to instantiate.</param>
    /// <param name="x">Requested X position (µm); may be nudged by placement search.</param>
    /// <param name="y">Requested Y position (µm); may be nudged by placement search.</param>
    public static PlaceComponentCommand? TryCreate(
        DesignCanvasViewModel canvas,
        ComponentTemplate template,
        double x,
        double y)
    {
        var validPosition = canvas.FindValidPlacement(x, y, template.WidthMicrometers, template.HeightMicrometers);

        if (validPosition == null)
        {
            return null; // No space available
        }

        return new PlaceComponentCommand(canvas, template, validPosition.Value.x, validPosition.Value.y, true);
    }

    /// <summary>
    /// Creates a placement command that puts the component at exactly (x, y) —
    /// no collision search, no nudging, no chip-bounds clamping. For programmatic
    /// imports (e.g. GDS) where the source layout's coordinates are authoritative
    /// and any nudge would silently break abutment between placed instances.
    /// </summary>
    /// <param name="canvas">Canvas the component is placed onto.</param>
    /// <param name="template">PDK template to instantiate.</param>
    /// <param name="x">Exact X position (µm) of the rotated bounding box's top-left corner.</param>
    /// <param name="y">Exact Y position (µm) of the rotated bounding box's top-left corner.</param>
    /// <param name="quarterTurnsCounterClockwise">
    /// Number of 90° counter-clockwise rotations applied to the component before
    /// placement (0–3). The position is rotation-invariant (top-left stays fixed).
    /// </param>
    public static PlaceComponentCommand CreateExact(
        DesignCanvasViewModel canvas,
        ComponentTemplate template,
        double x,
        double y,
        int quarterTurnsCounterClockwise = 0)
        => new(canvas, template, x, y, true, quarterTurnsCounterClockwise);

    /// <summary>The component instance created by this command (null when invalid).</summary>
    public Component? PlacedComponent => _component;

    /// <summary>The canvas ViewModel created for the component on <see cref="Execute"/> (null until then).</summary>
    public ComponentViewModel? CreatedViewModel => _createdViewModel;

    public string Description => $"Place {_template.Name}";

    public void Execute()
    {
        if (_isValid && _component != null)
        {
            // Check if component already exists in canvas (e.g. after Undo then Redo)
            _createdViewModel = _canvas.Components.FirstOrDefault(c => c.Component == _component);

            if (_createdViewModel == null)
            {
                // Component not in canvas, add it
                _createdViewModel = _canvas.AddComponent(_component, _template.Name, _template.PdkSource);
            }
        }
    }

    public void Undo()
    {
        if (_component != null)
        {
            // Find the ComponentViewModel by the Component reference
            // (The stored _createdViewModel might have been removed/re-added by other commands like CreateGroupCommand)
            var viewModel = _canvas.Components.FirstOrDefault(c => c.Component == _component);
            if (viewModel != null)
            {
                _canvas.RemoveComponent(viewModel);
            }
            _createdViewModel = null;
        }
    }
}
