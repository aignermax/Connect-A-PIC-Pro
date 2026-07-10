using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for placing a group template instance on the canvas.
/// Creates a deep copy of the template group with new unique identifiers and seeds the
/// template's per-instance Nazca overrides into the design's override store (issue #720)
/// so raw-code/override members keep their geometry in the new design.
/// Returns null from TryCreate if no valid placement position exists.
/// </summary>
public class PlaceGroupTemplateCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly GroupLibraryManager _libraryManager;
    private readonly GroupTemplate _template;
    private readonly double _x;
    private readonly double _y;
    private readonly bool _isValid;
    private readonly ComponentGroup? _groupToPlace;
    private readonly IDictionary<string, NazcaCodeOverride>? _overrideStore;
    private readonly Dictionary<string, NazcaCodeOverride> _overrideSeeds = new();
    private readonly List<string> _seededOverrideIds = new();
    private List<ComponentViewModel>? _placedComponentViewModels;

    private PlaceGroupTemplateCommand(
        DesignCanvasViewModel canvas,
        GroupLibraryManager libraryManager,
        GroupTemplate template,
        double x,
        double y,
        bool isValid,
        IDictionary<string, NazcaCodeOverride>? overrideStore)
    {
        _canvas = canvas;
        _libraryManager = libraryManager;
        _template = template;
        _x = x;
        _y = y;
        _isValid = isValid;
        _overrideStore = overrideStore;

        // Create the group instance in the constructor so Execute/Undo/Execute reuses the same instance
        if (isValid && template.TemplateGroup != null)
        {
            _groupToPlace = _libraryManager.InstantiateTemplate(template, x, y);
            _groupToPlace.IsPrefab = false; // Instance, not prefab
            _overrideSeeds = GroupTemplateNazcaOverrides.BuildSeedMap(template, _groupToPlace);
        }
    }

    /// <summary>
    /// Tries to create a placement command. Returns null if no valid position exists.
    /// </summary>
    /// <param name="canvas">The target design canvas.</param>
    /// <param name="libraryManager">The group library the template belongs to.</param>
    /// <param name="template">The template to instantiate.</param>
    /// <param name="x">Requested X position (canvas µm).</param>
    /// <param name="y">Requested Y position (canvas µm).</param>
    /// <param name="overrideStore">
    /// The target design's live Nazca override store (e.g. <c>StoredNazcaOverrides</c>);
    /// template member overrides are seeded into it on Execute (issue #720). Null skips seeding.
    /// </param>
    public static PlaceGroupTemplateCommand? TryCreate(
        DesignCanvasViewModel canvas,
        GroupLibraryManager libraryManager,
        GroupTemplate template,
        double x,
        double y,
        IDictionary<string, NazcaCodeOverride>? overrideStore = null)
    {
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

        return new PlaceGroupTemplateCommand(
            canvas,
            libraryManager,
            template,
            validPosition.Value.x,
            validPosition.Value.y,
            true,
            overrideStore);
    }

    public string Description => $"Place group template '{_template.Name}'";

    public void Execute()
    {
        if (!_isValid || _groupToPlace == null)
            return;

        // Add the group as a single component to the canvas
        // The DesignCanvasViewModel will handle group components appropriately
        var groupVm = _canvas.AddComponent(_groupToPlace);
        _placedComponentViewModels = new List<ComponentViewModel> { groupVm };

        SeedOverrides();

        // Recalculate routes for the new components
        _ = _canvas.RecalculateRoutesAsync();
    }

    /// <summary>
    /// Writes the template's member overrides into the design's override store under the
    /// placed instance's identifiers, so export/preview see the raw-code geometry (#720).
    /// Existing entries are never overwritten; only entries seeded here are removed on Undo.
    /// </summary>
    private void SeedOverrides()
    {
        if (_overrideStore == null)
            return;

        _seededOverrideIds.Clear();
        foreach (var (identifier, nazcaOverride) in _overrideSeeds)
        {
            if (_overrideStore.ContainsKey(identifier))
                continue;

            _overrideStore[identifier] = nazcaOverride.Clone();
            _seededOverrideIds.Add(identifier);
        }
    }

    public void Undo()
    {
        if (_groupToPlace == null || _placedComponentViewModels == null || _placedComponentViewModels.Count == 0)
            return;

        // Remove the group from canvas (this will handle child cleanup)
        _canvas.RemoveComponent(_placedComponentViewModels[0]);

        // Take back only the overrides this command seeded
        if (_overrideStore != null)
        {
            foreach (var identifier in _seededOverrideIds)
                _overrideStore.Remove(identifier);
            _seededOverrideIds.Clear();
        }

        _placedComponentViewModels = null;

        // Recalculate routes after removal
        _ = _canvas.RecalculateRoutesAsync();
    }
}
