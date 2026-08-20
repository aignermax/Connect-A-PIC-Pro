using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Selection;

/// <summary>
/// Rubber-band hit-testing for waveguide connections (issue #862): adds every optical connection
/// whose drawn path crosses the selection rectangle to
/// <see cref="SelectionManager.SelectedConnections"/>. Electrical connections are excluded —
/// metal traces carry no routing style (issue #682; revisit with curved metal, issue #854).
/// </summary>
public static class ConnectionBoxSelector
{
    /// <summary>
    /// Applies the box-selection result to <paramref name="selection"/>. Does NOT clear the
    /// existing selection — the component pass (<see cref="SelectionManager.SelectInRectangle"/>)
    /// runs first and already cleared it for a plain (non-Ctrl/Alt) drag.
    /// </summary>
    /// <param name="selection">The selection manager to mutate.</param>
    /// <param name="connections">All connections on the canvas.</param>
    /// <param name="rectMinX">Left edge of the selection rectangle.</param>
    /// <param name="rectMinY">Top edge of the selection rectangle.</param>
    /// <param name="rectMaxX">Right edge of the selection rectangle.</param>
    /// <param name="rectMaxY">Bottom edge of the selection rectangle.</param>
    /// <param name="removeFromSelection">If true, removes hits instead (Alt behavior).</param>
    public static void SelectInRectangle(
        SelectionManager selection,
        IEnumerable<WaveguideConnectionViewModel> connections,
        double rectMinX, double rectMinY,
        double rectMaxX, double rectMaxY,
        bool removeFromSelection = false)
    {
        foreach (var conn in connections)
        {
            if (conn.Connection.IsElectrical) continue;
            if (!PathIntersectsRectangle(conn, rectMinX, rectMinY, rectMaxX, rectMaxY)) continue;

            if (removeFromSelection)
            {
                if (selection.SelectedConnections.Remove(conn))
                    conn.IsSelected = false;
            }
            else if (!selection.SelectedConnections.Contains(conn))
            {
                selection.SelectedConnections.Add(conn);
                conn.IsSelected = true;
            }
        }
    }

    /// <summary>
    /// True when any routed segment (arcs approximated by their chord, like the click
    /// hit-test) crosses the rectangle; falls back to the straight endpoint line when the
    /// connection is not routed yet.
    /// </summary>
    public static bool PathIntersectsRectangle(
        WaveguideConnectionViewModel conn,
        double minX, double minY, double maxX, double maxY)
    {
        var segments = conn.Connection.GetPathSegments();
        if (segments.Count == 0)
            return SegmentIntersectsRectangle(conn.StartX, conn.StartY, conn.EndX, conn.EndY, minX, minY, maxX, maxY);

        foreach (var seg in segments)
        {
            if (SegmentIntersectsRectangle(
                    seg.StartPoint.X, seg.StartPoint.Y, seg.EndPoint.X, seg.EndPoint.Y,
                    minX, minY, maxX, maxY))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Line-segment vs. axis-aligned rectangle intersection via the slab (Liang-Barsky) test.
    /// </summary>
    private static bool SegmentIntersectsRectangle(
        double x1, double y1, double x2, double y2,
        double minX, double minY, double maxX, double maxY)
    {
        // Trivial accept: an endpoint inside the rectangle.
        if (IsInside(x1, y1, minX, minY, maxX, maxY) || IsInside(x2, y2, minX, minY, maxX, maxY))
            return true;

        double dx = x2 - x1;
        double dy = y2 - y1;
        double tMin = 0.0, tMax = 1.0;

        if (!ClipAxis(dx, minX - x1, maxX - x1, ref tMin, ref tMax)) return false;
        if (!ClipAxis(dy, minY - y1, maxY - y1, ref tMin, ref tMax)) return false;
        return tMin <= tMax;
    }

    private static bool IsInside(double x, double y, double minX, double minY, double maxX, double maxY)
        => x >= minX && x <= maxX && y >= minY && y <= maxY;

    /// <summary>One Liang-Barsky slab clip; false when the segment misses the slab entirely.</summary>
    private static bool ClipAxis(double delta, double distToMin, double distToMax, ref double tMin, ref double tMax)
    {
        const double Epsilon = 1e-12;
        if (Math.Abs(delta) < Epsilon)
            return distToMin <= 0 && distToMax >= 0; // Parallel: inside the slab or a miss.

        double t1 = distToMin / delta;
        double t2 = distToMax / delta;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
        return true;
    }
}
