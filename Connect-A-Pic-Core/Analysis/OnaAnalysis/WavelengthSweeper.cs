using CAP_Core.Components.Core;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using System.Numerics;

namespace CAP_Core.Analysis.OnaAnalysis
{
    /// <summary>
    /// Orchestrates an ONA (Optical Network Analyzer) wavelength sweep.
    /// At each wavelength step the system S-matrix is recomputed (using
    /// <see cref="WavelengthInterpolator"/> for smooth spectra) and the
    /// field propagation is calculated with all configured input ports active,
    /// bypassing the single-wavelength laser-type filter used in regular simulation.
    /// </summary>
    public class WavelengthSweeper
    {
        private readonly ISystemMatrixBuilder _matrixBuilder;
        private readonly IExternalPortManager _portManager;

        /// <summary>
        /// Creates a wavelength sweeper.
        /// </summary>
        /// <param name="matrixBuilder">Builds the system S-matrix at each target wavelength.</param>
        /// <param name="portManager">Provides the active input port configuration.</param>
        public WavelengthSweeper(ISystemMatrixBuilder matrixBuilder, IExternalPortManager portManager)
        {
            _matrixBuilder = matrixBuilder ?? throw new ArgumentNullException(nameof(matrixBuilder));
            _portManager = portManager ?? throw new ArgumentNullException(nameof(portManager));
        }

        /// <summary>
        /// Runs the wavelength sweep and returns the complete result including insertion-loss
        /// data and any pre-sweep diagnostic warnings.
        /// </summary>
        /// <param name="configuration">Sweep range and step count.</param>
        /// <param name="gridManager">
        ///   Grid used to check component wavelength coverage and emit warnings.
        /// </param>
        /// <param name="cancellationToken">Allows the caller to cancel mid-sweep.</param>
        public async Task<WavelengthSweepResult> RunSweepAsync(
            WavelengthSweepConfiguration configuration,
            GridManager gridManager,
            CancellationToken cancellationToken = default)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (gridManager == null) throw new ArgumentNullException(nameof(gridManager));

            var warnings = CollectWavelengthCoverageWarnings(gridManager, configuration);
            double inputPower = ComputeTotalInputPower();
            var wavelengths = configuration.GenerateWavelengthValues();
            var dataPoints = new List<WavelengthDataPoint>(wavelengths.Length);
            List<Guid>? monitoredPinIds = null;

            foreach (int wl in wavelengths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var systemMatrix = _matrixBuilder.GetSystemSMatrix(wl);
                var usedInputs = _portManager.GetUsedExternalInputs(); // No wavelength filter for ONA
                var inputVector = UsedInputConverter.ToVectorOfFields(usedInputs, systemMatrix);

                int stepCount = SMatrix.DefaultMaxIterations;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var fields = await systemMatrix.CalcFieldAtPinsAfterStepsAsync(inputVector, stepCount, cts)
                    ?? new Dictionary<Guid, Complex>();

                monitoredPinIds ??= fields.Keys.ToList();
                dataPoints.Add(new WavelengthDataPoint(wl, fields, inputPower));
            }

            return new WavelengthSweepResult(
                configuration,
                dataPoints,
                monitoredPinIds ?? new List<Guid>(),
                warnings);
        }

        private double ComputeTotalInputPower()
        {
            double total = 0;
            foreach (var input in _portManager.GetAllExternalInputs())
                total += input.InFlowPower.Magnitude;
            return total > 0 ? total : 1.0; // fallback to 1.0 so dB stays finite
        }

        private static List<string> CollectWavelengthCoverageWarnings(
            GridManager gridManager,
            WavelengthSweepConfiguration config)
        {
            var warnings = new List<string>();
            var seenTypes = new HashSet<string>();

            foreach (var component in gridManager.TileManager.GetAllComponents())
            {
                // Virtual analysis tools (e.g. ONA Analyzer) don't have meaningful
                // wavelength data — they're simulation-only probes. Their map gets
                // populated from a synthetic 980/1310/1550 triple by the template
                // loader, which would spuriously trigger this warning.
                if (component.IsAnalysisTool) continue;

                // gdsfactory-backend components have no real Nazca function — their
                // NazcaFunctionName is a synthesized "nazca_<name>" placeholder, which made
                // warnings mislabel e.g. 'cspdk.sin300.coupler_straight' as
                // 'nazca_coupler_straight' (#712). Prefer the real gdsfactory factory name.
                string key = component.GdsFactoryFunction
                    ?? component.NazcaFunctionName
                    ?? component.Identifier;
                var definedWavelengths = component.WaveLengthToSMatrixMap.Keys;

                if (definedWavelengths.Count == 1)
                {
                    if (seenTypes.Add(key))
                    {
                        warnings.Add(
                            $"Component '{key}' has only one defined wavelength " +
                            $"({definedWavelengths.First()} nm). " +
                            "Nearest-neighbour fallback will be used — spectrum may appear flat.");
                    }
                }
                else if (definedWavelengths.Count > 1)
                {
                    int minDefined = definedWavelengths.Min();
                    int maxDefined = definedWavelengths.Max();
                    bool sweepExtrapolates = config.StartNm < minDefined || config.EndNm > maxDefined;

                    if (sweepExtrapolates && seenTypes.Add(key))
                    {
                        warnings.Add(
                            $"Component '{key}' is defined only between {minDefined}–{maxDefined} nm " +
                            $"but the sweep range is {config.StartNm}–{config.EndNm} nm. " +
                            "Nearest-neighbour fallback will be used outside the defined bracket — results may be inaccurate.");
                    }
                }
            }

            return warnings;
        }
    }
}
