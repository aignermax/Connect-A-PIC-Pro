namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Read-only description of a single resizable bend, used to place an in-canvas
/// radius handle. All coordinates are in micrometers (world space).
/// </summary>
/// <param name="BendIndex">0-based index of this bend among the path's bend segments
/// (the same index accepted by <see cref="BendRadiusEditor.TryApplyOverride"/>).</param>
/// <param name="Corner">Intersection of the two adjacent tangent lines; stays fixed while
/// the radius changes.</param>
/// <param name="Bisector">Unit vector pointing from the corner toward the arc (and its
/// centre) — the direction the handle slides along.</param>
/// <param name="RadiusMicrometers">Current bend radius.</param>
/// <param name="HandleFactor">Factor <c>k</c> such that the arc's nearest point to the
/// corner sits at distance <c>Radius · k</c> along <see cref="Bisector"/>.</param>
public sealed record BendCorner(
    int BendIndex,
    (double X, double Y) Corner,
    (double X, double Y) Bisector,
    double RadiusMicrometers,
    double HandleFactor);
