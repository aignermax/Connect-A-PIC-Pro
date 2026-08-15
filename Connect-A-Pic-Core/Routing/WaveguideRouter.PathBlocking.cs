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
}
