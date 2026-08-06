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

    private static readonly ConditionalWeakTable<FrozenWaveguidePath, CachedPathBounds> PathBoundsCache = new();

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
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var segment in segments)
        {
            Include(segment.StartPoint.X, segment.StartPoint.Y);
            Include(segment.EndPoint.X, segment.EndPoint.Y);
            if (segment is BendSegment bend)
            {
                Include(bend.Center.X - bend.RadiusMicrometers, bend.Center.Y - bend.RadiusMicrometers);
                Include(bend.Center.X + bend.RadiusMicrometers, bend.Center.Y + bend.RadiusMicrometers);
            }
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);

        void Include(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
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
