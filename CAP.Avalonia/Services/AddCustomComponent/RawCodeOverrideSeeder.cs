using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Seeds the per-instance <see cref="NazcaCodeOverride"/> for a newly placed raw-code
/// authored custom component (#701): the template's stored cell code becomes the
/// instance's raw-code override, so the existing #559/#637 override pipeline renders,
/// previews, and exports the real geometry with no new export plumbing.
/// </summary>
public static class RawCodeOverrideSeeder
{
    /// <summary>
    /// Seeds <paramref name="overrides"/> with a raw-code override for
    /// <paramref name="component"/> when <paramref name="template"/> carries raw code.
    /// No-op for normal PDK templates and when the instance already has an override
    /// (never clobbers user edits, e.g. on redo after an undo). The export anchor is the
    /// template's origin offset re-expressed in cell-internal bbox coordinates
    /// (<c>bboxXMin = −offsetX</c>, <c>bboxYMax = offsetY</c>), and the template pins are
    /// recorded as both override and template pins — they match by construction, so the
    /// component keeps its saved simulation model (<c>HasNoSimulationModel</c> = false).
    /// </summary>
    /// <returns>True when an override was seeded.</returns>
    public static bool Seed(
        Component component, ComponentTemplate template, IDictionary<string, NazcaCodeOverride> overrides)
    {
        if (string.IsNullOrWhiteSpace(template.RawCode))
            return false;
        if (overrides.ContainsKey(component.Identifier))
            return false;

        var overrideEntry = new NazcaCodeOverride
        {
            RawCode = template.RawCode,
            Backend = template.RawCodeBackend == "gdsfactory"
                ? OverrideBackend.GdsFactory
                : OverrideBackend.Nazca,
            OverridePins = MapPins(template),
            TemplatePins = MapPins(template),
            HasNoSimulationModel = false,
        };
        overrideEntry.SetOverrideGeometry(
            template.WidthMicrometers, template.HeightMicrometers,
            -template.NazcaOriginOffsetX, template.NazcaOriginOffsetY);

        overrides[component.Identifier] = overrideEntry;
        return true;
    }

    /// <summary>Maps the template's pin definitions to the override's persisted pin shape.</summary>
    private static List<OverridePinData> MapPins(ComponentTemplate template) =>
        template.PinDefinitions.Select(p => new OverridePinData
        {
            Name = p.Name,
            OffsetXMicrometers = p.OffsetX,
            OffsetYMicrometers = p.OffsetY,
            AngleDegrees = p.AngleDegrees,
        }).ToList();
}
