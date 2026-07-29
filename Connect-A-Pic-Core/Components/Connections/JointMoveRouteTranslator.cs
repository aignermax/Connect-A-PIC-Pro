using CAP_Core.Routing;

namespace CAP_Core.Components.Connections;

/// <summary>
/// Detects that BOTH endpoints of a routed connection moved by the same delta — the user
/// dragged both connected components together — and translates the existing route to the
/// new pin positions instead of letting it be re-routed. A joint move does not change the
/// relative constellation of the pins, so a pure translation preserves the exact geometry,
/// including manually edited bend radii and segment shifts.
/// </summary>
public static class JointMoveRouteTranslator
{
    /// <summary>
    /// Maximum difference in micrometers between the start-pin delta and the end-pin delta
    /// for the move to count as one uniform translation. Matches the endpoint tolerance
    /// used by <see cref="WaveguideConnection.FrozenPathStillMatchesPins"/>.
    /// </summary>
    public const double DeltaMatchToleranceMicrometers =
        WaveguideConnection.FrozenEndpointToleranceMicrometers;

    /// <summary>
    /// Translates the connection's routed path so its endpoints match the current pin
    /// positions, when — and only when — both pins moved by the same delta. Returns false
    /// (leaving the path untouched) when the endpoints already match, moved by different
    /// deltas (only one component moved, or a component was resized), or a pin's direction
    /// changed (a rotation can coincidentally produce equal deltas). Collision checks are
    /// the caller's responsibility: the translated path may overlap a third component and
    /// must still go through the normal validity pipeline.
    /// </summary>
    /// <param name="connection">The connection whose route may be translated in place.</param>
    /// <returns>True when the path was replaced by a translated copy matching the pins.</returns>
    public static bool TryTranslateToPins(WaveguideConnection connection)
    {
        var path = connection.RoutedPath;
        if (path == null || path.Segments.Count == 0 ||
            connection.StartPin == null || connection.EndPin == null)
            return false;
        if (path.IsBlockedFallback || path.IsInvalidGeometry || path.IsPlaceholderGeometry)
            return false;

        var (startX, startY) = connection.StartPin.GetAbsolutePosition();
        var (endX, endY) = connection.EndPin.GetAbsolutePosition();
        var first = path.Segments[0];
        var last = path.Segments[^1];

        double startDx = startX - first.StartPoint.X;
        double startDy = startY - first.StartPoint.Y;
        double endDx = endX - last.EndPoint.X;
        double endDy = endY - last.EndPoint.Y;

        // Endpoints already match: nothing moved, keep the live path untouched.
        if (Length(startDx, startDy) <= DeltaMatchToleranceMicrometers &&
            Length(endDx, endDy) <= DeltaMatchToleranceMicrometers)
            return false;

        // Different deltas: only one component moved (or one was resized) — genuine re-route.
        if (Length(startDx - endDx, startDy - endDy) > DeltaMatchToleranceMicrometers)
            return false;

        // A rotation can coincidentally move both pins by the same delta; the pins'
        // directions then no longer match the path's launch/entry directions.
        var (startDirectionOk, endDirectionOk) = CachedRouteValidator.CheckPinDirections(
            connection.StartPin, connection.EndPin, path);
        if (!startDirectionOk || !endDirectionOk)
            return false;

        // Publish the finished copy through the single reference assignment so the UI
        // thread never observes half-translated segments (see ReplaceRoutedPath).
        connection.ReplaceRoutedPath(path.TranslatedCopy(startDx, startDy));
        return true;
    }

    private static double Length(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
}
