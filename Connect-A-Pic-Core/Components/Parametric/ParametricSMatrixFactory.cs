using System.Numerics;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;

namespace CAP_Core.Components.Parametric
{
    /// <summary>
    /// Builds live, slider-bound <see cref="SMatrix"/> instances from an immutable
    /// <see cref="ParametricSMatrixSnapshot"/>. Every build creates its own
    /// <see cref="ParametricSMatrix"/> evaluation state, so slider edits stay
    /// instance-scoped even though the snapshot itself is shared. Used both by the
    /// PDK template converter (initial placement) and by the group-template
    /// serializer (prefab round-trip), which must not depend on PDK draft DTOs.
    /// </summary>
    public static class ParametricSMatrixFactory
    {
        /// <summary>
        /// Builds a parametric S-matrix whose connections evaluate the snapshot's
        /// formulas against the given sliders' current values. The matrix keeps a
        /// <see cref="SMatrix.ParametricRebuild"/> factory and the
        /// <see cref="SMatrix.ParametricSnapshot"/> so clones and serializers can
        /// recover the parametric definition.
        /// </summary>
        /// <param name="pins">Logical pins of the component (matched to connections by name).</param>
        /// <param name="sliders">Sliders of the component, indexed by their position.</param>
        /// <param name="snapshot">Parameter and formula definitions.</param>
        public static SMatrix Build(
            List<Pin> pins,
            List<Slider> sliders,
            ParametricSMatrixSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var parametric = new ParametricSMatrix(snapshot.Parameters, snapshot.Connections);

            var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var sliderTuples = sliders.Select(s => (s.ID, s.Value)).ToList();
            var sMatrix = new SMatrix(pinIds, sliderTuples)
            {
                ParametricSnapshot = snapshot
            };
            sMatrix.ParametricRebuild = (newPins, newSliders) =>
                Build(newPins, newSliders, snapshot);

            var pinByName = new Dictionary<string, Pin>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in pins)
                pinByName[pin.Name] = pin;

            var paramToSliderGuid = MapParametersToSliderGuids(snapshot, sliders);
            var orderedParamSliders = snapshot.Parameters
                .Where(p => paramToSliderGuid.ContainsKey(p.Name))
                .Select(p => (p.Name, SliderGuid: paramToSliderGuid[p.Name]))
                .ToList();
            var usedSliderGuids = orderedParamSliders.Select(x => x.SliderGuid).ToList();

            foreach (var conn in snapshot.Connections)
            {
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
                    for (int i = 0; i < capturedParamSliders.Count && i < parameters.Count; i++)
                    {
                        double val = Convert.ToDouble(parameters[i]);
                        capturedParametric.SetParameterValue(capturedParamSliders[i].Name, val);
                    }

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
        /// Maps each slider-bound parameter name to the GUID of the slider at the
        /// position its <see cref="ParameterDefinition.SliderNumber"/> points to.
        /// </summary>
        private static Dictionary<string, Guid> MapParametersToSliderGuids(
            ParametricSMatrixSnapshot snapshot,
            List<Slider> sliders)
        {
            var paramToSliderGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var param in snapshot.Parameters)
            {
                if (param.SliderNumber is not int sn)
                    continue;
                if (sn < 0 || sn >= sliders.Count)
                    throw new InvalidOperationException(
                        $"Parameter '{param.Name}' references sliderNumber {sn}, " +
                        $"but only {sliders.Count} slider(s) exist on this instance.");
                paramToSliderGuid[param.Name] = sliders[sn].ID;
            }
            return paramToSliderGuid;
        }
    }
}
