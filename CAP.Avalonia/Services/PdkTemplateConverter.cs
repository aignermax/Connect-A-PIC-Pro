using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Components.Parametric;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using System.Numerics;

namespace CAP.Avalonia.Services;

public static class PdkTemplateConverter
{
    /// <summary>
    /// Converts a PDK component draft to a library template. Optical pins carry the
    /// PDK's waveguide width/layer onto every placed instance (DRC-lite pin-mismatch
    /// rule): an explicit per-pin draft value wins; otherwise the default optical
    /// cross-section of <paramref name="process"/> fills in. When neither exists the
    /// values stay null and the rule stays silent (legacy PDKs, playground).
    /// </summary>
    public static ComponentTemplate ConvertToTemplate(
        PdkComponentDraft pdkComp,
        string pdkName,
        string? nazcaModuleName,
        string? gdsFactoryRoutingCrossSection = null,
        ProcessDefinition? process = null)
    {
        var opticalDefaults = ProcessOpticalDefaultsResolver.Resolve(process);
        var pinDefs = pdkComp.Pins.Select(p =>
        {
            var kind = PinKindHelper.Parse(p.PinKind);
            bool isOptical = kind == MatterType.Light;
            return new PinDefinition(
                p.Name,
                p.OffsetXMicrometers,
                p.OffsetYMicrometers,
                p.AngleDegrees,
                kind,
                PolarizationRules.Resolve(p.Polarization, pdkComp.Name, pdkComp.NazcaFunction),
                p.WaveguideWidthMicrometers ?? (isOptical ? opticalDefaults.WidthUm : null),
                p.Layer ?? (isOptical ? opticalDefaults.Layer : null));
        }).ToArray();

        double nazcaOriginOffsetX = pdkComp.NazcaOriginOffsetX ?? 0;
        double nazcaOriginOffsetY = pdkComp.NazcaOriginOffsetY ?? 0;

        var template = new ComponentTemplate
        {
            Name = pdkComp.Name,
            Category = pdkComp.Category,
            WidthMicrometers = pdkComp.WidthMicrometers,
            HeightMicrometers = pdkComp.HeightMicrometers,
            PinDefinitions = pinDefs,
            NazcaFunctionName = pdkComp.NazcaFunction,
            GdsFactoryFunction = pdkComp.GdsFactoryFunction,
            GdsFactoryRoutingCrossSection = gdsFactoryRoutingCrossSection,
            NazcaParameters = pdkComp.NazcaParameters,
            HasSlider = pdkComp.Sliders?.Any() ?? false,
            SliderMin = pdkComp.Sliders?.FirstOrDefault()?.MinVal ?? 0,
            SliderMax = pdkComp.Sliders?.FirstOrDefault()?.MaxVal ?? 100,
            SliderDefinitions = BuildSliderDefinitions(pdkComp),
            ParameterDefinitions = ParametricSMatrixMapper.MapParameters(pdkComp.SMatrix?.Parameters),
            PdkSource = pdkName,
            NazcaModuleName = nazcaModuleName,
            NazcaOriginOffsetX = nazcaOriginOffsetX,
            NazcaOriginOffsetY = nazcaOriginOffsetY,
            RawCode = pdkComp.RawCode,
            RawCodeBackend = pdkComp.RawCodeBackend,
            OutlinePolygons = pdkComp.OutlinePolygons,
            SourceDraft = pdkComp,
        };

        if (pdkComp.SMatrix?.WavelengthData is { Count: > 0 } wlData)
        {
            template.CreateWavelengthSMatrixMap = pins =>
            {
                var map = new Dictionary<int, SMatrix>();
                foreach (var entry in wlData)
                {
                    var draft = new PdkSMatrixDraft
                    {
                        WavelengthNm = entry.WavelengthNm,
                        Connections = entry.Connections
                    };
                    map[entry.WavelengthNm] = CreateSMatrixFromPdk(pins, draft);
                }
                return map;
            };
        }
        else if (pdkComp.SMatrix != null && ParametricSMatrixMapper.IsParametric(pdkComp.SMatrix))
        {
            ParametricSMatrixMapper.Validate(
                pdkComp.SMatrix,
                pdkComp.Name,
                pdkComp.Pins,
                pdkComp.Sliders?.Count ?? 0);

            var capturedSMatrixDraft = pdkComp.SMatrix;
            template.CreateSMatrixWithSliders = (pins, sliders) =>
                BuildParametricSMatrix(pins, sliders, capturedSMatrixDraft);
        }
        else
        {
            template.CreateSMatrix = pins => CreateSMatrixFromPdk(pins, pdkComp.SMatrix);
        }

        return template;
    }

    /// <summary>
    /// Builds one <see cref="SliderDefinition"/> per PDK slider. A slider bound
    /// to a named parameter starts at that parameter's default value (so the
    /// placed instance matches the documented physics); unbound sliders start
    /// at the range midpoint, matching the legacy behaviour.
    /// </summary>
    private static IReadOnlyList<SliderDefinition> BuildSliderDefinitions(PdkComponentDraft pdkComp)
    {
        if (pdkComp.Sliders is not { Count: > 0 } sliderDrafts)
            return Array.Empty<SliderDefinition>();

        var parameters = pdkComp.SMatrix?.Parameters;
        return sliderDrafts.Select(s =>
        {
            var boundParam = parameters?.FirstOrDefault(p => p.SliderNumber == s.SliderNumber);
            double initial = boundParam?.DefaultValue ?? (s.MinVal + s.MaxVal) / 2;
            return new SliderDefinition(s.SliderNumber, s.MinVal, s.MaxVal, initial);
        }).ToList();
    }

    private static SMatrix BuildParametricSMatrix(
        List<Pin> pins,
        List<Slider> sliders,
        PdkSMatrixDraft sMatrixDraft)
    {
        var parametric = ParametricSMatrixMapper.MapToParametricSMatrix(sMatrixDraft);
        var snapshot = new ParametricSMatrixSnapshot(parametric.Parameters, parametric.Connections);
        return ParametricSMatrixFactory.Build(pins, sliders, snapshot);
    }

    public static SMatrix CreateSMatrixFromPdk(List<Pin> pins, PdkSMatrixDraft? sMatrixDraft)
    {
        var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var sMatrix = new SMatrix(pinIds, new List<(Guid, double)>());

        if (sMatrixDraft?.Connections == null || sMatrixDraft.Connections.Count == 0)
            return sMatrix;

        var pinByName = new Dictionary<string, Pin>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in pins)
            pinByName[pin.Name] = pin;

        var transfers = new Dictionary<(Guid, Guid), Complex>();

        foreach (var conn in sMatrixDraft.Connections)
        {
            if (!pinByName.TryGetValue(conn.FromPin, out var fromPin) ||
                !pinByName.TryGetValue(conn.ToPin, out var toPin))
                continue;

            var phaseRad = conn.PhaseDegrees * Math.PI / 180.0;
            var value = Complex.FromPolarCoordinates(conn.Magnitude, phaseRad);

            transfers[(fromPin.IDInFlow, toPin.IDOutFlow)] = value;
            transfers[(toPin.IDInFlow, fromPin.IDOutFlow)] = value;
        }

        sMatrix.SetValues(transfers);
        return sMatrix;
    }
}
