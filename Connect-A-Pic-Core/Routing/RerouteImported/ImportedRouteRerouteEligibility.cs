using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.RerouteImported;

/// <summary>
/// Decides which connections the "Re-route imported routes" action may hand back
/// to the live router. Imported GDS routes arrive as frozen cached routes with
/// the default Auto style; anything the user shaped deliberately — an explicit
/// routing style, manual bend/segment edits, a locked connection — is never
/// re-routed silently.
/// </summary>
public static class ImportedRouteRerouteEligibility
{
    /// <summary>
    /// True when <paramref name="connection"/> is a frozen, unedited optical route
    /// that a re-route pass may replace with a fresh A* route: frozen with actual
    /// geometry, Auto style (an explicit style is the user's choice and is frozen
    /// by design), not electrical (metal traces are out of scope), not locked, and
    /// without manual bend/segment edits (hand-edited geometry is sacred).
    /// </summary>
    public static bool IsEligible(WaveguideConnection connection) =>
        connection.IsRouteFrozen
        && !connection.IsElectrical
        && !connection.IsLocked
        && connection.Type == WaveguideType.Auto
        && !connection.HasManualPathEdits
        && connection.RoutedPath is { Segments.Count: > 0 };

    /// <summary>
    /// True when <paramref name="connection"/> is a frozen optical route that is
    /// EXCLUDED from re-routing only because it carries manual edits — surfaced in
    /// the UI so the user sees why those routes are kept unchanged.
    /// </summary>
    public static bool IsKeptHandEdited(WaveguideConnection connection) =>
        connection.IsRouteFrozen
        && !connection.IsElectrical
        && connection.Type == WaveguideType.Auto
        && connection.HasManualPathEdits;

    /// <summary>
    /// True when <paramref name="path"/> is a group-internal frozen route that would
    /// be re-routable if it lived on the canvas: frozen with actual geometry, Auto
    /// style, both pins present (pin-less GDS outlines never re-expand into live
    /// connections), and no manual bend/segment edits. The standard GDS import
    /// groups everything it placed, so these paths are counted and surfaced with a
    /// hint to open or dissolve the group before re-routing.
    /// </summary>
    public static bool IsEligibleGroupInternal(FrozenWaveguidePath path) =>
        path.IsRouteFrozen
        && path.ConnectionType == WaveguideType.Auto
        && path.StartPin is not null
        && path.EndPin is not null
        && path.Path is { Segments.Count: > 0 }
        && path.BendRadiusOverrides.Count == 0
        && path.StraightShiftOffsets.Count == 0;

    /// <summary>
    /// Counts the re-routable frozen paths inside <paramref name="group"/>,
    /// including nested child groups.
    /// </summary>
    public static int CountEligibleGroupInternal(ComponentGroup group)
    {
        var count = group.InternalPaths.Count(IsEligibleGroupInternal);
        foreach (var childGroup in group.ChildComponents.OfType<ComponentGroup>())
            count += CountEligibleGroupInternal(childGroup);
        return count;
    }
}
