using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.Components.Parametric;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using System.Numerics;

namespace CAP.Avalonia.Services;

/// <summary>
/// Converts PDK component definitions from JSON (PdkComponentDraft) into
/// ComponentTemplate instances ready for placement on the canvas.
/// </summary>
public static class PdkTemplateConverter
{
    /// <summary>
    /// Converts a <see cref="PdkComponentDraft"/> into a <see cref="ComponentTemplate"/>.
    /// </summary>
    /// <param name="pdkComp">The PDK component draft from the loaded JSON file.</param>
    /// <param name="pdkName">Display name of the PDK (becomes PdkSource on the template).</param>
    /// <param name="nazcaModuleName">Optional Python module name for Nazca import generation.</param>
    /// <returns>A fully configured <see cref="ComponentTemplate"/> with S-Matrix factory.</returns>
    public static ComponentTemplate ConvertToTemplate(
        PdkComponentDraft pdkComp,
        string pdkName,
        string? nazcaModuleName)
    {
        var pinDefs = pdkComp.Pins.Select(p => new PinDefinition(
            p.Name,
            p.OffsetXMicrometers,
            p.OffsetYMicrometers,
            p.AngleDegrees,
            PolarizationRules.Resolve(p.Polarization, pdkComp.Name, pdkComp.NazcaFunction)
        )).ToArray();

        // NazcaOriginOffset is required — validated by PdkLoader.
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
            NazcaParameters = pdkComp.NazcaParameters,
            HasSlider = pdkComp.Sliders?.Any() ?? false,
            SliderMin = pdkComp.Sliders?.FirstOrDefault()?.MinVal ?? 0,
            SliderMax = pdkComp.Sliders?.FirstOrDefault()?.MaxVal ?? 100,
            PdkSource = pdkName,
            NazcaModuleName = nazcaModuleName,
            NazcaOriginOffsetX = nazcaOriginOffsetX,
            NazcaOriginOffsetY = nazcaOriginOffsetY,
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
            // Fail-at-load-time validation: unknown pin names, bad slider
            // indices, invalid formulas are caught here instead of silently
            // producing a broken simulation at run time.
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
    /// Builds an <see cref="SMatrix"/> with NonLinearConnections driven by slider values
    /// for components that define parametric S-matrices (formulas referencing slider parameters).
    /// </summary>
    private static SMatrix BuildParametricSMatrix(
        List<Pin> pins,
        List<Slider> sliders,
        PdkSMatrixDraft sMatrixDraft)
    {
        var parametric = ParametricSMatrixMapper.MapToParametricSMatrix(sMatrixDraft);

        var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var sliderTuples = sliders.Select(s => (s.ID, s.Value)).ToList();
        var sMatrix = new SMatrix(pinIds, sliderTuples);

        // Carry the draft so Component.Clone() can rebuild this S-matrix
        // against the cloned pins + sliders instead of trying to re-parse
        // the non-NCalc raw-formula string, and so each cloned instance gets
        // its own ParametricSMatrix with isolated _currentValues state.
        var capturedDraft = sMatrixDraft;
        sMatrix.ParametricRebuild = (newPins, newSliders) =>
            BuildParametricSMatrix(newPins, newSliders, capturedDraft);

        var pinByName = new Dictionary<string, Pin>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in pins)
            pinByName[pin.Name] = pin;

        // Build param name → slider GUID mapping using SliderNumber from the
        // draft. Bounds were already validated at PDK load time via
        // ParametricSMatrixMapper.Validate; any out-of-range index that
        // slips in here would throw deterministically instead of silently
        // leaving the parameter unbound.
        var paramToSliderGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var paramDraft in sMatrixDraft.Parameters ?? [])
        {
            if (paramDraft.SliderNumber is int sn)
            {
                if (sn < 0 || sn >= sliders.Count)
                    throw new InvalidOperationException(
                        $"Parameter '{paramDraft.Name}' references sliderNumber {sn}, " +
                        $"but only {sliders.Count} slider(s) exist on this instance.");
                paramToSliderGuid[paramDraft.Name] = sliders[sn].ID;
            }
        }

        // Get ordered list of (paramName, sliderGuid) for params that have slider bindings
        var orderedParamSliders = parametric.Parameters
            .Where(p => paramToSliderGuid.ContainsKey(p.Name))
            .Select(p => (p.Name, SliderGuid: paramToSliderGuid[p.Name]))
            .ToList();

        var usedSliderGuids = orderedParamSliders.Select(x => x.SliderGuid).ToList();

        foreach (var conn in parametric.Connections)
        {
            // Pin-name mismatch must throw instead of silently dropping the
            // connection. ParametricSMatrixMapper.Validate already enforces
            // pin-name validity at PDK load, so reaching this point means
            // something upstream mutated the draft or skipped validation.
            if (!pinByName.TryGetValue(conn.FromPin, out var fromPin))
                throw new InvalidOperationException(
                    $"Parametric connection references unknown pin '{conn.FromPin}'.");
            if (!pinByName.TryGetValue(conn.ToPin, out var toPin))
                throw new InvalidOperationException(
                    $"Parametric connection references unknown pin '{conn.ToPin}'.");

            var capturedConn = conn;
            var capturedParametric = parametric;
            var capturedParamSliders = orderedParamSliders;

            Func<List<object>, Complex> calcFunc = parameters =>
            {
                // Update parametric model with current slider values
                for (int i = 0; i < capturedParamSliders.Count && i < parameters.Count; i++)
                {
                    double val = Convert.ToDouble(parameters[i]);
                    capturedParametric.SetParameterValue(capturedParamSliders[i].Name, val);
                }

                // Find evaluated value for this specific connection. Using
                // Single (not FirstOrDefault) because EvaluatedConnection is
                // a record struct — a miss would silently return a
                // Complex.Zero default with no indication, producing a
                // correct-looking but wrong simulation result.
                var results = capturedParametric.EvaluateConnections();
                var match = results.Where(e =>
                    e.FromPin == capturedConn.FromPin && e.ToPin == capturedConn.ToPin).ToList();
                if (match.Count == 0)
                    throw new InvalidOperationException(
                        $"No evaluated connection for {capturedConn.FromPin}→{capturedConn.ToPin}.");
                return match[0].Value;
            };

            var rawFormula = $"mag={conn.MagnitudeFormula};phase={conn.PhaseDegFormula}";
            var connFn = new ConnectionFunction(calcFunc, rawFormula, usedSliderGuids, false);

            sMatrix.NonLinearConnections[(fromPin.IDInFlow, toPin.IDOutFlow)] = connFn;
            sMatrix.NonLinearConnections[(toPin.IDInFlow, fromPin.IDOutFlow)] = connFn;
        }

        return sMatrix;
    }

    /// <summary>
    /// Builds an <see cref="SMatrix"/> from a <see cref="PdkSMatrixDraft"/>.
    /// Each JSON connection entry creates both forward and reverse transfers.
    /// </summary>
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
