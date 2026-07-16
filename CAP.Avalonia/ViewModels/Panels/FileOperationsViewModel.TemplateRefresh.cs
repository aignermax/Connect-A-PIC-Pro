using System;
using System.Collections.Generic;
using System.Linq;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Type-wide propagation of a saved component definition (PR #742): placed instances snapshot
/// their S-matrix at placement time (<see cref="ComponentTemplates.CreateFromTemplate"/>), so a
/// "Compute with Meep" + save in the component editor would otherwise only affect FUTURE
/// placements. This partial pushes the freshly saved template's matrices into every matching
/// live instance while explicit overrides keep winning.
/// </summary>
public partial class FileOperationsViewModel
{
    /// <summary>
    /// Rebuilds the base wavelength-to-S-matrix map of every placed instance of
    /// <paramref name="template"/> from the template's (just saved) PDK definition — real
    /// persisted data only, nothing fabricated — then re-applies project-local and user-global
    /// overrides on top so an explicit override still wins over the refreshed PDK default.
    /// </summary>
    /// <param name="template">The freshly registered library template after an editor save.</param>
    public void RefreshInstancesFromTemplate(ComponentTemplate template)
    {
        var templateKey = $"{template.PdkSource}::{template.Name}";
        var matching = _canvas.Components
            .Select(vm => vm.Component)
            .Where(c => ResolveTemplateKey(c) == templateKey)
            .ToList();
        if (matching.Count == 0)
            return;

        foreach (var component in matching)
            RebuildBaseSMatrices(component, template);

        ReapplyOverridesTo(matching);
    }

    /// <summary>Replaces a component's base matrices with the ones the template now defines.</summary>
    private void RebuildBaseSMatrices(Component component, ComponentTemplate template)
    {
        var logicalPins = component.PhysicalPins
            .Where(pp => pp.LogicalPin != null)
            .Select(pp => pp.LogicalPin!)
            .ToList();
        if (logicalPins.Count == 0)
            return;

        try
        {
            var map = BuildWavelengthMap(template, logicalPins, component.GetAllSliders());
            if (map != null)
                component.WaveLengthToSMatrixMap = map;
        }
        catch (Exception ex)
        {
            // A pin/name mismatch means the live instance no longer matches the saved geometry —
            // keep its current matrices rather than guessing (no silent physics).
            _errorConsole?.LogWarning(
                $"Could not refresh the S-matrix of '{component.Identifier}' from the saved " +
                $"definition of '{template.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the wavelength map exactly like placement does
    /// (<see cref="ComponentTemplates.CreateFromTemplate"/>), but against the live instance's
    /// existing logical pins so the pin GUIDs the simulator routes by stay valid.
    /// </summary>
    private static Dictionary<int, SMatrix>? BuildWavelengthMap(
        ComponentTemplate template, List<Pin> pins, List<Slider> sliders)
    {
        if (template.CreateWavelengthSMatrixMap != null)
            return template.CreateWavelengthSMatrixMap(pins);

        SMatrix single;
        if (template.CreateSMatrixWithSliders != null)
            single = template.CreateSMatrixWithSliders(pins, sliders);
        else if (template.CreateSMatrix != null)
            single = template.CreateSMatrix(pins);
        else
            return null;

        return new Dictionary<int, SMatrix> { { 1550, single }, { 1310, single }, { 980, single } };
    }

    /// <summary>
    /// Re-applies stored overrides to <paramref name="components"/> in the same order the
    /// placement handler uses: project-local instance overrides first, then user-global
    /// template overrides — so explicit overrides always beat the refreshed PDK default.
    /// </summary>
    private void ReapplyOverridesTo(IReadOnlyList<Component> components)
    {
        if (StoredSMatrices.Count > 0)
        {
            Services.SMatrixOverrideApplicator.ApplyAll(
                components,
                StoredSMatrices,
                templateKeyResolver: ResolveTemplateKey,
                geometryKeyResolver: ResolveGeometryKey,
                errorConsole: _errorConsole,
                keyMatchesKnownTemplate: KeyMatchesKnownLibraryTemplate);
        }

        ApplyUserGlobalOverrides(components);
    }
}
