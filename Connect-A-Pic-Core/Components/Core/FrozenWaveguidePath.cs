using System.Numerics;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;

namespace CAP_Core.Components.Core;

/// <summary>
/// Represents a waveguide path that is frozen (fixed geometry).
/// Unlike regular RoutedPath, these don't recalculate during group moves.
/// The path geometry is stored in absolute coordinates and translated when the group moves.
/// </summary>
public class FrozenWaveguidePath : ICloneable
{
    /// <summary>
    /// The routed path segments with fixed geometry.
    /// </summary>
    public RoutedPath Path { get; set; }

    /// <summary>
    /// Physical pin where this frozen path starts.
    /// </summary>
    public PhysicalPin StartPin { get; set; }

    /// <summary>
    /// Physical pin where this frozen path ends.
    /// </summary>
    public PhysicalPin EndPin { get; set; }

    /// <summary>
    /// Unique identifier for this frozen path.
    /// </summary>
    public Guid PathId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Propagation loss in dB per centimeter.
    /// Default matches the standard waveguide loss used in WaveguideConnection.
    /// Default: 0.5 dB/cm (high-quality strip waveguide)
    /// </summary>
    public double PropagationLossDbPerCm { get; set; } = 0.5;

    /// <summary>
    /// Routing style of the original connection (Auto/Bend/SBend/Cobra).
    /// Preserved so re-expanding the group (edit mode, ungroup, template
    /// instantiation) restores the user's chosen style instead of "Auto".
    /// </summary>
    public WaveguideType ConnectionType { get; set; } = WaveguideType.Auto;

    /// <summary>
    /// Bend radius of the original connection in micrometers.
    /// </summary>
    public double BendRadiusMicrometers { get; set; } = WaveguideConnection.DefaultBendRadiusMicrometers;

    /// <summary>
    /// Waveguide width of the original connection in micrometers.
    /// </summary>
    public double WidthMicrometers { get; set; } = WaveguideConnection.DefaultWidthMicrometers;

    /// <summary>
    /// Whether the original connection's route was frozen (manually pinned geometry).
    /// </summary>
    public bool IsRouteFrozen { get; set; }

    /// <summary>
    /// Manual per-bend radius overrides of the original connection, keyed by bend index.
    /// </summary>
    public Dictionary<int, double> BendRadiusOverrides { get; } = new();

    /// <summary>
    /// Captures the per-connection routing settings (style, radius, width, freeze
    /// flag, bend overrides, propagation loss) from a live connection so they
    /// survive while the connection only exists as a frozen path inside a group.
    /// </summary>
    /// <param name="connection">The live connection to capture settings from.</param>
    public void CaptureSettingsFrom(WaveguideConnection connection)
    {
        ConnectionType = connection.Type;
        BendRadiusMicrometers = connection.BendRadiusMicrometers;
        WidthMicrometers = connection.WidthMicrometers;
        IsRouteFrozen = connection.IsRouteFrozen;
        PropagationLossDbPerCm = connection.PropagationLossDbPerCm;
        BendRadiusOverrides.Clear();
        foreach (var (bendIndex, radius) in connection.BendRadiusOverrides)
            BendRadiusOverrides[bendIndex] = radius;
    }

    /// <summary>
    /// Applies the stored routing settings back onto a live connection, used when
    /// the group is expanded again (group edit mode or ungroup).
    /// </summary>
    /// <param name="connection">The live connection to restore settings onto.</param>
    public void ApplySettingsTo(WaveguideConnection connection)
    {
        connection.Type = ConnectionType;
        connection.BendRadiusMicrometers = BendRadiusMicrometers;
        connection.WidthMicrometers = WidthMicrometers;
        connection.IsRouteFrozen = IsRouteFrozen;
        connection.PropagationLossDbPerCm = PropagationLossDbPerCm;
        connection.BendRadiusOverrides.Clear();
        foreach (var (bendIndex, radius) in BendRadiusOverrides)
            connection.BendRadiusOverrides[bendIndex] = radius;
    }

    /// <summary>
    /// Copies the stored routing settings from another frozen path (deep-copy and
    /// clone support).
    /// </summary>
    /// <param name="source">The frozen path to copy settings from.</param>
    public void CopySettingsFrom(FrozenWaveguidePath source)
    {
        ConnectionType = source.ConnectionType;
        BendRadiusMicrometers = source.BendRadiusMicrometers;
        WidthMicrometers = source.WidthMicrometers;
        IsRouteFrozen = source.IsRouteFrozen;
        PropagationLossDbPerCm = source.PropagationLossDbPerCm;
        BendRadiusOverrides.Clear();
        foreach (var (bendIndex, radius) in source.BendRadiusOverrides)
            BendRadiusOverrides[bendIndex] = radius;
    }

    /// <summary>
    /// Amplitude transmission coefficient accounting for propagation loss.
    /// Returns Complex.One when no path is available (conservative, no loss assumed).
    /// Formula: amplitude = 10^(-loss_dB / 20), where loss_dB = PropagationLossDbPerCm * length_cm.
    /// </summary>
    public Complex TransmissionCoefficient
    {
        get
        {
            if (Path?.Segments == null || Path.Segments.Count == 0)
                return Complex.One;

            double lengthMicrometers = Path.TotalLengthMicrometers;
            double lengthCm = lengthMicrometers / 10_000.0;
            double lossDb = PropagationLossDbPerCm * lengthCm;
            double amplitude = Math.Pow(10.0, -lossDb / 20.0);
            return new Complex(amplitude, 0);
        }
    }

    /// <summary>
    /// Translates all segments in the path by the specified delta.
    /// Used when moving the containing ComponentGroup.
    /// </summary>
    /// <param name="deltaX">X offset in micrometers.</param>
    /// <param name="deltaY">Y offset in micrometers.</param>
    public void TranslateBy(double deltaX, double deltaY)
    {
        if (Path?.Segments == null) return;

        foreach (var segment in Path.Segments)
        {
            segment.StartPoint = (
                segment.StartPoint.X + deltaX,
                segment.StartPoint.Y + deltaY
            );
            segment.EndPoint = (
                segment.EndPoint.X + deltaX,
                segment.EndPoint.Y + deltaY
            );

            // If it's a bend segment, translate the center point as well
            if (segment is BendSegment bend)
            {
                bend.Center = (
                    bend.Center.X + deltaX,
                    bend.Center.Y + deltaY
                );
            }
        }
    }

    /// <summary>
    /// Creates a clone of this frozen path with a new ID.
    /// </summary>
    public object Clone()
    {
        var clonedPath = new RoutedPath
        {
            IsBlockedFallback = Path.IsBlockedFallback,
            IsInvalidGeometry = Path.IsInvalidGeometry
        };

        // Deep clone all segments
        foreach (var segment in Path.Segments)
        {
            if (segment is BendSegment bend)
            {
                clonedPath.Segments.Add(new BendSegment(
                    bend.Center.X,
                    bend.Center.Y,
                    bend.RadiusMicrometers,
                    bend.StartAngleDegrees,
                    bend.SweepAngleDegrees
                ));
            }
            else if (segment is StraightSegment straight)
            {
                clonedPath.Segments.Add(new StraightSegment(
                    straight.StartPoint.X,
                    straight.StartPoint.Y,
                    straight.EndPoint.X,
                    straight.EndPoint.Y,
                    straight.StartAngleDegrees
                ));
            }
        }

        var clone = new FrozenWaveguidePath
        {
            Path = clonedPath,
            PathId = Guid.NewGuid(),
            // StartPin and EndPin references must be updated after cloning by the ComponentGroup
        };
        clone.CopySettingsFrom(this);
        return clone;
    }
}
