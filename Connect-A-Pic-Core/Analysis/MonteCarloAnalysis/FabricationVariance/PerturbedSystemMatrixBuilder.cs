using System.Numerics;
using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;

namespace CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance
{
    /// <summary>
    /// Decorates an <see cref="ISystemMatrixBuilder"/> with per-component fabrication
    /// perturbations. After the inner builder assembles the system S-matrix,
    /// every intra-component transfer is multiplied by the component's sampled loss/phase
    /// factor; couplers are additionally evaluated at a shifted wavelength and MMIs receive
    /// a port imbalance. Magnitudes are clamped to max(1, |nominal|) per the passivity
    /// policy. Component state is never mutated — with no active sample the inner matrix
    /// passes through unchanged.
    /// </summary>
    public sealed class PerturbedSystemMatrixBuilder : ISystemMatrixBuilder
    {
        /// <summary>Physical length of one grid tile in µm (the PDK's _CellSize).</summary>
        public const double NominalTileLengthUm = 250;

        private readonly ISystemMatrixBuilder _inner;
        private readonly FabricationVarianceSource _source;
        private readonly Dictionary<Guid, Component> _pinOwners = new();
        private readonly Dictionary<Component, List<Guid>> _orderedOutFlowIds = new();

        /// <summary>
        /// Wraps <paramref name="inner"/> so that the variance sample active on
        /// <paramref name="source"/> perturbs every built system matrix.
        /// </summary>
        public PerturbedSystemMatrixBuilder(ISystemMatrixBuilder inner, FabricationVarianceSource source)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            IndexComponentPins();
        }

        /// <inheritdoc />
        public SMatrix GetSystemSMatrix(int LaserWaveLengthInNm)
        {
            var matrix = _inner.GetSystemSMatrix(LaserWaveLengthInNm);
            var deviations = _source.CurrentDeviations;
            if (deviations == null) return matrix;

            var perturbations = ComputePerturbations(deviations, LaserWaveLengthInNm);
            PerturbLinearEntries(matrix, perturbations, LaserWaveLengthInNm);
            PerturbNonLinearEntries(matrix, perturbations);
            return matrix;
        }

        private void IndexComponentPins()
        {
            foreach (var component in _source.Components)
            {
                var outFlowIds = new List<Guid>();
                foreach (var pin in component.GetAllPins())
                {
                    _pinOwners[pin.IDInFlow] = component;
                    _pinOwners[pin.IDOutFlow] = component;
                    outFlowIds.Add(pin.IDOutFlow);
                }
                _orderedOutFlowIds[component] = outFlowIds;
            }
        }

        private static Dictionary<Component, SMatrixPerturbation> ComputePerturbations(
            IReadOnlyDictionary<Component, ComponentDeviation> deviations, int wavelengthNm)
        {
            var perturbations = new Dictionary<Component, SMatrixPerturbation>();
            foreach (var (component, deviation) in deviations)
            {
                perturbations[component] = VarianceSensitivityModel.Compute(
                    ComponentVarianceClassifier.Classify(component),
                    deviation,
                    wavelengthNm,
                    EstimateLengthUm(component));
            }
            return perturbations;
        }

        /// <summary>Optical path length estimate: the component's larger tile span × tile pitch.</summary>
        private static double EstimateLengthUm(Component component) =>
            Math.Max(component.Parts.GetLength(0), component.Parts.GetLength(1)) * NominalTileLengthUm;

        private void PerturbLinearEntries(
            SMatrix matrix,
            Dictionary<Component, SMatrixPerturbation> perturbations,
            int wavelengthNm)
        {
            var updates = new Dictionary<(Guid, Guid), Complex>();
            var shiftedBases = new Dictionary<Component, Dictionary<(Guid, Guid), Complex>>();

            foreach (var entry in matrix.GetNonNullValues())
            {
                var component = OwnerOfBoth(entry.Key);
                if (component == null || !perturbations.TryGetValue(component, out var perturbation))
                    continue;

                Complex baseValue = ResolveBaseValue(component, perturbation, entry, wavelengthNm, shiftedBases);
                updates[entry.Key] = Perturb(baseValue, perturbation, entry.Key.PinIdEnd, component);
            }
            matrix.SetValues(updates);
        }

        /// <summary>
        /// The nominal transfer the perturbation multiplies: for spectrally shifted kinds
        /// (couplers) the component's own S-matrix evaluated at wavelength − shift, so the
        /// whole coupling spectrum moves; otherwise the entry as built.
        /// </summary>
        private static Complex ResolveBaseValue(
            Component component,
            SMatrixPerturbation perturbation,
            KeyValuePair<(Guid PinIdStart, Guid PinIdEnd), Complex> entry,
            int wavelengthNm,
            Dictionary<Component, Dictionary<(Guid, Guid), Complex>> shiftedBases)
        {
            if (perturbation.WavelengthShiftNm == 0 || component.WaveLengthToSMatrixMap.Count < 2)
                return entry.Value;

            if (!shiftedBases.TryGetValue(component, out var shifted))
            {
                int shiftedTargetNm = wavelengthNm - (int)Math.Round(perturbation.WavelengthShiftNm);
                shifted = WavelengthInterpolator
                    .GetMatrix(component.WaveLengthToSMatrixMap, shiftedTargetNm, out _)
                    .GetNonNullValues();
                shiftedBases[component] = shifted;
            }
            return shifted.TryGetValue(entry.Key, out var shiftedValue) ? shiftedValue : entry.Value;
        }

        private Complex Perturb(
            Complex baseValue, SMatrixPerturbation perturbation, Guid outFlowId, Component component)
        {
            double amplitude = perturbation.AmplitudeFactor
                * (1.0 + ImbalanceSign(component, outFlowId) * perturbation.ImbalanceFraction);
            var value = baseValue * Complex.FromPolarCoordinates(
                Math.Max(amplitude, 0), perturbation.PhaseRadians);

            double passivityCap = Math.Max(1.0, baseValue.Magnitude);
            return value.Magnitude > passivityCap
                ? value * (passivityCap / value.Magnitude)
                : value;
        }

        /// <summary>
        /// Deterministic ± sign of the MMI imbalance per output pin (alternating in pin
        /// order), so one port gains what the other loses across the whole run.
        /// </summary>
        private double ImbalanceSign(Component component, Guid outFlowId)
        {
            int index = _orderedOutFlowIds[component].IndexOf(outFlowId);
            return index >= 0 && index % 2 == 1 ? -1.0 : 1.0;
        }

        /// <summary>
        /// Formula-driven (parametric) entries are recomputed during field iteration, which
        /// would overwrite a static perturbation — so their functions are wrapped to apply
        /// the loss/phase factor after evaluation (|factor| ≤ 1 keeps them passive).
        /// </summary>
        private void PerturbNonLinearEntries(
            SMatrix matrix, Dictionary<Component, SMatrixPerturbation> perturbations)
        {
            foreach (var key in matrix.NonLinearConnections.Keys.ToList())
            {
                var component = OwnerOfBoth(key);
                if (component == null || !perturbations.TryGetValue(component, out var perturbation))
                    continue;

                var original = matrix.NonLinearConnections[key];
                var factor = Complex.FromPolarCoordinates(
                    perturbation.AmplitudeFactor, perturbation.PhaseRadians);
                var innerFunc = original.CalcConnectionWeightAsync;
                matrix.NonLinearConnections[key] = original with
                {
                    CalcConnectionWeightAsync = parameters => innerFunc(parameters) * factor,
                };
            }
        }

        /// <summary>The component owning BOTH pins, or null (inter-component connections).</summary>
        private Component? OwnerOfBoth((Guid PinIdStart, Guid PinIdEnd) key) =>
            _pinOwners.TryGetValue(key.PinIdStart, out var start)
            && _pinOwners.TryGetValue(key.PinIdEnd, out var end)
            && ReferenceEquals(start, end)
                ? start
                : null;
    }
}
