using System.Globalization;
using CAP_Core.Components.Connections;

namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Applies manual per-bend radius overrides to a routed waveguide path.
/// A bend between two straight segments is re-built with a new radius by moving its
/// tangent points along the (unchanged) tangent lines, shrinking or extending the
/// adjacent straight segments accordingly. Applying an override freezes the route.
/// </summary>
public static class BendRadiusEditor
{
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>
    /// Absolute lower bound for a bend radius in micrometers, used as the fallback minimum when
    /// no fabrication process defines a stricter waveguide minimum bend radius. Callers that know
    /// the active process should pass its resolved minimum to
    /// <see cref="TryApplyOverride(WaveguideConnection, int, double, out string?, double)"/>.
    /// </summary>
    public const double MinRadiusMicrometers = 0.1;
    private const double MaxSweepDegreesForEdit = 179.0;

    /// <summary>
    /// Tries to set the radius of the bend at <paramref name="bendIndex"/> (index among
    /// the path's bend segments, 0-based) to <paramref name="newRadiusMicrometers"/>.
    /// On success the override is recorded, the route is frozen and losses are refreshed.
    /// </summary>
    /// <param name="connection">The connection whose routed path is edited.</param>
    /// <param name="bendIndex">0-based index of the bend along the path.</param>
    /// <param name="newRadiusMicrometers">Desired bend radius in micrometers.</param>
    /// <param name="error">Human-readable reason when the edit is not possible.</param>
    /// <param name="minRadiusMicrometers">
    /// Lower bound the new radius must not fall below — the active fabrication process' minimum
    /// waveguide bend radius. Defaults to <see cref="MinRadiusMicrometers"/> when no process is
    /// resolvable, preserving the previous absolute-minimum behaviour.
    /// </param>
    /// <returns>True when the bend was rebuilt with the new radius.</returns>
    public static bool TryApplyOverride(WaveguideConnection connection, int bendIndex,
                                        double newRadiusMicrometers, out string? error,
                                        double minRadiusMicrometers = MinRadiusMicrometers)
    {
        error = null;
        var segments = connection.RoutedPath?.Segments;
        if (segments == null || segments.Count == 0)
        {
            error = "Connection has no routed path.";
            return false;
        }
        double effectiveMin = minRadiusMicrometers > 0 ? minRadiusMicrometers : MinRadiusMicrometers;
        if (newRadiusMicrometers < effectiveMin)
        {
            error = string.Format(CultureInfo.InvariantCulture,
                "Radius must be ≥ {0:0.###} µm (process minimum).", effectiveMin);
            return false;
        }

        int segmentIndex = FindBendSegmentIndex(segments, bendIndex);
        if (segmentIndex < 0)
        {
            error = $"Bend #{bendIndex + 1} not found (path has {CountBends(segments)} bends).";
            return false;
        }

        if (!TryRebuildBend(segments, segmentIndex, newRadiusMicrometers, out error))
            return false;

        connection.BendRadiusOverrides[bendIndex] = newRadiusMicrometers;
        connection.IsRouteFrozen = true;
        connection.UpdateLossFromPath();
        return true;
    }

    /// <summary>Counts the bend segments of a path.</summary>
    public static int CountBends(IReadOnlyList<PathSegment> segments)
        => segments.Count(s => s is BendSegment);

    /// <summary>
    /// Returns a <see cref="BendCorner"/> for every <b>resizable</b> bend of the path — a
    /// <see cref="BendSegment"/> flanked by two <see cref="StraightSegment"/>s and not at a pin —
    /// mirroring the guards in <see cref="TryRebuildBend"/>. Used to position in-canvas radius
    /// handles. Non-resizable bends are silently skipped but still advance the bend index, so the
    /// returned <see cref="BendCorner.BendIndex"/> matches the index accepted by
    /// <see cref="TryApplyOverride"/>.
    /// </summary>
    /// <param name="segments">The connection's routed path segments.</param>
    /// <returns>One entry per resizable bend, in path order.</returns>
    public static IReadOnlyList<BendCorner> GetBendCorners(IReadOnlyList<PathSegment> segments)
    {
        var corners = new List<BendCorner>();
        if (segments == null || segments.Count == 0)
            return corners;

        int bendIndex = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i] is not BendSegment bend)
                continue;
            bendIndex++;

            if (!IsResizable(segments, i, bend))
                continue;
            if (TryBuildCorner(bend, bendIndex, out var corner))
                corners.Add(corner);
        }
        return corners;
    }

    /// <summary>True when the bend at <paramref name="segmentIndex"/> can be resized
    /// (not pin-adjacent, flanked by straights, sweep within the editable range).</summary>
    private static bool IsResizable(IReadOnlyList<PathSegment> segments, int segmentIndex, BendSegment bend)
    {
        if (segmentIndex == 0 || segmentIndex == segments.Count - 1)
            return false;
        if (segments[segmentIndex - 1] is not StraightSegment || segments[segmentIndex + 1] is not StraightSegment)
            return false;
        double sweepAbs = Math.Abs(bend.SweepAngleDegrees);
        return sweepAbs >= double.Epsilon && sweepAbs <= MaxSweepDegreesForEdit;
    }

    /// <summary>Computes the corner, bisector and handle factor of a resizable bend.
    /// Returns false when the half-sweep sine is degenerate (bisector/factor undefined).</summary>
    private static bool TryBuildCorner(BendSegment bend, int bendIndex, out BendCorner corner)
    {
        const double epsilon = 1e-9;
        corner = default!;

        double halfSweepRad = Math.Abs(bend.SweepAngleDegrees) * DegreesToRadians / 2.0;
        double sinHalf = Math.Sin(halfSweepRad);
        if (sinHalf <= epsilon)
            return false;

        var dirIn = UnitVector(bend.StartAngleDegrees);
        var dirOut = UnitVector(bend.EndAngleDegrees);
        double tangent = bend.RadiusMicrometers * Math.Tan(halfSweepRad);
        var cornerPoint = (X: bend.StartPoint.X + tangent * dirIn.X,
                           Y: bend.StartPoint.Y + tangent * dirIn.Y);

        var (bx, by) = (dirOut.X - dirIn.X, dirOut.Y - dirIn.Y);
        double bisLength = Math.Sqrt(bx * bx + by * by);
        if (bisLength <= epsilon)
            return false;
        var bisector = (X: bx / bisLength, Y: by / bisLength);

        // Corner→center distance is √(τ² + r²) = r/cos(halfSweep) (right triangle over the
        // tangent point), so the arc's nearest point to the corner — where the handle is
        // drawn — sits at r·(1/cos(halfSweep) − 1) along the bisector.
        double cosHalf = Math.Cos(halfSweepRad);
        if (cosHalf <= epsilon)
            return false;
        double handleFactor = 1.0 / cosHalf - 1.0;
        corner = new BendCorner(bendIndex, cornerPoint, bisector, bend.RadiusMicrometers, handleFactor);
        return true;
    }

    /// <summary>Returns the segment index of the n-th bend, or -1 when out of range.</summary>
    private static int FindBendSegmentIndex(IReadOnlyList<PathSegment> segments, int bendIndex)
    {
        if (bendIndex < 0)
            return -1;
        int seen = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i] is BendSegment && seen++ == bendIndex)
                return i;
        }
        return -1;
    }

    private static bool TryRebuildBend(IReadOnlyList<PathSegment> segments, int segmentIndex,
                                       double newRadius, out string? error)
    {
        error = null;
        var bend = (BendSegment)segments[segmentIndex];

        if (segmentIndex == 0 || segmentIndex == segments.Count - 1)
        {
            error = "Bends directly at a pin cannot be resized.";
            return false;
        }
        if (segments[segmentIndex - 1] is not StraightSegment before ||
            segments[segmentIndex + 1] is not StraightSegment after)
        {
            error = "Only bends between two straight segments can be resized.";
            return false;
        }

        double sweepAbs = Math.Abs(bend.SweepAngleDegrees);
        if (sweepAbs < double.Epsilon || sweepAbs > MaxSweepDegreesForEdit)
        {
            error = "This bend's sweep angle does not allow resizing.";
            return false;
        }

        // The corner is the intersection of the two tangent lines. It stays fixed while
        // the tangent length t = r * tan(|sweep| / 2) changes with the radius.
        double halfSweepRad = sweepAbs * DegreesToRadians / 2.0;
        double oldTangent = bend.RadiusMicrometers * Math.Tan(halfSweepRad);
        double newTangent = newRadius * Math.Tan(halfSweepRad);

        var dirIn = UnitVector(bend.StartAngleDegrees);
        var dirOut = UnitVector(bend.EndAngleDegrees);
        var corner = (X: bend.StartPoint.X + oldTangent * dirIn.X,
                      Y: bend.StartPoint.Y + oldTangent * dirIn.Y);

        var newStart = (X: corner.X - newTangent * dirIn.X, Y: corner.Y - newTangent * dirIn.Y);
        var newEnd = (X: corner.X + newTangent * dirOut.X, Y: corner.Y + newTangent * dirOut.Y);

        if (!FitsOnStraights(before, after, corner, newTangent))
        {
            error = "Radius too large: adjacent straight segments are too short.";
            return false;
        }

        // Center sits perpendicular to the start tangent, on the turn side.
        double startRad = bend.StartAngleDegrees * DegreesToRadians;
        double perp = startRad - Math.PI / 2 * Math.Sign(bend.SweepAngleDegrees);
        bend.Center = (newStart.X - newRadius * Math.Cos(perp), newStart.Y - newRadius * Math.Sin(perp));
        bend.RadiusMicrometers = newRadius;
        bend.StartPoint = newStart;
        bend.EndPoint = newEnd;

        before.EndPoint = newStart;
        after.StartPoint = newEnd;
        return true;
    }

    /// <summary>Checks that both adjacent straights keep a non-negative length.</summary>
    private static bool FitsOnStraights(StraightSegment before, StraightSegment after,
                                        (double X, double Y) corner, double tangentLength)
    {
        double beforeAvailable = Distance(before.StartPoint, corner);
        double afterAvailable = Distance(after.EndPoint, corner);
        return tangentLength <= beforeAvailable && tangentLength <= afterAvailable;
    }

    private static (double X, double Y) UnitVector(double angleDegrees)
    {
        double rad = angleDegrees * DegreesToRadians;
        return (Math.Cos(rad), Math.Sin(rad));
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
