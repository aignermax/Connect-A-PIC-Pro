using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Library;

public static class ComponentTemplates
{
    private static int _componentCounter = 0;

    [Obsolete("Use JSON PDK files (demo-pdk.json, siepic-ebeam-pdk.json) as the source of component templates. This method returns an empty list.")]
    public static List<ComponentTemplate> GetAllTemplates() => new List<ComponentTemplate>();

    public static Component CreateFromTemplate(ComponentTemplate template, double x, double y)
    {
        _componentCounter++;
        var instanceName = $"{template.Name}_{_componentCounter}";

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
            logicalPins.Add(new Pin(def.Name, i, def.Kind, side) { Polarization = def.Polarization });
        }

        var parts = new Part[1, 1];
        parts[0, 0] = new Part(logicalPins);

        var sliders = new List<Slider>();
        if (template.HasSlider)
        {
            sliders.Add(new Slider(Guid.NewGuid(), 0, (template.SliderMin + template.SliderMax) / 2, template.SliderMax, template.SliderMin));
        }

        Dictionary<int, SMatrix> wavelengthMap;
        if (template.CreateWavelengthSMatrixMap != null)
        {
            wavelengthMap = template.CreateWavelengthSMatrixMap(logicalPins);
        }
        else
        {
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
        component.GdsFactoryFunction = template.GdsFactoryFunction;
        component.GdsFactoryRoutingCrossSection = template.GdsFactoryRoutingCrossSection;

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

    [ObservableProperty]
    private bool _hasUserGlobalSMatrixOverride;
    public Func<List<Pin>, SMatrix>? CreateSMatrix { get; set; }
    public Func<List<Pin>, List<Slider>, SMatrix>? CreateSMatrixWithSliders { get; set; }

    public Func<List<Pin>, Dictionary<int, SMatrix>>? CreateWavelengthSMatrixMap { get; set; }

    public string? NazcaFunctionName { get; set; }

    public string? NazcaParameters { get; set; }

    public string PdkSource { get; set; } = "Built-in";

    public double NazcaOriginOffsetX { get; set; } = 0;
    public double NazcaOriginOffsetY { get; set; } = 0;

    public string? NazcaModuleName { get; set; }

    public string? GdsFactoryFunction { get; set; }

    public string? GdsFactoryRoutingCrossSection { get; set; }

    public string? RawCode { get; set; }

    public string? RawCodeBackend { get; set; }

    public bool IsCustom { get; set; }
}

public class PinDefinition
{
    public string Name { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double AngleDegrees { get; }

    public MatterType Kind { get; }

    public PolarizationKind Polarization { get; }

    public PinDefinition(string name, double offsetX, double offsetY, double angleDegrees,
        MatterType kind = MatterType.Light, PolarizationKind polarization = PolarizationKind.TE)
    {
        Name = name;
        OffsetX = offsetX;
        OffsetY = offsetY;
        AngleDegrees = angleDegrees;
        Kind = kind;
        Polarization = polarization;
    }
}
