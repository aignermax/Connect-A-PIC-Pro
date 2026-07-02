using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// Infrastructure for component templates. Component definitions are loaded exclusively
/// from JSON PDK files (demo-pdk.json, siepic-ebeam-pdk.json, user PDKs).
/// </summary>
public static class ComponentTemplates
{
    private static int _componentCounter = 0;

    /// <summary>
    /// Returns an empty list. All component templates are now loaded from JSON PDK files.
    /// Use <see cref="CAP.Avalonia.Services.PdkTemplateConverter"/> to convert JSON PDK
    /// components into <see cref="ComponentTemplate"/> instances.
    /// </summary>
    [Obsolete("Use JSON PDK files (demo-pdk.json, siepic-ebeam-pdk.json) as the source of component templates. This method returns an empty list.")]
    public static List<ComponentTemplate> GetAllTemplates() => new List<ComponentTemplate>();

    public static Component CreateFromTemplate(ComponentTemplate template, double x, double y)
    {
        _componentCounter++;
        var instanceName = $"{template.Name}_{_componentCounter}";

        // Create logical pins
        var logicalPins = new List<Pin>();
        for (int i = 0; i < template.PinDefinitions.Length; i++)
        {
            var def = template.PinDefinitions[i];
            var side = def.AngleDegrees switch
            {
                0 => RectSide.Right,
                90 => RectSide.Up,
                180 => RectSide.Left,
                270 => RectSide.Down,
                _ => RectSide.Right
            };
            logicalPins.Add(new Pin(def.Name, i, def.Kind, side));
        }

        // Create parts array (simplified: single part)
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(logicalPins);

        // Create sliders before S-Matrix (slider-aware S-Matrices need slider IDs)
        var sliders = new List<Slider>();
        if (template.HasSlider)
        {
            sliders.Add(new Slider(Guid.NewGuid(), 0, (template.SliderMin + template.SliderMax) / 2, template.SliderMax, template.SliderMin));
        }

        // Create wavelength→S-Matrix map
        Dictionary<int, SMatrix> wavelengthMap;
        if (template.CreateWavelengthSMatrixMap != null)
        {
            // Multi-wavelength: each wavelength has its own S-matrix
            wavelengthMap = template.CreateWavelengthSMatrixMap(logicalPins);
        }
        else
        {
            // Single S-matrix duplicated across standard wavelengths
            SMatrix sMatrix;
            if (template.CreateSMatrixWithSliders != null)
                sMatrix = template.CreateSMatrixWithSliders(logicalPins, sliders);
            else if (template.CreateSMatrix != null)
                sMatrix = template.CreateSMatrix(logicalPins);
            else
                throw new InvalidOperationException($"Template '{template.Name}' has no S-Matrix factory.");

            wavelengthMap = new Dictionary<int, SMatrix>
            {
                { 1550, sMatrix },
                { 1310, sMatrix },
                { 980, sMatrix }
            };
        }

        // Create physical pins linked to logical pins
        var physicalPins = new List<PhysicalPin>();
        for (int i = 0; i < template.PinDefinitions.Length; i++)
        {
            var def = template.PinDefinitions[i];
            physicalPins.Add(new PhysicalPin
            {
                Name = def.Name,
                OffsetXMicrometers = def.OffsetX,
                OffsetYMicrometers = def.OffsetY,
                AngleDegrees = def.AngleDegrees,
                LogicalPin = logicalPins[i]
            });
        }

        // Use explicit NazcaFunctionName if set, otherwise generate from name
        var nazcaFunction = template.NazcaFunctionName
            ?? $"nazca_{template.Name.ToLower().Replace(" ", "_")}";
        var nazcaParams = template.NazcaParameters ?? "";

        var component = new Component(
            wavelengthMap,
            sliders,
            nazcaFunction,
            nazcaParams,
            parts,
            0,
            instanceName,
            DiscreteRotation.R0,
            physicalPins);

        component.PhysicalX = x;
        component.PhysicalY = y;
        component.WidthMicrometers = template.WidthMicrometers;
        component.HeightMicrometers = template.HeightMicrometers;
        component.NazcaOriginOffsetX = template.NazcaOriginOffsetX;
        component.NazcaOriginOffsetY = template.NazcaOriginOffsetY;
        component.NazcaModuleName = template.NazcaModuleName;

        // Set human-readable display name from the template's display name.
        // This ensures components placed from the library show their PDK display name
        // (e.g., "Grating Coupler TE 1550") rather than their NazcaFunctionName (e.g., "ebeam_gc_te1550")
        // when grouped into prefabs and later instantiated.
        component.HumanReadableName = template.Name;

        return component;
    }

}

public partial class ComponentTemplate : ObservableObject
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public double WidthMicrometers { get; set; }
    public double HeightMicrometers { get; set; }
    public PinDefinition[] PinDefinitions { get; set; } = Array.Empty<PinDefinition>();
    public bool HasSlider { get; set; }
    public double SliderMin { get; set; }
    public double SliderMax { get; set; }

    /// <summary>
    /// True when the user-global <see cref="Services.UserSMatrixOverrideStore"/>
    /// holds an entry under this template's <c>"{PdkSource}::{Name}"</c> key —
    /// drives the 📊 badge in the PDK library list. Refreshed by
    /// <see cref="ComponentLibraryViewModel.RefreshUserGlobalOverrideBadges"/>
    /// after any import or delete in the Component Settings dialog and after
    /// project load (which can migrate template overrides from .lun files).
    /// </summary>
    [ObservableProperty]
    private bool _hasUserGlobalSMatrixOverride;
    public Func<List<Pin>, SMatrix>? CreateSMatrix { get; set; }
    public Func<List<Pin>, List<Slider>, SMatrix>? CreateSMatrixWithSliders { get; set; }

    /// <summary>
    /// Optional factory for multi-wavelength S-matrices (e.g., from measured .sparam data).
    /// When set, takes precedence over CreateSMatrix for building the wavelength map.
    /// </summary>
    public Func<List<Pin>, Dictionary<int, SMatrix>>? CreateWavelengthSMatrixMap { get; set; }

    /// <summary>
    /// Nazca function name for export (e.g., "pdk.mmi2x2").
    /// If not set, uses a default based on the Name.
    /// </summary>
    public string? NazcaFunctionName { get; set; }

    /// <summary>
    /// Optional Nazca function parameters (e.g., "length=50").
    /// </summary>
    public string? NazcaParameters { get; set; }

    /// <summary>
    /// Identifies which PDK this component comes from (e.g., "SiEPIC EBeam", "Built-in").
    /// </summary>
    public string PdkSource { get; set; } = "Built-in";

    /// <summary>
    /// Offset from our top-left origin to Nazca's component origin (a0 pin position).
    /// Used by the exporter to correctly place components with .put(x, y).
    /// </summary>
    public double NazcaOriginOffsetX { get; set; } = 0;
    public double NazcaOriginOffsetY { get; set; } = 0;

    /// <summary>
    /// Python module name for Nazca import (e.g., "siepic_ebeam_pdk").
    /// </summary>
    public string? NazcaModuleName { get; set; }
}

public class PinDefinition
{
    public string Name { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double AngleDegrees { get; }

    /// <summary>
    /// Signal domain of the pin: <see cref="MatterType.Light"/> (optical, default)
    /// or <see cref="MatterType.Electricity"/> (electrical metal contact).
    /// </summary>
    public MatterType Kind { get; }

    public PinDefinition(string name, double offsetX, double offsetY, double angleDegrees, MatterType kind = MatterType.Light)
    {
        Name = name;
        OffsetX = offsetX;
        OffsetY = offsetY;
        AngleDegrees = angleDegrees;
        Kind = kind;
    }
}
