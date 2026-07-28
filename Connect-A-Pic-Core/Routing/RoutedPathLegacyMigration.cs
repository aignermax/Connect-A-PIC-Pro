namespace CAP_Core.Routing;

/// <summary>
/// Infers <see cref="RoutedPath.IsPlaceholderGeometry"/> for routes saved before that flag
/// existed. <see cref="WaveguideRouter"/>'s degrade-to-blocked-fallback step replaces a
/// self-crossing route with a single straight line between the pins, flagged
/// <see cref="RoutedPath.IsBlockedFallback"/> — a shape a genuinely blocked (but real) fallback
/// or a crossing diagnostic essentially never takes, since both retain the router's actual
/// (typically multi-segment) geometry. A file saved between that step shipping and the
/// placeholder flag being introduced carries exactly this shape with no way to tell it apart
/// from the placeholder case, so it must be inferred on load rather than trusted as false.
/// </summary>
public static class RoutedPathLegacyMigration
{
    /// <summary>
    /// True when a loaded route's shape matches the router's placeholder replacement:
    /// blocked-fallback and reduced to exactly one straight segment.
    /// </summary>
    public static bool InferPlaceholderGeometry(bool isBlockedFallback, IReadOnlyList<PathSegment> segments) =>
        isBlockedFallback && segments.Count == 1 && segments[0] is StraightSegment;
}
