using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.CrossingInsertion;

/// <summary>
/// Describes a validated opportunity to insert a waveguide crossing component:
/// a new (re-routed) connection whose direct path intersects an existing
/// connection at a right angle, together with the geometry needed for placement.
/// </summary>
public class CrossingCandidate
{
    /// <summary>The connection whose direct (crossing) path was evaluated.</summary>
    public required WaveguideConnection NewConnection { get; init; }

    /// <summary>The already-routed connection that the direct path intersects.</summary>
    public required WaveguideConnection ExistingConnection { get; init; }

    /// <summary>The direct path of <see cref="NewConnection"/> that crosses the existing waveguide.</summary>
    public required RoutedPath DirectPath { get; init; }

    /// <summary>Intersection point of the two waveguides in micrometers.</summary>
    public required (double X, double Y) IntersectionPoint { get; init; }

    /// <summary>True when <see cref="NewConnection"/> runs horizontally at the intersection.</summary>
    public required bool NewConnectionIsHorizontal { get; init; }

    /// <summary>Unit travel direction of the new connection at the intersection.</summary>
    public required (double X, double Y) NewDirection { get; init; }

    /// <summary>Unit travel direction of the existing connection at the intersection.</summary>
    public required (double X, double Y) ExistingDirection { get; init; }

    /// <summary>
    /// Estimated insertion loss (dB) of the crossing variant:
    /// direct-path propagation + bend loss + crossing through-loss.
    /// </summary>
    public required double CrossingVariantLossDb { get; init; }
}

/// <summary>
/// Bookkeeping for one inserted crossing: the physical crossing component,
/// the two original (unsplit) connections and the four sub-connections that
/// replaced them. Enables clean dissolution when a crossed net is removed.
/// </summary>
public class CrossingRecord
{
    /// <summary>The placed ebeam_crossing4 (or compatible) component instance.</summary>
    public required Component CrossingComponent { get; init; }

    /// <summary>Original connection A before splitting (the re-routed one).</summary>
    public required WaveguideConnection OriginalA { get; init; }

    /// <summary>Original connection B before splitting (the pre-existing one).</summary>
    public required WaveguideConnection OriginalB { get; init; }

    /// <summary>The two sub-connections that replaced <see cref="OriginalA"/>.</summary>
    public required List<WaveguideConnection> SubConnectionsA { get; init; }

    /// <summary>The two sub-connections that replaced <see cref="OriginalB"/>.</summary>
    public required List<WaveguideConnection> SubConnectionsB { get; init; }

    /// <summary>All four sub-connections of this crossing.</summary>
    public IEnumerable<WaveguideConnection> AllSubConnections =>
        SubConnectionsA.Concat(SubConnectionsB);

    /// <summary>Checks whether the given connection is one of this record's sub-connections.</summary>
    public bool ContainsSubConnection(WaveguideConnection connection) =>
        SubConnectionsA.Contains(connection) || SubConnectionsB.Contains(connection);

    /// <summary>
    /// Returns the original connection the given sub-connection belongs to,
    /// or null when it is not part of this record.
    /// </summary>
    public WaveguideConnection? GetOriginalFor(WaveguideConnection subConnection)
    {
        if (SubConnectionsA.Contains(subConnection)) return OriginalA;
        if (SubConnectionsB.Contains(subConnection)) return OriginalB;
        return null;
    }
}
