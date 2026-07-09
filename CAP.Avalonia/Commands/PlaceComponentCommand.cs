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
        bool isValid)
    {
        _canvas = canvas;
        _template = template;
        _x = x;
        _y = y;
        _isValid = isValid;

        if (isValid)
        {
            _component = ComponentTemplates.CreateFromTemplate(template, _x, _y);
        }
    }

    /// <summary>
    /// Tries to create a placement command. Returns null if no valid position exists.
    /// </summary>
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

    public string Description => $"Place {_template.Name}";

    /// <summary>
    /// The component instance this command places (created once in the constructor, so it
    /// survives undo/redo). Lets callers post-process the placement — e.g. seeding the
    /// raw-code override for raw-code authored custom components (#701).
    /// </summary>
    public Component? PlacedComponent => _component;

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
