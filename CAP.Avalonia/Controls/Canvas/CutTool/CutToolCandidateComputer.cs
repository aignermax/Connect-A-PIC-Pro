using Avalonia;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP_Core.Components.Core;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Controls.Canvas.CutTool;

/// <summary>
/// Recomputes the Cut tool's guide lines and insertion candidates (issue #798) for the
/// current viewport and writes them into <see cref="CanvasInteractionState"/>, where the
/// overlay renderer and the gesture recognizer read them. Guide lines originate only from
/// pins of viewport-visible components, and candidates outside the viewport are dropped,
/// so a large design never floods the screen with far-away markers.
/// </summary>
public sealed class CutToolCandidateComputer
{
    /// <summary>Extra straight run beyond the crossing half-extent so port stubs dock cleanly (µm).</summary>
    public const double StubClearanceMicrometers = 1.0;

    private readonly ManualCrossingCandidateFinder _finder = new();

    /// <summary>
    /// Refreshes <see cref="CanvasInteractionState.CutGuideLines"/> and
    /// <see cref="CanvasInteractionState.CutCandidates"/>. Clears both when no PDK
    /// crossing template is loaded (the tool then has nothing it could insert).
    /// </summary>
    /// <param name="viewportWorld">Visible canvas area in world (micrometer) coordinates.</param>
    public void Update(CanvasInteractionState state, DesignCanvasViewModel canvas,
                       MainViewModel mainVm, Rect viewportWorld)
    {
        var template = CrossingComponentInstance.FindCrossingTemplate(mainVm.LeftPanel.AllTemplates);
        if (template == null)
        {
            state.ResetCutTool();
            return;
        }

        double requiredRun = Math.Max(template.WidthMicrometers, template.HeightMicrometers) / 2.0
                             + StubClearanceMicrometers;

        var guides = _finder.BuildGuideLines(CollectVisiblePins(canvas, viewportWorld));
        var candidates = _finder
            .FindCandidates(guides, CollectEligibleConnections(canvas), requiredRun)
            .Where(c => viewportWorld.Contains(new Point(c.IntersectionPoint.X, c.IntersectionPoint.Y)))
            .ToList();

        state.CutGuideLines = guides;
        state.CutCandidates = candidates;
        if (state.HoveredCutCandidate != null && !candidates.Contains(state.HoveredCutCandidate))
            state.HoveredCutCandidate = null;
    }

    private static IEnumerable<PhysicalPin> CollectVisiblePins(
        DesignCanvasViewModel canvas, Rect viewportWorld)
    {
        return canvas.Components
            .Where(vm => viewportWorld.Intersects(new Rect(vm.X, vm.Y, vm.Width, vm.Height)))
            .SelectMany(vm => vm.Component.PhysicalPins);
    }

    /// <summary>
    /// Connections the tool may split: sub-connections created by the adaptive crossing
    /// pass (#553) are skipped — they are transient routing artifacts that the next
    /// reroute may dissolve, so anchoring a manual crossing to them would be unstable.
    /// </summary>
    private static IEnumerable<CAP_Core.Components.Connections.WaveguideConnection>
        CollectEligibleConnections(DesignCanvasViewModel canvas)
    {
        var crossing = canvas.ConnectionManager.CrossingInsertion;
        return canvas.Connections
            .Select(vm => vm.Connection)
            .Where(c => crossing == null || crossing.ResolveToOriginal(c) == c);
    }
}
