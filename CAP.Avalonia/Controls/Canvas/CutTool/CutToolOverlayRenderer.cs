using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Controls.Canvas.CutTool;

/// <summary>
/// Draws the Cut tool overlay (issue #798) in the world transform: dashed guide lines
/// extending from visible pins along their facing axis, and circular markers at each
/// crossing-insertion candidate. The hovered candidate is drawn larger and filled so
/// the click target is unmistakable. Sizes are divided by the zoom so they stay
/// screen-constant. Also triggers the per-frame candidate recomputation.
/// </summary>
public sealed class CutToolOverlayRenderer : ICanvasRenderer
{
    private const double GuideLineWidthPx = 1.0;
    private const double GuideDashPx = 4.0;
    private const double CandidateRadiusPx = 5.0;
    private const double HoveredRadiusPx = 8.0;
    private const double MarkerStrokePx = 1.5;

    private static readonly Color GuideColor = Color.FromArgb(140, 255, 200, 60);
    private static readonly IBrush CandidateFill = new SolidColorBrush(Color.FromArgb(90, 255, 200, 60));
    private static readonly IBrush HoveredFill = new SolidColorBrush(Color.FromRgb(255, 200, 60));
    private static readonly IBrush MarkerStrokeBrush = new SolidColorBrush(Color.FromRgb(255, 230, 150));

    private readonly CutToolCandidateComputer _computer = new();

    /// <inheritdoc/>
    public void Render(DrawingContext context, CanvasRenderContext rc)
    {
        var mainVm = rc.MainViewModel;
        var state = rc.InteractionState;
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Cut)
        {
            if (state.CutCandidates.Count > 0 || state.CutGuideLines.Count > 0)
                state.ResetCutTool();
            return;
        }

        double zoom = rc.Zoom <= 0 ? 1.0 : rc.Zoom;
        var viewport = ComputeViewportWorld(rc, zoom);
        _computer.Update(state, rc.ViewModel, mainVm, viewport);

        DrawGuideLines(context, state.CutGuideLines, viewport, zoom);
        DrawCandidates(context, state, zoom);
    }

    /// <summary>Visible canvas area in world (micrometer) coordinates.</summary>
    private static Rect ComputeViewportWorld(CanvasRenderContext rc, double zoom)
    {
        var vm = rc.ViewModel;
        return new Rect(-vm.PanX / zoom, -vm.PanY / zoom,
                        rc.Bounds.Width / zoom, rc.Bounds.Height / zoom);
    }

    private static void DrawGuideLines(DrawingContext context,
        IReadOnlyList<PinGuideLine> guides, Rect viewport, double zoom)
    {
        var pen = new Pen(new SolidColorBrush(GuideColor), GuideLineWidthPx / zoom)
        {
            DashStyle = new DashStyle(new[] { GuideDashPx, GuideDashPx }, 0),
        };

        // Long enough to cross the whole viewport from any visible origin.
        double reach = viewport.Width + viewport.Height;
        foreach (var guide in guides)
        {
            var start = new Point(guide.Origin.X, guide.Origin.Y);
            var end = new Point(guide.Origin.X + guide.Direction.X * reach,
                                guide.Origin.Y + guide.Direction.Y * reach);
            context.DrawLine(pen, start, end);
        }
    }

    private static void DrawCandidates(DrawingContext context, CanvasInteractionState state, double zoom)
    {
        var stroke = new Pen(MarkerStrokeBrush, MarkerStrokePx / zoom);
        foreach (var candidate in state.CutCandidates)
        {
            bool hovered = candidate == state.HoveredCutCandidate;
            double radius = (hovered ? HoveredRadiusPx : CandidateRadiusPx) / zoom;
            var center = new Point(candidate.IntersectionPoint.X, candidate.IntersectionPoint.Y);
            context.DrawEllipse(hovered ? HoveredFill : CandidateFill, stroke, center, radius, radius);
        }
    }
}
