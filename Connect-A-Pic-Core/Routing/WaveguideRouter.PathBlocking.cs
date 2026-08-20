using CAP_Core.Components.Core;

namespace CAP_Core.Routing;

/// <summary>
/// Path-blocking checks against specific obstacle classes in the pathfinding grid.
/// </summary>
public partial class WaveguideRouter
{
    /// <summary>
    /// Checks if any segment in a path passes through cells blocked strictly by component
    /// geometry (cell state 1), ignoring frozen group path markings (state 3) and waveguide
    /// obstacles. Used for destructive verdicts — unfreezing a manually edited AUTO route
    /// discards the user's bend edits, so it must not be triggered by a group path marking:
    /// while a group is being ungrouped, that marking is a ghost of the very connection
    /// being judged (a stale in-flight routing pass can hold a grid rebuilt from the
    /// already-removed group).
    /// </summary>
    public bool IsPathBlockedByComponentOnly(IEnumerable<PathSegment> segments)
    {
        if (PathfindingGrid == null) return false;
        return IsPathBlocked(segments, PathfindingGrid.IsBlockedByComponentOnly);
    }

    /// <summary>
    /// Same verdict as <see cref="IsPathBlockedByComponentOnly(IEnumerable{PathSegment})"/>,
    /// but with the own-pin tolerance: cells inside the pin corridors of
    /// <paramref name="ownPins"/> that only a foreign component's padding band reaches
    /// across do not count as blocked — a route may hug its own pin. A foreign component
    /// body inside the corridor still blocks.
    /// </summary>
    public bool IsPathBlockedByComponentOnly(
        IEnumerable<PathSegment> segments, IReadOnlyCollection<PhysicalPin> ownPins)
    {
        if (PathfindingGrid == null) return false;
        var corridors = PathfindingGrid.GetPinCorridorCells(ownPins);
        return IsPathBlocked(segments,
            (gx, gy) => PathfindingGrid.IsBlockedByComponentOnly(gx, gy, corridors));
    }

    /// <summary>
    /// Same verdict as <see cref="IsPathBlockedByComponents(IEnumerable{PathSegment})"/>
    /// (component geometry plus frozen group path markings), with the own-pin tolerance
    /// described at
    /// <see cref="IsPathBlockedByComponentOnly(IEnumerable{PathSegment}, IReadOnlyCollection{PhysicalPin})"/>.
    /// </summary>
    public bool IsPathBlockedByComponents(
        IEnumerable<PathSegment> segments, IReadOnlyCollection<PhysicalPin> ownPins)
    {
        if (PathfindingGrid == null) return false;
        var corridors = PathfindingGrid.GetPinCorridorCells(ownPins);
        return IsPathBlocked(segments,
            (gx, gy) => PathfindingGrid.IsBlockedByComponent(gx, gy, corridors));
    }
}
