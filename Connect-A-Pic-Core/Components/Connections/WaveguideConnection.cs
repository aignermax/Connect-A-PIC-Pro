using System.Numerics;
using CAP_Core.LightCalculation.MaterialDispersion;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Components.Core;

namespace CAP_Core.Components.Connections
{
    /// <summary>
    /// Represents a waveguide routing connection between two physical pins.
    /// Automatically calculates transmission coefficient based on geometry and loss parameters.
    /// </summary>
    public class WaveguideConnection
    {
        /// <summary>Default waveguide width in micrometers (standard: 500 nm strip).</summary>
        public const double DefaultWidthMicrometers = 0.5;

        /// <summary>Default bend radius in micrometers.</summary>
        public const double DefaultBendRadiusMicrometers = 10.0;

        public Guid Id { get; set; } = Guid.NewGuid();
        public PhysicalPin StartPin { get; set; }
        public PhysicalPin EndPin { get; set; }
        public double WidthMicrometers { get; set; } = DefaultWidthMicrometers;
        public double BendRadiusMicrometers { get; set; } = DefaultBendRadiusMicrometers;
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
        /// Manual perpendicular shifts of straight segments in micrometers, keyed by the index
        /// of the segment among the path's straight segments (0 = first straight along the
        /// path). Recorded by <c>SegmentShiftEditor</c>; cleared together with
        /// <see cref="BendRadiusOverrides"/> whenever the connection is re-routed from scratch.
        /// </summary>
        public Dictionary<int, double> StraightShiftOffsets { get; } = new();

        /// <summary>
        /// True when the routed path carries manual in-canvas edits (bend radius overrides or
        /// straight segment shifts) that must survive recalculation while the endpoints match.
        /// </summary>
        public bool HasManualPathEdits => BendRadiusOverrides.Count > 0 || StraightShiftOffsets.Count > 0;

        /// <summary>
        /// Tolerance in micrometers for deciding whether a frozen path still matches
        /// the current pin positions.
        /// </summary>
        public const double FrozenEndpointToleranceMicrometers = 1.0;

        /// <summary>
        /// True when this connection joins electrical pins and must be laid out as a
        /// metal trace instead of an optical waveguide (issue #682). Cross-kind pairs
        /// are rejected at creation time (see PinKindHelper), so checking one pin of
        /// the pair is authoritative.
        /// </summary>
        public bool IsElectrical =>
            StartPin?.MatterType == MatterType.Electricity ||
            EndPin?.MatterType == MatterType.Electricity;

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
        /// GDS layer of the route polygons this connection was derived from during a
        /// GDS import (paired with <see cref="SourceGdsDataType"/>), when ALL source
        /// polygons share one layer — the layer the connection's geometry returns to
        /// on export (manufacturing needs the original layers, not the process
        /// default). Null for connections created inside the app and for ambiguous
        /// multi-layer sources; exports then use the process defaults, unchanged.
        /// </summary>
        public int? SourceGdsLayer { get; set; }

        /// <summary>
        /// GDS datatype of the import source polygons — see <see cref="SourceGdsLayer"/>.
        /// </summary>
        public int? SourceGdsDataType { get; set; }

        /// <summary>
        /// Optional target geometric length (µm) this connection's route was stretched to
        /// with a meander (issue #1008). Null means no length intent — the route is whatever
        /// the router produces. Set by the meander actuator together with a frozen route;
        /// persisted in .lun files so the intent survives save/load.
        /// </summary>
        public double? TargetLengthMicrometers { get; set; }

        /// <summary>
        /// Accepted deviation (µm) from <see cref="TargetLengthMicrometers"/>.
        /// Null when no target length is set.
        /// </summary>
        public double? LengthToleranceMicrometers { get; set; }

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

            // The effective bend radius honors both the connection's own setting and the
            // fabrication process' minimum: the larger of the two governs the geometry.
            // The floor is per-pin-pair: electrical pairs bend at the metal cross-section
            // floor (issue #854), optical pairs at their endpoints' process floor when the
            // router carries a provider (issue #937), else the canvas-wide floor.
            double effectiveBendRadius = Math.Max(BendRadiusMicrometers, router.ResolveProcessFloorFor(StartPin, EndPin));

            if (Type != WaveguideType.Auto)
            {
                // A styled route the user has hand-edited (bend radius via the canvas handles,
                // recorded in BendRadiusOverrides) is sacred: keep it as long as its endpoints
                // still match the pins, only refreshing the losses. Rebuilding here would
                // silently wipe the manual edit on every unrelated recalculation. A joint move
                // of both components is a pure translation and keeps the edits too.
                if (HasManualPathEdits &&
                    (FrozenPathStillMatchesPins() || JointMoveRouteTranslator.TryTranslateToPins(this)))
                {
                    UpdateLossFromPath(wavelengthNm);
                    return;
                }

                // Explicit style: the visible curve is the styled primitive rebuilt from the
                // current pins, so it tracks component moves while ignoring obstacles by design.
                // An endpoint move (or style change, which invalidates the route) discards any
                // manual bend edits — mirroring the Auto unfreeze behavior. Frozen so incremental
                // routing and the exporter treat it as a fixed route and never replace it with
                // the A* result.
                BendRadiusOverrides.Clear();
                StraightShiftOffsets.Clear();
                var styledPath = ConnectionStyleRouteBuilder.Build(StartPin, EndPin, Type, effectiveBendRadius);
                if (styledPath != null)
                {
                    RoutedPath = styledPath;
                    IsRouteFrozen = true;
                    UpdateLossFromPath(wavelengthNm);
                    return;
                }

                // The styled primitive cannot leave the start pin along the pin direction for
                // this layout (e.g. the end pin lies behind the start pin). Rather than drawing
                // a broken curve into the component, fall through to the A* route below; the
                // style is kept and takes effect again once the layout allows it.
                IsRouteFrozen = false;
            }

            if (IsRouteFrozen)
            {
                // Both endpoints moved by the same delta (joint drag of both components):
                // translate the frozen geometry instead of unfreezing it.
                if (FrozenPathStillMatchesPins() || JointMoveRouteTranslator.TryTranslateToPins(this))
                {
                    // Keep manually edited geometry; just refresh the loss values.
                    UpdateLossFromPath(wavelengthNm);
                    return;
                }

                // An endpoint moved: unfreeze and discard manual bend and segment-shift edits.
                IsRouteFrozen = false;
                BendRadiusOverrides.Clear();
                StraightShiftOffsets.Clear();
            }

            // Update router settings. The router owns the process floor: it first attempts
            // max(connection radius, process minimum) and degrades to the connection radius
            // with RoutedPath.ViolatesProcessMinBendRadius set when the floor finds no clean path.
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
        /// Atomically replaces <see cref="RoutedPath"/> with a new instance and refreshes the
        /// loss. Background post-processing (e.g. the pin-lead collapse) computes on a private
        /// copy and publishes the finished result through this single reference assignment, so
        /// the UI thread never observes half-mutated segments of the live path.
        /// </summary>
        /// <param name="path">The finished replacement path (a fresh instance, not the live one).</param>
        public void ReplaceRoutedPath(RoutedPath path)
        {
            RoutedPath = path;
            UpdateLossFromPath();
        }

        /// <summary>
        /// Recalculates <see cref="TotalLossDb"/> and <see cref="TransmissionCoefficient"/>
        /// from the current <see cref="RoutedPath"/> geometry.
        /// </summary>
        /// <param name="wavelengthNm">Wavelength in nm used when a <see cref="DispersionModel"/> is set.</param>
        public void UpdateLossFromPath(double wavelengthNm = 1550.0)
        {
            // Calculate total loss from actual path. Smooth polyline styles (SBend/Cobra)
            // contain no BendSegments, so BendCount is 0 for them: their bend loss is
            // APPROXIMATED as pure propagation loss over the sampled curve length — a
            // conservative simplification for adiabatic curves without a single radius.
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
        /// Discards the current <see cref="RoutedPath"/> so the next routing pass rebuilds it from
        /// scratch. Needed when the routing intent changes (e.g. the user picks a new
        /// <see cref="Type"/>) but no component moved: incremental routing keeps any route whose
        /// endpoints still match, so without this the stale path would survive and the new style
        /// would only take effect after the next component move.
        /// </summary>
        public void InvalidateRoute()
        {
            RoutedPath = null;
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
