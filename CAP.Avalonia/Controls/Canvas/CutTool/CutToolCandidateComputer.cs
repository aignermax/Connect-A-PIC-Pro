using Avalonia;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing.CrossingInsertion;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Controls.Canvas.CutTool;

/// <summary>
/// Recomputes the Cut tool's guide lines and insertion candidates for the
/// current viewport and writes them into <see cref="CanvasInteractionState"/>, where the
/// overlay renderer and the gesture recognizer read them. Guide lines originate only from
/// pins of viewport-visible components, connections with no segment in view are skipped
/// entirely, and candidates outside the viewport are dropped, so a large design never floods
/// the screen with far-away markers or wastes time on off-screen geometry. The (expensive)
/// guide/candidate sweep itself only reruns when the viewport or design content actually
/// changed since the last call — a content-signature dirty flag, since <see cref="Update"/>
/// runs every render frame while Cut mode is active, not only on pointer move.
/// </summary>
public sealed class CutToolCandidateComputer
{
    /// <summary>Extra straight run beyond the crossing half-extent so port stubs dock cleanly (µm).</summary>
    public const double StubClearanceMicrometers = 1.0;

    private static readonly CrossingInserter PortValidator = new();

    private readonly ManualCrossingCandidateFinder _finder = new();
    private int? _lastSignature;

    /// <summary>
    /// Refreshes <see cref="CanvasInteractionState.CutGuideLines"/> and
    /// <see cref="CanvasInteractionState.CutCandidates"/>. Clears both when no usable PDK
    /// crossing template is loaded (the tool then has nothing it could insert).
    /// </summary>
    /// <param name="viewportWorld">Visible canvas area in world (micrometer) coordinates.</param>
    public void Update(CanvasInteractionState state, DesignCanvasViewModel canvas,
                       MainViewModel mainVm, Rect viewportWorld)
    {
        var template = FindUsableCrossingTemplate(mainVm.LeftPanel.AllTemplates);
        if (template == null)
        {
            state.ResetCutTool();
            _lastSignature = null;
            return;
        }

        int signature = ComputeContentSignature(canvas, viewportWorld);
        bool alreadyCurrent = _lastSignature == signature
            && (state.CutCandidates.Count > 0 || state.CutGuideLines.Count > 0);
        if (alreadyCurrent) return;
        _lastSignature = signature;

        double requiredRun = ComputeRequiredRunMicrometers(template);
        var visibleConnections = CollectViewportRelevantConnections(
            CollectEligibleConnections(canvas), viewportWorld).ToList();
        var footprint = BuildFootprintClearance(canvas.Router.PathfindingGrid, template);

        var guides = _finder.BuildGuideLines(CollectVisiblePins(canvas, viewportWorld));
        var candidates = _finder
            .FindCandidates(guides, visibleConnections, requiredRun, footprint)
            .Where(c => viewportWorld.Contains(new Point(c.IntersectionPoint.X, c.IntersectionPoint.Y)))
            .ToList();

        state.CutGuideLines = guides;
        state.CutCandidates = candidates;

        // A free-cut hover is never a member of the ambient guide-intersection list above (it
        // is computed live from the pointer position, see CutToolGestureRecognizer) — only a
        // stale SNAP hover that fell out of this frame's candidates is cleared here.
        if (state.HoveredCutCandidate is { IsFreeCut: false } hovered && !candidates.Contains(hovered))
            state.HoveredCutCandidate = null;
    }

    /// <summary>
    /// Finds the crossing template AND confirms it exposes all four wired ports the split
    /// needs (mirrors the adaptive pass's upfront <see cref="CrossingInserter.HasAllFourWiredPorts"/>
    /// guard). A template missing a port would otherwise pass every per-candidate geometry
    /// check and only fail once the user clicks, reporting success dishonestly.
    /// </summary>
    public static ComponentTemplate? FindUsableCrossingTemplate(IEnumerable<ComponentTemplate> templates)
    {
        var template = CrossingComponentInstance.FindCrossingTemplate(templates);
        if (template == null) return null;

        var probe = CrossingComponentInstance.CreateFromTemplates(templates);
        return probe != null && PortValidator.HasAllFourWiredPorts(probe.Component) ? template : null;
    }

    /// <summary>
    /// Straight run required on each side of a candidate point: half the crossing's larger
    /// footprint dimension plus stub clearance, so its ports dock cleanly. Shared by the
    /// guide-based and free-cut candidate paths.
    /// </summary>
    public static double ComputeRequiredRunMicrometers(ComponentTemplate template) =>
        Math.Max(template.WidthMicrometers, template.HeightMicrometers) / 2.0 + StubClearanceMicrometers;

    /// <summary>
    /// Grid clearance descriptor for the loaded crossing template, or null when the design
    /// has no live pathfinding grid yet (the bounding-box check is then skipped).
    /// </summary>
    public static FootprintClearance? BuildFootprintClearance(PathfindingGrid? grid, ComponentTemplate template)
    {
        if (grid == null) return null;
        double halfExtent = Math.Max(template.WidthMicrometers, template.HeightMicrometers) / 2.0;
        return new FootprintClearance(grid, halfExtent);
    }

    private static IEnumerable<PhysicalPin> CollectVisiblePins(
        DesignCanvasViewModel canvas, Rect viewportWorld)
    {
        return canvas.Components
            .Where(vm => viewportWorld.Intersects(new Rect(vm.X, vm.Y, vm.Width, vm.Height)))
            .SelectMany(vm => vm.Component.PhysicalPins);
    }

    /// <summary>
    /// Connections the tool may split: sub-connections of an active adaptive crossing are
    /// excluded structurally — a connection whose start or end pin docks on a component
    /// flagged <see cref="Component.IsInsertedCrossing"/> is never eligible, regardless of
    /// whether the adaptive service is currently running. Without this structural check,
    /// disabling the adaptive feature (or loading a design before its registry rebuilds)
    /// would offer the adaptive crossing's own stub connections as cuttable, and undoing that
    /// insertion would dissolve a crossing the Cut tool never created.
    /// Internal so <see cref="CAP.Avalonia.Gestures.CutToolGestureRecognizer"/> can reuse the
    /// exact same eligibility filter for the free-cut fallback.
    /// </summary>
    internal static IEnumerable<WaveguideConnection> CollectEligibleConnections(DesignCanvasViewModel canvas)
    {
        var crossing = canvas.ConnectionManager.CrossingInsertion;
        return canvas.Connections
            .Select(vm => vm.Connection)
            .Where(c => crossing == null || crossing.ResolveToOriginal(c) == c)
            .Where(c => !DocksOnInsertedCrossing(c));
    }

    private static bool DocksOnInsertedCrossing(WaveguideConnection connection) =>
        connection.StartPin.ParentComponent?.IsInsertedCrossing == true ||
        connection.EndPin.ParentComponent?.IsInsertedCrossing == true;

    /// <summary>Drops connections with no segment anywhere near the viewport, so the expensive
    /// guide/segment intersection sweep never runs against off-screen geometry.</summary>
    private static IEnumerable<WaveguideConnection> CollectViewportRelevantConnections(
        IEnumerable<WaveguideConnection> connections, Rect viewportWorld)
    {
        foreach (var connection in connections)
        {
            if (connection.IsElectrical) continue;
            if (connection.GetPathSegments().Any(s => SegmentBoundsIntersectViewport(s, viewportWorld)))
                yield return connection;
        }
    }

    private static bool SegmentBoundsIntersectViewport(PathSegment segment, Rect viewport)
    {
        double minX = Math.Min(segment.StartPoint.X, segment.EndPoint.X);
        double maxX = Math.Max(segment.StartPoint.X, segment.EndPoint.X);
        double minY = Math.Min(segment.StartPoint.Y, segment.EndPoint.Y);
        double maxY = Math.Max(segment.StartPoint.Y, segment.EndPoint.Y);
        return viewport.Intersects(new Rect(minX, minY, maxX - minX, maxY - minY));
    }

    /// <summary>
    /// Cheap fingerprint of everything that could change the ambient candidate list:
    /// viewport, component positions/count, and every connection's routed geometry.
    /// Recomputing this is asymptotically cheaper than the full guide×segment sweep it
    /// gates (no per-pin multiplication), so skipping the sweep on an unchanged frame is a
    /// net win even though the fingerprint itself touches the same geometry.
    /// </summary>
    private static int ComputeContentSignature(DesignCanvasViewModel canvas, Rect viewportWorld)
    {
        var hash = new HashCode();
        hash.Add(viewportWorld.X);
        hash.Add(viewportWorld.Y);
        hash.Add(viewportWorld.Width);
        hash.Add(viewportWorld.Height);
        hash.Add(canvas.Components.Count);
        foreach (var component in canvas.Components)
        {
            hash.Add(component.X);
            hash.Add(component.Y);
        }
        hash.Add(canvas.Connections.Count);
        foreach (var connectionVm in canvas.Connections)
        {
            foreach (var segment in connectionVm.Connection.GetPathSegments())
            {
                hash.Add(segment.StartPoint.X);
                hash.Add(segment.StartPoint.Y);
                hash.Add(segment.EndPoint.X);
                hash.Add(segment.EndPoint.Y);
            }
        }
        return hash.ToHashCode();
    }
}
