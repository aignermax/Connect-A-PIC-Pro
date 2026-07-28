using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// Grid and crossing footprint needed to verify a Cut-tool candidate's bounding box is
/// actually free of other geometry — mirrors the adaptive pass's <see cref="CrossingInsertion.CrossingInserter"/>
/// bounding-box check. Pass null to skip the check (e.g. a call site with no live grid);
/// production candidate resolution always supplies one.
/// </summary>
/// <param name="Grid">The pathfinding grid tracking component and waveguide obstacles.</param>
/// <param name="HalfExtentMicrometers">Half the crossing's larger footprint dimension (µm).</param>
public readonly record struct FootprintClearance(PathfindingGrid Grid, double HalfExtentMicrometers);
