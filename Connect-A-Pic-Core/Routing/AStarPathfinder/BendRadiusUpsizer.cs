using CAP_Core.Routing;

namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Post-routing pass that grows bend radii to the largest allowed value the surrounding
/// free space permits — photonic bend loss shrinks rapidly with radius, so generous space
/// should yield the gentlest bend. It runs AFTER every connection is routed and registered
/// as a grid obstacle, so each candidate arc is vetoed against ALL sibling routes at their
/// final positions. Doing this during path smoothing instead would let early routes claim
/// large arcs in space later routes still need, forcing new crossings (the route order is
/// arbitrary from the user's point of view). Upsizing never adds or removes segments: a
/// bend's radius grows and its two neighbouring straights shed exactly the extra tangent
/// length, keeping the path's topology — and therefore export geometry counts — stable.
/// </summary>
public class BendRadiusUpsizer
{
    /// <summary>Sweep angle above which the tangent length formula is clamped (tan(45°) = 1).</summary>
    private const double MaxTangentSweepDegrees = 90.0;

    /// <summary>Minimum straight length (µm) each neighbour must keep after shedding
    /// tangent length, so no straight degenerates to a point.</summary>
    private const double MinRemainingStraightMicrometers = 0.5;

    /// <summary>Maximum deviation (µm) between the rebuilt arc's endpoint and the point the
    /// outgoing straight geometry predicts; larger means the corner is not a clean tangent
    /// corner and is left untouched.</summary>
    private const double EndpointToleranceMicrometers = 0.05;

    private readonly PathfindingGrid _grid;
    private readonly List<double> _allowedRadii;

    /// <summary>Creates an upsizer sampling candidate arcs against <paramref name="grid"/>.</summary>
    /// <param name="grid">Pathfinding grid holding every sibling route and component obstacle.</param>
    /// <param name="allowedRadii">Foundry-style discrete radii (µm) bends may snap to.</param>
    public BendRadiusUpsizer(PathfindingGrid grid, IEnumerable<double> allowedRadii)
    {
        _grid = grid;
        _allowedRadii = allowedRadii.OrderByDescending(r => r).ToList();
    }

    /// <summary>
    /// Grows every straight-bend-straight corner of <paramref name="path"/> to the largest
    /// allowed radius whose extra tangent length fits both straights and whose arc clears
    /// the grid. The caller must have removed the path's own grid obstacle beforehand.
    /// </summary>
    /// <returns>True when at least one bend was upsized.</returns>
    public bool TryUpsize(RoutedPath path)
    {
        bool changed = false;
        for (int i = 1; i < path.Segments.Count - 1; i++)
        {
            if (path.Segments[i] is not BendSegment bend ||
                path.Segments[i - 1] is not StraightSegment incoming ||
                path.Segments[i + 1] is not StraightSegment outgoing)
                continue;

            changed |= TryUpsizeCorner(path, i, bend, incoming, outgoing);
        }
        return changed;
    }

    /// <summary>Tries candidate radii for one corner, largest first, applying the first fit.</summary>
    private bool TryUpsizeCorner(RoutedPath path, int bendIndex,
        BendSegment bend, StraightSegment incoming, StraightSegment outgoing)
    {
        double sweep = Math.Abs(bend.SweepAngleDegrees);
        if (sweep < 2 || sweep > MaxTangentSweepDegrees + 0.5)
            return false;

        double tanHalfSweep = Math.Tan(
            Math.Min(sweep, MaxTangentSweepDegrees) * Math.PI / 360.0);

        foreach (var radius in _allowedRadii)
        {
            if (radius <= bend.RadiusMicrometers)
                return false; // descending order: nothing larger remains

            double tangentDelta = (radius - bend.RadiusMicrometers) * tanHalfSweep;
            if (incoming.LengthMicrometers - tangentDelta < MinRemainingStraightMicrometers ||
                outgoing.LengthMicrometers - tangentDelta < MinRemainingStraightMicrometers)
                continue;

            var candidate = BuildCandidate(bend, incoming, radius, tangentDelta);
            if (candidate == null ||
                !EndpointMatchesOutgoing(candidate, outgoing, tangentDelta) ||
                IsArcBlocked(candidate))
                continue;

            incoming.EndPoint = candidate.StartPoint;
            outgoing.StartPoint = candidate.EndPoint;
            path.Segments[bendIndex] = candidate;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Rebuilds the bend with <paramref name="radius"/>, starting <paramref name="tangentDelta"/>
    /// earlier along the incoming straight so the arc stays tangent to both legs.
    /// </summary>
    private static BendSegment? BuildCandidate(
        BendSegment bend, StraightSegment incoming, double radius, double tangentDelta)
    {
        double length = incoming.LengthMicrometers;
        if (length < MinRemainingStraightMicrometers)
            return null;

        double dirX = (incoming.EndPoint.X - incoming.StartPoint.X) / length;
        double dirY = (incoming.EndPoint.Y - incoming.StartPoint.Y) / length;
        double startX = bend.StartPoint.X - dirX * tangentDelta;
        double startY = bend.StartPoint.Y - dirY * tangentDelta;

        double bendDir = Math.Sign(bend.SweepAngleDegrees);
        if (bendDir == 0) return null;

        double startRad = bend.StartAngleDegrees * Math.PI / 180.0;
        double centerX = startX - Math.Sin(startRad) * bendDir * radius;
        double centerY = startY + Math.Cos(startRad) * bendDir * radius;

        return new BendSegment(centerX, centerY, radius,
            bend.StartAngleDegrees, bend.SweepAngleDegrees);
    }

    /// <summary>
    /// True when the rebuilt arc lands exactly where the outgoing straight geometry
    /// predicts — the point <paramref name="tangentDelta"/> along the straight.
    /// </summary>
    private static bool EndpointMatchesOutgoing(
        BendSegment candidate, StraightSegment outgoing, double tangentDelta)
    {
        double length = outgoing.LengthMicrometers;
        if (length < MinRemainingStraightMicrometers)
            return false;

        double dirX = (outgoing.EndPoint.X - outgoing.StartPoint.X) / length;
        double dirY = (outgoing.EndPoint.Y - outgoing.StartPoint.Y) / length;
        double expectedX = outgoing.StartPoint.X + dirX * tangentDelta;
        double expectedY = outgoing.StartPoint.Y + dirY * tangentDelta;

        double dx = candidate.EndPoint.X - expectedX;
        double dy = candidate.EndPoint.Y - expectedY;
        return dx * dx + dy * dy <=
            EndpointToleranceMicrometers * EndpointToleranceMicrometers;
    }

    /// <summary>
    /// Samples the candidate arc at half-cell steps against the grid's blocked cells.
    /// Endpoints are skipped — they legitimately touch the route's own straights.
    /// </summary>
    private bool IsArcBlocked(BendSegment bend)
    {
        double stepLength = _grid.CellSizeMicrometers * 0.5;
        var samples = ArcSampling.SamplePoints(bend, stepLength).ToList();

        for (int i = 1; i < samples.Count - 1; i++)
        {
            var (gx, gy) = _grid.PhysicalToGrid(samples[i].X, samples[i].Y);
            if (_grid.IsBlocked(gx, gy)) return true;
        }
        return false;
    }
}
