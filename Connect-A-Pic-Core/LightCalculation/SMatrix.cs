using System.Numerics;
using MathNet.Numerics.LinearAlgebra;
using System.Linq.Dynamic.Core;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.Components.Parametric;

namespace CAP_Core.LightCalculation
{
    public class SMatrix
    {
        /// <summary>
        /// Safety cap for the Neumann-series iteration loop.  Residual-based convergence
        /// (<see cref="DefaultConvergenceEpsilon"/>) will terminate the loop early for
        /// well-converged circuits; this constant only fires for near-lossless resonators
        /// where the series does not contract.
        /// </summary>
        public const int DefaultMaxIterations = 10_000;

        /// <summary>
        /// Field-vector L2-norm delta threshold for declaring convergence.
        /// When ‖field_n − field_{n-1}‖₂ falls below this value the Neumann series
        /// has converged and the iteration stops regardless of <c>maxIterations</c>.
        /// </summary>
        public const double DefaultConvergenceEpsilon = 1e-9;

        public Matrix<Complex> SMat { get; private set; } // the SMat works like SMat[PinNROutflow, PinNRInflow] --> so opposite from what one might expect
        public readonly Dictionary<Guid, int> PinReference; // all PinIDs inside of the matrix. the int is the index of the row/column in the SMat.. and also of the inputVector.
        public Dictionary<Guid, double> SliderReference { get; internal set; }
        private readonly Dictionary<int, Guid> ReversePinReference; // sometimes we want to find the GUID and only have the ID
        private readonly int size;
        public const int MaxToStringPinGuidSize = 6;
        public Dictionary<(Guid PinIdStart, Guid PinIdEnd), ConnectionFunction> NonLinearConnections { get; set; }

        /// <summary>
        /// Optional rebuild factory used by <c>Component.Clone()</c> when the
        /// S-matrix was produced by a parametric PDK template. Rebuilding via
        /// this factory gives the clone its own <c>ParametricSMatrix</c>
        /// instance (isolated parameter state) and avoids trying to re-parse
        /// the parametric formula through <c>MathExpressionReader</c>, which
        /// would otherwise throw because the raw-formula string is not valid
        /// NCalc syntax. <c>null</c> for non-parametric matrices.
        /// Convention: set only by the PDK template converter at construction
        /// and carried forward by <c>Component.Clone</c>. Do not mutate from
        /// elsewhere — the "set once, propagate on clone" invariant would
        /// break. The setter is <c>public</c> because the template converter
        /// lives in a separate assembly (<c>CAP.Avalonia</c>).
        /// </summary>
        public Func<List<Pin>, List<Slider>, SMatrix>? ParametricRebuild { get; set; }

        /// <summary>
        /// Immutable parameter/formula definitions this matrix was built from
        /// (<see cref="ParametricSMatrixSnapshot"/>), or <c>null</c> for non-parametric
        /// matrices. Unlike <see cref="ParametricRebuild"/> (an opaque factory), the
        /// snapshot exposes the actual formulas and parameter metadata, so serializers
        /// can persist them and rebuild an equivalent live matrix after a disk
        /// round-trip. Set once by <c>ParametricSMatrixFactory.Build</c> and carried
        /// forward by rebuilds; the instance is immutable and freely shareable.
        /// </summary>
        public ParametricSMatrixSnapshot? ParametricSnapshot { get; set; }

        public SMatrix(List<Guid> allPinsInGrid, List<(Guid sliderID, double value)> AllSliders)
        {
            if (allPinsInGrid != null && allPinsInGrid.Count > 0)
            {
                size = allPinsInGrid.Count;
            }
            else
            {
                size = 0;
            }

            SMat = Matrix<Complex>.Build.Sparse(size, size);
            // initialize PinReferences
            PinReference = new();
            ReversePinReference = new();
            int i = 0;
            foreach (var pin in allPinsInGrid)
            {
                PinReference.Add(pin, i);
                ReversePinReference.Add(i, pin);
                i++;
            }
            NonLinearConnections = new();
            SliderReference = new();
            foreach (var slider in AllSliders)
            {
                SliderReference.Add(slider.sliderID, slider.value);
            }
        }

        public void SetValues(Dictionary<(Guid PinIdInflow, Guid PinIdOutflow), Complex> transfers, bool reset = false)
        {
            if (transfers == null || PinReference == null)
            {
                return;
            }

            if (reset)
            {
                SMat = Matrix<Complex>.Build.Sparse(size, size);
            }

            foreach (var relation in transfers.Keys)
            {
                if (PinReference.ContainsKey(relation.PinIdInflow) && PinReference.ContainsKey(relation.PinIdOutflow))
                {
                    int indexInflow = PinReference[relation.PinIdInflow];
                    int indexOutflow = PinReference[relation.PinIdOutflow];
                    SMat[indexOutflow, indexInflow] = transfers[relation];
                }
            }
        }

        public Dictionary<(Guid PinIdStart, Guid PinIdEnd), Complex> GetNonNullValues()
        {
            var transfers = new Dictionary<(Guid inflow, Guid outflow), Complex>();
            for (int iOut = 0; iOut < size; iOut++)
            {
                for (int iIn = 0; iIn < size; iIn++)
                {
                    if (SMat[iOut, iIn] == Complex.Zero) continue;
                    transfers[(ReversePinReference[iIn], ReversePinReference[iOut])] = SMat[iOut, iIn];
                }
            }
            return transfers;
        }

        public static SMatrix CreateSystemSMatrix(List<SMatrix> matrices)
        {
            var allPinIDs = matrices.SelectMany(x => x.PinReference.Keys).Distinct().ToList();
            var allSliderIDs = matrices.SelectMany(x => x.SliderReference.Select(k => (k.Key, k.Value))).ToList(); // convert SliderReference to the required tuple
            SMatrix sysMat = new(allPinIDs, allSliderIDs);

            foreach (SMatrix matrix in matrices)
            {
                var transfers = matrix.GetNonNullValues();
                var nonLinearTransfers = matrix.NonLinearConnections;
                sysMat.SetValues(transfers);
                // also copy the nonlinear functions
                foreach (var key in nonLinearTransfers.Keys)
                {
                    sysMat.NonLinearConnections.Add(key, nonLinearTransfers[key]);
                }
            }
            return sysMat;
        }

        /// <summary>
        /// Iterates the Neumann series  field = Σ Sⁿ · input  until the field-vector
        /// L2-norm delta falls below <paramref name="convergenceEpsilon"/> or
        /// <paramref name="maxIterations"/> steps are reached (safety cap).
        ///
        /// The residual-based stop criterion replaces the old fixed-step heuristic
        /// (pinCount×2) and correctly handles high-reflectivity / deep-cavity circuits
        /// where the geometric series converges slowly (round-trip factor → 1).
        /// </summary>
        /// <param name="inputVector">Steady-state input amplitude at each pin.</param>
        /// <param name="maxIterations">
        ///   Hard upper bound on iterations.  Use <see cref="DefaultMaxIterations"/> unless
        ///   a tighter bound is known to be sufficient (e.g. unit tests for non-resonant circuits).
        /// </param>
        /// <param name="cancellation">Cancellation support for user-triggered stop.</param>
        /// <param name="convergenceEpsilon">
        ///   L2-norm threshold; iteration stops when ‖field_n − field_{n-1}‖₂ &lt; epsilon.
        ///   Defaults to <see cref="DefaultConvergenceEpsilon"/> (1e-9).
        /// </param>
        public async Task<Dictionary<Guid, Complex>> CalcFieldAtPinsAfterStepsAsync(
            MathNet.Numerics.LinearAlgebra.Vector<Complex> inputVector,
            int maxIterations,
            CancellationTokenSource cancellation,
            double convergenceEpsilon = DefaultConvergenceEpsilon)
        {
            if (maxIterations < 1) return new Dictionary<Guid, Complex>();

            // Update SMat using non-linear connections that don't depend on the current field.
            await RecomputeSMatNonLinearPartsAsync(inputVector, SkipOuterLoopFunctions: false);
            try
            {
                var inputAfterSteps = SMat * inputVector + inputVector;
                var converged = false;

                for (int i = 1; i < maxIterations && !converged; i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await Task.Run(async () =>
                    {
                        var previousField = inputAfterSteps;
                        // Recalculate non-linear entries because the field vector has changed
                        // (e.g. logic gates that switch based on optical power).
                        await RecomputeSMatNonLinearPartsAsync(inputAfterSteps, SkipOuterLoopFunctions: true);
                        inputAfterSteps = SMat * inputAfterSteps + inputVector;

                        // Residual-based convergence: stop when the field change is negligible.
                        double delta = (inputAfterSteps - previousField).L2Norm();
                        converged = delta < convergenceEpsilon;
                    }, cancellation.Token);
                }

                return ConvertToDictWithGuids(inputAfterSteps);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected during user-triggered stop; return
                // empty so the caller can handle it without noise.
                return new Dictionary<Guid, Complex>();
            }
            // Any other exception (formula evaluation error, matrix singular,
            // division by zero in a parametric connection, ...) previously
            // got swallowed here and produced a blank simulation with no
            // indication of what went wrong. Let it propagate so the UI
            // error-handling layer can log and surface it.
        }

        private List<object> GetWeightParameters(IEnumerable<Guid> parameterGuids, MathNet.Numerics.LinearAlgebra.Vector<Complex>? inputVector)
        {
            List<object> usedParameterValues = new();
            foreach (var paramGuid in parameterGuids)
            {
                // first check if the parameterGuid is in the pin-Dict
                if (PinReference.TryGetValue(paramGuid, out int pinNumber))
                {
                    if (inputVector == null)
                        throw new InvalidOperationException(
                            "Field-dependent formula connection needs the current field vector; " +
                            "only parameter-only connections may be evaluated without one.");
                    usedParameterValues.Add(inputVector[pinNumber]);
                }
                // check if parameterGuid is in the slider Dict
                else if (SliderReference.TryGetValue(paramGuid, out double sliderPosition))
                {
                    usedParameterValues.Add(sliderPosition);
                }
            }

            return usedParameterValues;
        }

        /// <summary>
        /// Evaluates every parameter-only (slider-driven) formula connection once and writes
        /// the resulting constant into the linear matrix. Such connections depend on design
        /// parameters, not on the optical field, so the circuit stays linear for a fixed
        /// parameter set — callers that require a purely linear system (e.g. the time-domain
        /// simulation) can use this to resolve them instead of rejecting the design.
        /// Field-dependent (inner-loop) connections are left untouched.
        /// </summary>
        public void EvaluateParameterOnlyConnections()
        {
            foreach (var connection in NonLinearConnections)
            {
                if (connection.Value.IsInnerLoopFunction)
                    continue;
                var weightParameters = GetWeightParameters(connection.Value.UsedParameterGuids, inputVector: null);
                var calculatedWeight = connection.Value.CalcConnectionWeightAsync(weightParameters);
                SMat[PinReference[connection.Key.PinIdEnd], PinReference[connection.Key.PinIdStart]] = calculatedWeight;
            }
        }

        /// <summary>
        /// Returns a lightweight copy of this matrix in which every parameter-only
        /// (slider-driven) formula connection has been resolved to the numeric value
        /// implied by the current slider positions. Field-dependent (inner-loop)
        /// connections cannot be resolved without a field and stay unevaluated.
        /// The original matrix is left untouched — used when a numeric snapshot of
        /// the current parameter state is needed (e.g. prefab serialization) without
        /// baking values into the live component.
        /// </summary>
        public SMatrix CreateEvaluatedSnapshot()
        {
            var snapshot = new SMatrix(
                PinReference.Keys.ToList(),
                SliderReference.Select(kv => (kv.Key, kv.Value)).ToList())
            {
                NonLinearConnections = NonLinearConnections,
                ParametricRebuild = ParametricRebuild,
                ParametricSnapshot = ParametricSnapshot
            };
            snapshot.SetValues(GetNonNullValues());
            snapshot.EvaluateParameterOnlyConnections();
            return snapshot;
        }
        private async Task RecomputeSMatNonLinearPartsAsync(MathNet.Numerics.LinearAlgebra.Vector<Complex> inputVector, bool SkipOuterLoopFunctions = true)
        {
            foreach (var connection in NonLinearConnections)
            {
                if (connection.Value.IsInnerLoopFunction == false && SkipOuterLoopFunctions == true)// some functions 
                    continue;
                var indexStart = PinReference[connection.Key.PinIdStart];
                var indexEnd = PinReference[connection.Key.PinIdEnd];
                var weightParameters = GetWeightParameters(connection.Value.UsedParameterGuids, inputVector);
                var calculatedWeight = connection.Value.CalcConnectionWeightAsync(weightParameters);
                SMat[indexEnd, indexStart] = calculatedWeight;
            }
        }

        private Dictionary<Guid, Complex> ConvertToDictWithGuids(MathNet.Numerics.LinearAlgebra.Vector<Complex> lightPropagationVector)
        {
            var GuidsAndLightValues = new Dictionary<Guid, Complex>();
            for (int i = 0; i < lightPropagationVector.Count; i++)
            {
                GuidsAndLightValues.Add(ReversePinReference[i], lightPropagationVector[i]);
            }
            return GuidsAndLightValues;
        }

    }
}
