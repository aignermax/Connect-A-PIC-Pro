using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_DataAccess.Persistence.PIR;

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
    private readonly IDictionary<string, NazcaCodeOverride>? _overrideStore;
    private ComponentViewModel? _createdViewModel;
    private bool _seededRawCodeOverride;

    private PlaceComponentCommand(
        DesignCanvasViewModel canvas,
        ComponentTemplate template,
        double x,
        double y,
        bool isValid,
        IDictionary<string, NazcaCodeOverride>? overrideStore)
    {
        _canvas = canvas;
        _template = template;
        _x = x;
        _y = y;
        _isValid = isValid;
        _overrideStore = overrideStore;

        if (isValid)
        {
            _component = ComponentTemplates.CreateFromTemplate(template, _x, _y);
        }
    }

    /// <summary>
    /// Tries to create a placement command. Returns null if no valid position exists.
    /// </summary>
    /// <param name="canvas">Canvas the component is placed onto.</param>
    /// <param name="template">PDK template to instantiate.</param>
    /// <param name="x">Requested X position (µm); may be nudged by placement search.</param>
    /// <param name="y">Requested Y position (µm); may be nudged by placement search.</param>
    /// <param name="overrideStore">
    /// Optional per-instance raw-code override store (e.g.
    /// <c>FileOperationsViewModel.StoredNazcaOverrides</c>). When <paramref name="template"/>
    /// carries <see cref="ComponentTemplate.RawCode"/>, <see cref="Execute"/> seeds a matching
    /// <see cref="NazcaCodeOverride"/> entry for the placed instance so raw-code preview and
    /// export — which read the override map — work without any export-path changes.
    /// </param>
    public static PlaceComponentCommand? TryCreate(
        DesignCanvasViewModel canvas,
        ComponentTemplate template,
        double x,
        double y,
        IDictionary<string, NazcaCodeOverride>? overrideStore = null)
    {
        var validPosition = canvas.FindValidPlacement(x, y, template.WidthMicrometers, template.HeightMicrometers);

        if (validPosition == null)
        {
            return null; // No space available
        }

        return new PlaceComponentCommand(canvas, template, validPosition.Value.x, validPosition.Value.y, true, overrideStore);
    }

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

            SeedRawCodeOverride();
        }
    }

    /// <summary>
    /// Seeds a per-instance <see cref="NazcaCodeOverride"/> for the placed component when
    /// <see cref="_template"/> is a raw-code component. No-ops when there is no override
    /// store, the template has no raw code, or the store already has an entry for this
    /// instance's identifier (e.g. restored from a .lun file on load) — an existing entry
    /// is never overwritten. Runs on every <see cref="Execute"/>, not just the first, so a
    /// redo after <see cref="Undo"/> (which removes the entry this command seeded) restores it.
    /// </summary>
    private void SeedRawCodeOverride()
    {
        if (_overrideStore == null || _component == null) return;
        if (string.IsNullOrEmpty(_template.RawCode)) return;
        if (_overrideStore.ContainsKey(_component.Identifier)) return;

        _overrideStore[_component.Identifier] = new NazcaCodeOverride
        {
            RawCode = _template.RawCode,
            Backend = _template.RawCodeBackend == "gdsfactory" ? OverrideBackend.GdsFactory : OverrideBackend.Nazca
        };
        _seededRawCodeOverride = true;
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

            // Undo symmetry: only remove the override entry if THIS command seeded it —
            // never touch a pre-existing entry (e.g. loaded from a .lun file).
            if (_seededRawCodeOverride && _overrideStore != null)
            {
                _overrideStore.Remove(_component.Identifier);
                _seededRawCodeOverride = false;
            }
        }
    }
}
