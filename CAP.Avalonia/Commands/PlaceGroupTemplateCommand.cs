using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for placing a group template instance on the canvas.
/// Creates a deep copy of the template group with new unique identifiers.
/// Returns null from TryCreate if no valid placement position exists or the
/// template's frozen physics data is rejected (see <see cref="TryCreate(DesignCanvasViewModel, GroupLibraryManager, GroupTemplate, double, double, out NonConvergentCircuitException?)"/>).
/// </summary>
public class PlaceGroupTemplateCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly GroupTemplate _template;
    private readonly ComponentGroup _groupToPlace;
    private List<ComponentViewModel>? _placedComponentViewModels;

    private PlaceGroupTemplateCommand(
        DesignCanvasViewModel canvas,
        GroupTemplate template,
        ComponentGroup groupToPlace)
    {
        _canvas = canvas;
        _template = template;
        // The group instance is created once in TryCreate so Execute/Undo/Execute reuses it.
        _groupToPlace = groupToPlace;
    }

    /// <summary>
    /// Tries to create a placement command. Returns null if no valid position exists
    /// or the template was rejected by the physics guard.
    /// </summary>
    public static PlaceGroupTemplateCommand? TryCreate(
        DesignCanvasViewModel canvas,
        GroupLibraryManager libraryManager,
        GroupTemplate template,
        double x,
        double y)
        => TryCreate(canvas, libraryManager, template, x, y, out _);

    /// <summary>
    /// Tries to create a placement command. Returns null if no valid position exists
    /// (<paramref name="physicsRejection"/> stays null) or if instantiating the template
    /// trips the S-matrix passivity guard — a saved template can carry frozen non-passive
    /// child data (stale FDTD/override results), and that must abort the placement cleanly
    /// instead of crashing the app (round-4 field crash). On rejection nothing has been
    /// added to the canvas or the undo stack; the discarded deep copy is the only work done.
    /// </summary>
    /// <param name="canvas">Canvas to place on.</param>
    /// <param name="libraryManager">Library manager that instantiates the template.</param>
    /// <param name="template">The group template to place.</param>
    /// <param name="x">Requested X centre position (µm).</param>
    /// <param name="y">Requested Y centre position (µm).</param>
    /// <param name="physicsRejection">
    /// The physics-guard exception when the template's S-matrix data was rejected
    /// (non-passive component, connection gain, resonant loop); null otherwise.
    /// </param>
    public static PlaceGroupTemplateCommand? TryCreate(
        DesignCanvasViewModel canvas,
        GroupLibraryManager libraryManager,
        GroupTemplate template,
        double x,
        double y,
        out NonConvergentCircuitException? physicsRejection)
    {
        physicsRejection = null;

        if (template.TemplateGroup == null)
        {
            return null; // Template not loaded
        }

        // Center the group at the click position
        double centeredX = x - template.WidthMicrometers / 2;
        double centeredY = y - template.HeightMicrometers / 2;

        var validPosition = canvas.FindValidPlacement(
            centeredX,
            centeredY,
            template.WidthMicrometers,
            template.HeightMicrometers);

        if (validPosition == null)
        {
            return null; // No space available
        }

        ComponentGroup groupToPlace;
        try
        {
            groupToPlace = libraryManager.InstantiateTemplate(
                template, validPosition.Value.x, validPosition.Value.y);
        }
        catch (NonConvergentCircuitException rejection)
        {
            // Physics guard tripped while computing the group's S-matrix: the template
            // data would fabricate energy. Never let this escape to the dispatcher —
            // surface it to the caller so the action aborts with a user-facing message.
            physicsRejection = rejection;
            return null;
        }

        groupToPlace.IsPrefab = false; // Instance, not prefab

        return new PlaceGroupTemplateCommand(canvas, template, groupToPlace);
    }

    public string Description => $"Place group template '{_template.Name}'";

    /// <summary>
    /// The group instance this command places on <see cref="Execute"/> (created once in
    /// <c>TryCreate</c>). Lets the caller pin placement-time metadata — e.g. the chiplet
    /// process binding (issue #935) — onto the instance before it lands on the canvas.
    /// </summary>
    public ComponentGroup GroupToPlace => _groupToPlace;

    public void Execute()
    {
        // Add the group as a single component to the canvas
        // The DesignCanvasViewModel will handle group components appropriately
        var groupVm = _canvas.AddComponent(_groupToPlace);
        _placedComponentViewModels = new List<ComponentViewModel> { groupVm };

        // Recalculate routes for the new components
        _ = _canvas.RecalculateRoutesAsync();
    }

    public void Undo()
    {
        if (_placedComponentViewModels == null || _placedComponentViewModels.Count == 0)
            return;

        // Remove the group from canvas (this will handle child cleanup)
        _canvas.RemoveComponent(_placedComponentViewModels[0]);

        _placedComponentViewModels = null;

        // Recalculate routes after removal
        _ = _canvas.RecalculateRoutesAsync();
    }
}
