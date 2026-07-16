using System;
using System.Collections.Generic;
using System.Linq;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

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
    /// persisted data only, nothing fabricated — then re-applies user-global and project-local
    /// overrides on top so an explicit override still wins over the refreshed PDK default.
    /// Instances inside <see cref="ComponentGroup"/>s are refreshed too (the group structure
    /// itself is never touched), and the running simulation is invalidated so the power-flow
    /// overlay never keeps rendering light computed from the old matrices.
    /// </summary>
    /// <param name="template">The freshly registered library template after an editor save.</param>
    public void RefreshInstancesFromTemplate(ComponentTemplate template)
    {
        // The save path treats component/PDK names case-insensitively; the instance match must
        // too, or a case-only rename silently skips every placed instance.
        var templateKey = $"{template.PdkSource}::{template.Name}";
        var matching = FlattenGroupChildren(_canvas.Components.Select(vm => vm.Component))
            .Where(c => string.Equals(ResolveTemplateKey(c), templateKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
            return;

        foreach (var component in matching)
            RebuildBaseSMatrices(component, template);

        ReapplyOverridesTo(matching);
        _canvas.InvalidateSimulation();
    }

    /// <summary>
    /// Yields all placed leaf components, descending into <see cref="ComponentGroup"/>s
    /// (read-only traversal — groups are never restructured).
    /// </summary>
    private static IEnumerable<Component> FlattenGroupChildren(IEnumerable<Component> components)
    {
        foreach (var component in components)
        {
            if (component is ComponentGroup group)
            {
                foreach (var child in FlattenGroupChildren(group.ChildComponents))
                    yield return child;
            }
            else
            {
                yield return component;
            }
        }
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

        // A pin rename/count change means the saved definition no longer describes this live
        // instance: rebuilding would silently produce half-populated or zero matrices
        // (PdkTemplateConverter skips unknown pin names). Keep the instance's current physics.
        if (!TemplatePinsMatchInstance(template, logicalPins))
        {
            _errorConsole?.LogWarning(
                $"Template pins changed — placed instance '{component.Identifier}' keeps its previous " +
                $"S-matrix; replace it to adopt the new definition of '{template.Name}'.");
            return;
        }

        try
        {
            var map = BuildWavelengthMap(template, logicalPins, component.GetAllSliders());
            if (map != null)
                component.WaveLengthToSMatrixMap = map;
        }
        catch (Exception ex)
        {
            // Any other rebuild failure: keep current matrices rather than guessing (no silent physics).
            _errorConsole?.LogWarning(
                $"Could not refresh the S-matrix of '{component.Identifier}' from the saved " +
                $"definition of '{template.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// True when the saved template's pins (definitions AND every pin name its stored S-matrix
    /// data references) resolve one-to-one against the live instance's pins.
    /// </summary>
    private static bool TemplatePinsMatchInstance(ComponentTemplate template, List<Pin> instancePins)
    {
        var instanceNames = new HashSet<string>(
            instancePins.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var templateNames = new HashSet<string>(
            template.PinDefinitions.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        return templateNames.SetEquals(instanceNames)
            && DraftConnectionsResolveAgainst(template.SourceDraft?.SMatrix, instanceNames);
    }

    /// <summary>True when every pin name in the persisted S-matrix data exists on the instance.</summary>
    private static bool DraftConnectionsResolveAgainst(PdkSMatrixDraft? draft, HashSet<string> pinNames)
    {
        if (draft is null)
            return true;
        var connections = (draft.Connections ?? new List<SMatrixConnection>())
            .Concat(draft.WavelengthData?.SelectMany(e => e.Connections)
                    ?? Enumerable.Empty<SMatrixConnection>());
        return connections.All(c => pinNames.Contains(c.FromPin) && pinNames.Contains(c.ToPin));
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
    /// Re-applies stored overrides to <paramref name="components"/> with the documented
    /// precedence per-instance &gt; user-global &gt; template: user-global template overrides
    /// first, project-local per-instance overrides LAST so they win the last-write-per-wavelength
    /// application.
    /// </summary>
    private void ReapplyOverridesTo(IReadOnlyList<Component> components)
    {
        ApplyUserGlobalOverrides(components);

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
    }
}
