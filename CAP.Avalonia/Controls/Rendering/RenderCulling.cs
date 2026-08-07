using System.Runtime.CompilerServices;
using Avalonia;
using CAP_Core.Components.Core;
using CAP_Core.Routing;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Viewport-culling and level-of-detail helpers shared by the canvas renderers.
/// All rectangles are in world (µm) coordinates; thresholds are expressed in screen
/// pixels and converted through the current zoom, so behaviour is zoom-independent
/// and visually unchanged for normal-sized designs at typical zoom.
/// </summary>
internal static class RenderCulling
{
    /// <summary>
    /// Margin added around the visible viewport before culling, in screen pixels.
    /// Covers partially visible items plus screen-capped adornments that extend past
    /// an item's bounding box (pin glyphs, direction stubs, group name labels).
    /// </summary>
    internal const double CullMarginScreenPx = 40.0;

    /// <summary>
    /// On-screen size (larger bounding-box dimension × zoom) below which a component
    /// is drawn body-only: pins, name labels, lock icons and laser indicators are
    /// sub-pixel noise at that scale and only cost frame time on huge imported designs.
    /// </summary>
    internal const double LodMinScreenPixels = 4.0;

    /// <summary>
    /// On-screen size (larger bounding-box dimension × zoom) below which an outline
    /// polygon is not drawn at all (KLayout-style geometry LOD). Far tighter than
    /// <see cref="LodMinScreenPixels"/>: this culls real geometry, not decorations,
    /// so it must only drop what rasterizes as an anti-aliased speck.
    /// </summary>
    internal const double OutlineMinScreenPixels = 1.5;

    private static readonly ConditionalWeakTable<FrozenWaveguidePath, CachedPathBounds> PathBoundsCache = new();

    /// <summary>Visible canvas area in world (µm) coordinates from pan/zoom and the
    /// screen-space canvas bounds.</summary>
    internal static Rect ComputeViewportWorld(double panX, double panY, Rect bounds, double zoom)
    {
        double safeZoom = SafeZoom(zoom);
        return new Rect(-panX / safeZoom, -panY / safeZoom,
                        bounds.Width / safeZoom, bounds.Height / safeZoom);
    }

    /// <summary>Grows the world-space viewport by the culling margin.</summary>
    internal static Rect InflateForCulling(Rect viewportWorld, double zoom) =>
        viewportWorld.Inflate(CullMarginScreenPx / SafeZoom(zoom));

    /// <summary>
    /// True when the component's larger dimension is smaller on screen than
    /// <see cref="LodMinScreenPixels"/>, i.e. its decorations should be skipped.
    /// </summary>
    internal static bool IsBelowLodThreshold(double widthWorld, double heightWorld, double zoom) =>
        Math.Max(widthWorld, heightWorld) * SafeZoom(zoom) < LodMinScreenPixels;

    /// <summary>
    /// True when the polygon's larger bounding-box dimension is smaller on screen than
    /// <see cref="OutlineMinScreenPixels"/>, i.e. drawing it costs more than the speck
    /// it produces. Callers pass the polygon's local-frame bounds: the outline draw
    /// transform is rigid (translation + rotation), so the local bbox × zoom is a
    /// conservative on-screen size that can never cull a polygon that would be visible.
    /// </summary>
    internal static bool IsBelowOutlineLodThreshold(double widthWorld, double heightWorld, double zoom) =>
        Math.Max(widthWorld, heightWorld) * SafeZoom(zoom) < OutlineMinScreenPixels;

    /// <summary>
    /// World-space bounding box of a frozen path, or <c>null</c> when the path has no
    /// segments. Cached per path instance because group moves translate the segments
    /// in place (<see cref="FrozenWaveguidePath.TranslateBy"/>) rather than replacing
    /// the path object: the first segment's start point is stored as an anchor and any
    /// change to it triggers a recompute, so the cache can never serve stale bounds.
    /// </summary>
    internal static Rect? GetFrozenPathBounds(FrozenWaveguidePath frozenPath)
    {
        var segments = frozenPath?.Path?.Segments;
        if (segments == null || segments.Count == 0)
            return null;

        var cached = PathBoundsCache.GetOrCreateValue(frozenPath!);
        var anchor = segments[0].StartPoint;
        if (!cached.HasValue || cached.Anchor != anchor)
        {
            cached.Bounds = ComputeSegmentBounds(segments);
            cached.Anchor = anchor;
            cached.HasValue = true;
        }
        return cached.Bounds;
    }

    /// <summary>
    /// Union of all segment extents. A bend contributes its full circle (centre ±
    /// radius) — a cheap conservative superset of the true arc box, so culling can
    /// never skip a visible arc.
    /// </summary>
    internal static Rect ComputeSegmentBounds(IReadOnlyList<PathSegment> segments)
    {
        var bounds = ComputeSegmentBounds(segments[0]);
        for (int i = 1; i < segments.Count; i++)
            bounds = bounds.Union(ComputeSegmentBounds(segments[i]));
        return bounds;
    }

    /// <summary>
    /// Conservative world-space bounds of one segment — the same union of endpoint
    /// box and bend circle the list overload builds, for per-segment culling in the
    /// render loop (no intermediate list allocation).
    /// </summary>
    internal static Rect ComputeSegmentBounds(PathSegment segment)
    {
        double minX = Math.Min(segment.StartPoint.X, segment.EndPoint.X);
        double minY = Math.Min(segment.StartPoint.Y, segment.EndPoint.Y);
        double maxX = Math.Max(segment.StartPoint.X, segment.EndPoint.X);
        double maxY = Math.Max(segment.StartPoint.Y, segment.EndPoint.Y);
        if (segment is BendSegment bend)
        {
            minX = Math.Min(minX, bend.Center.X - bend.RadiusMicrometers);
            minY = Math.Min(minY, bend.Center.Y - bend.RadiusMicrometers);
            maxX = Math.Max(maxX, bend.Center.X + bend.RadiusMicrometers);
            maxY = Math.Max(maxY, bend.Center.Y + bend.RadiusMicrometers);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static double SafeZoom(double zoom) => zoom <= 0 ? 1.0 : zoom;

    /// <summary>Mutable cache slot for one frozen path's bounds plus its anchor point.</summary>
    private sealed class CachedPathBounds
    {
        public bool HasValue;
        public (double X, double Y) Anchor;
        public Rect Bounds;
    }
}
