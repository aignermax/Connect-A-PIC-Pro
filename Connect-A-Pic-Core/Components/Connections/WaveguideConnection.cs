using System.Numerics;
using CAP_Core.LightCalculation.MaterialDispersion;
using CAP_Core.Routing;
using CAP_Core.Components.Core;

namespace CAP_Core.Components.Connections
{
    /// <summary>
    /// Represents a waveguide routing connection between two physical pins.
    /// Automatically calculates transmission coefficient based on geometry and loss parameters.
    /// </summary>
    public class WaveguideConnection
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public PhysicalPin StartPin { get; set; }
        public PhysicalPin EndPin { get; set; }
        public double WidthMicrometers { get; set; } = 0.5; // Standard: 500nm
        public double BendRadiusMicrometers { get; set; } = 10.0;
        public WaveguideType Type { get; set; } = WaveguideType.Auto;

        /// <summary>
        /// Indicates whether this connection is locked (cannot be deleted or modified).
        /// </summary>
        public bool IsLocked { get; set; }

        /// <summary>
        /// When true, the current routed path is frozen: re-routing keeps the existing
        /// geometry as long as both endpoints still match. Moving an endpoint beyond the
        /// tolerance automatically unfreezes the connection and clears bend overrides.
        /// Set automatically when a manual per-bend radius override is applied.
        /// </summary>
        public bool IsRouteFrozen { get; set; }

        /// <summary>
        /// Manual per-bend radius overrides in micrometers, keyed by the index of the
        /// bend among the path's bend segments (0 = first bend along the path).
        /// Cleared automatically when the connection is re-routed from scratch.
        /// </summary>
        public Dictionary<int, double> BendRadiusOverrides { get; } = new();

        /// <summary>
        /// Tolerance in micrometers for deciding whether a frozen path still matches
        /// the current pin positions.
        /// </summary>
        public const double FrozenEndpointToleranceMicrometers = 1.0;

        /// <summary>
        /// Target path length in micrometers. When set, the router will attempt to achieve this length.
        /// Null means no target length (just route the shortest valid path).
        /// </summary>
        public double? TargetLengthMicrometers { get; set; } = null;

        /// <summary>
        /// Whether the target length constraint is enabled.
        /// </summary>
        public bool IsTargetLengthEnabled { get; set; } = false;

        /// <summary>
        /// Tolerance for target length matching in micrometers. Default: ±1µm.
        /// </summary>
        public double LengthToleranceMicrometers { get; set; } = 1.0;

        /// <summary>
        /// Propagation loss in dB per centimeter.
        /// Typical values for silicon photonics:
        /// - High-quality strip waveguides: 0.3-0.5 dB/cm
        /// - Standard strip waveguides: 1-2 dB/cm
        /// - Rib waveguides: 0.5-1 dB/cm
        /// Default: 0.5 dB/cm (high-quality strip waveguide).
        /// When <see cref="DispersionModel"/> is set, its <c>LossDbPerCmAt</c> overrides this value.
        /// </summary>
        public double PropagationLossDbPerCm { get; set; } = 0.5;

        /// <summary>
        /// Optional wavelength-dependent dispersion model.
        /// When set, <see cref="RecalculateTransmission"/> and <see cref="RestoreCachedPath"/>
        /// query <see cref="IDispersionModel.LossDbPerCmAt"/> at the specified wavelength
        /// instead of using the scalar <see cref="PropagationLossDbPerCm"/>.
        /// </summary>
        public IDispersionModel? DispersionModel { get; set; }

        /// <summary>
        /// Loss per 90-degree bend in dB. Typical values: 0.01-0.1 dB per bend.
        /// </summary>
        public double BendLossDbPer90Deg { get; set; } = 0.05;

        /// <summary>
        /// The actual routed path with all segments (straights and bends).
        /// Populated after calling RecalculateTransmission().
        /// </summary>
        public RoutedPath? RoutedPath { get; private set; }

        /// <summary>
        /// Number of equivalent 90-degree bends in the routing.
        /// Calculated from actual path segments.
        /// </summary>
        public double BendCount => RoutedPath?.TotalEquivalent90DegreeBends ?? 0;

        /// <summary>
        /// Calculated path length in micrometers between the two pins.
        /// </summary>
        public double PathLengthMicrometers => RoutedPath?.TotalLengthMicrometers ?? 0;

        /// <summary>
        /// Difference between actual path length and target length in micrometers.
        /// Positive = actual is longer, negative = actual is shorter than target.
        /// Returns null if target length is not enabled or not set.
        /// </summary>
        public double? LengthDifference
        {
            get
            {
                if (!IsTargetLengthEnabled || !TargetLengthMicrometers.HasValue)
                    return null;
                return PathLengthMicrometers - TargetLengthMicrometers.Value;
            }
        }

        /// <summary>
        /// Indicates if the current path length matches the target within tolerance.
        /// Returns null if target length is not enabled.
        /// </summary>
        public bool? IsLengthMatched
        {
            get
            {
                if (!IsTargetLengthEnabled || !TargetLengthMicrometers.HasValue)
                    return null;
                var diff = Math.Abs(LengthDifference.Value);
                return diff <= LengthToleranceMicrometers;
            }
        }

        /// <summary>
        /// Gets the transmission coefficient calculated from current geometry and loss parameters.
        /// Call RecalculateTransmission() after component positions change.
        /// </summary>
        public Complex TransmissionCoefficient { get; private set; } = Complex.One;

        /// <summary>
        /// Total loss in dB for this connection.
        /// </summary>
        public double TotalLossDb { get; private set; }

        /// <summary>
        /// Recalculates the transmission coefficient based on current pin positions and loss parameters.
        /// Should be called whenever connected components are moved.
        /// </summary>
        /// <param name="router">The waveguide router to use for path calculation.</param>
        /// <param name="wavelengthNm">
        /// Wavelength in nm used when a <see cref="DispersionModel"/> is set.
        /// Defaults to 1550 nm when not provided.
        /// </param>
        /// <param name="cancellationToken">Token to cancel Phase 2 routing (e.g. when grid changes).</param>
        public void RecalculateTransmission(WaveguideRouter router,
                                             double wavelengthNm = 1550.0,
                                             CancellationToken cancellationToken = default)
        {
            if (StartPin == null || EndPin == null)
            {
                RoutedPath = null;
                TransmissionCoefficient = Complex.One;
                TotalLossDb = 0;
                return;
            }

            if (IsRouteFrozen)
            {
                if (FrozenPathStillMatchesPins())
                {
                    // Keep manually edited geometry; just refresh the loss values.
                    UpdateLossFromPath(wavelengthNm);
                    return;
                }

                // An endpoint moved: unfreeze and discard manual bend edits.
                IsRouteFrozen = false;
                BendRadiusOverrides.Clear();
            }

            // Update router settings
            router.MinBendRadiusMicrometers = BendRadiusMicrometers;

            // Route the connection using two-phase A* (Phase 1 quick, Phase 2 extended)
            RoutedPath = router.Route(StartPin, EndPin, cancellationToken);

            UpdateLossFromPath(wavelengthNm);
        }

        /// <summary>
        /// Checks whether the frozen path's endpoints still match the current pin positions
        /// within <see cref="FrozenEndpointToleranceMicrometers"/>.
        /// </summary>
        public bool FrozenPathStillMatchesPins()
        {
            if (RoutedPath == null || RoutedPath.Segments.Count == 0 || StartPin == null || EndPin == null)
                return false;

            var (startX, startY) = StartPin.GetAbsolutePosition();
            var (endX, endY) = EndPin.GetAbsolutePosition();
            var first = RoutedPath.Segments[0];
            var last = RoutedPath.Segments[^1];

            return Distance(first.StartPoint.X, first.StartPoint.Y, startX, startY) <= FrozenEndpointToleranceMicrometers
                && Distance(last.EndPoint.X, last.EndPoint.Y, endX, endY) <= FrozenEndpointToleranceMicrometers;
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Recalculates <see cref="TotalLossDb"/> and <see cref="TransmissionCoefficient"/>
        /// from the current <see cref="RoutedPath"/> geometry.
        /// </summary>
        /// <param name="wavelengthNm">Wavelength in nm used when a <see cref="DispersionModel"/> is set.</param>
        public void UpdateLossFromPath(double wavelengthNm = 1550.0)
        {
            // Calculate total loss from actual path
            double lossDbPerCm = DispersionModel?.LossDbPerCmAt(wavelengthNm) ?? PropagationLossDbPerCm;
            double propagationLoss = (PathLengthMicrometers / 10000.0) * lossDbPerCm; // µm to cm
            double bendLoss = BendCount * BendLossDbPer90Deg;
            TotalLossDb = propagationLoss + bendLoss;

            // Convert dB loss to linear amplitude coefficient
            // Loss in dB = -20 * log10(|amplitude|)
            // |amplitude| = 10^(-loss_dB / 20)
            double amplitudeCoefficient = Math.Pow(10, -TotalLossDb / 20.0);
            TransmissionCoefficient = new Complex(amplitudeCoefficient, 0);
        }

        /// <summary>
        /// Restores a previously cached routed path without invoking the router.
        /// Recalculates transmission loss from the provided path geometry.
        /// Used when loading designs with cached route data.
        /// </summary>
        /// <param name="cachedPath">The cached routed path to restore.</param>
        /// <param name="wavelengthNm">
        /// Wavelength in nm used when a <see cref="DispersionModel"/> is set.
        /// Defaults to 1550 nm when not provided.
        /// </param>
        public void RestoreCachedPath(RoutedPath cachedPath, double wavelengthNm = 1550.0)
        {
            RoutedPath = cachedPath;
            UpdateLossFromPath(wavelengthNm);
        }

        /// <summary>
        /// Gets all path segments for rendering or export.
        /// </summary>
        public IReadOnlyList<PathSegment> GetPathSegments()
        {
            return RoutedPath?.Segments ?? new List<PathSegment>();
        }

        /// <summary>
        /// Checks if the routed path is valid.
        /// </summary>
        public bool IsPathValid => RoutedPath?.IsValid ?? false;

        /// <summary>
        /// Indicates if this connection uses a fallback path that goes through obstacles.
        /// When true, the path should be displayed differently (e.g., red/dashed).
        /// </summary>
        public bool IsBlockedFallback => RoutedPath?.IsBlockedFallback ?? false;
    }
}
