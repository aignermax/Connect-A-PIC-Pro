using Avalonia;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Controls;

/// <summary>
/// Manages interaction state for the DesignCanvas (drag operations, previews, hover state).
/// Consolidates all drag-related fields and preview state in one place.
/// </summary>
public class CanvasInteractionState
{
    // Component drag state
    public ComponentViewModel? DraggingComponent { get; set; }
    public double DragStartX { get; set; }
    public double DragStartY { get; set; }
    public bool ShowDragPreview { get; set; }
    public Point DragPreviewPosition { get; set; }
    public bool DragPreviewValid { get; set; }

    // Group drag state
    public bool IsGroupDragging { get; set; }
    public double GroupDragStartCanvasX { get; set; }
    public double GroupDragStartCanvasY { get; set; }
    public Dictionary<ComponentViewModel, (double x, double y)> GroupDragStartPositions { get; } = new();

    // Connection drag state
    public PhysicalPin? ConnectionDragStartPin { get; set; }
    public Point ConnectionDragCurrentPoint { get; set; }

    // Component placement preview state
    public bool ShowPlacementPreview { get; set; }
    public ComponentTemplate? PlacementPreviewTemplate { get; set; }
    public Point PlacementPreviewPosition { get; set; }

    // Group template placement preview state
    public bool ShowGroupTemplatePlacementPreview { get; set; }
    public GroupTemplate? GroupTemplatePlacementPreview { get; set; }
    public Point GroupTemplatePlacementPreviewPosition { get; set; }

    // Panning state
    public bool IsPanning { get; set; }
    public bool HasPanned { get; set; }
    public Point LastPointerPosition { get; set; }

    // Power flow hover state
    public WaveguideConnectionViewModel? HoveredConnection { get; set; }
    public Point LastCanvasPosition { get; set; }

    // In-canvas bend-radius handle state (issue #574): index of the bend whose handle is being
    // dragged (-1 when none) and whether the last requested radius was rejected (clamped), so the
    // renderer can highlight the active handle and paint it red when the drag hits a limit.
    public int ActiveBendIndex { get; set; } = -1;
    public bool ActiveBendClamped { get; set; }

    // In-canvas segment parallel-shift handle state (issue #791): straight index of the segment
    // whose midpoint handle is being dragged (-1 when none), whether the last requested shift
    // was rejected (clamped), and the live shift delta (µm) shown next to the handle.
    public int ActiveShiftStraightIndex { get; set; } = -1;
    public bool ActiveShiftClamped { get; set; }
    public double ActiveShiftDeltaMicrometers { get; set; }

    // ComponentGroup hover state
    public ComponentGroup? HoveredGroup { get; set; }

    // ComponentGroup label hover state
    public ComponentGroup? HoveredGroupLabel { get; set; }

    // ComponentGroup lock icon hover state
    public ComponentGroup? HoveredGroupLockIcon { get; set; }

    // Laser on/off icon hover state (#690)
    public ComponentViewModel? HoveredLaserIconComponent { get; set; }

    // Cut tool state (issue #798): guide lines from visible pins, insertion candidates on
    // perpendicular waveguide segments, and the candidate currently under the pointer.
    public IReadOnlyList<PinGuideLine> CutGuideLines { get; set; } = Array.Empty<PinGuideLine>();
    public IReadOnlyList<ManualCrossingCandidate> CutCandidates { get; set; } = Array.Empty<ManualCrossingCandidate>();
    public ManualCrossingCandidate? HoveredCutCandidate { get; set; }

    // Double-click detection state
    public DateTime LastClickTime { get; set; } = DateTime.MinValue;
    public ComponentViewModel? LastClickedComponent { get; set; }
    public const int DoubleClickMilliseconds = 300;

    /// <summary>
    /// Resets all drag-related state.
    /// </summary>
    public void ResetDragState()
    {
        DraggingComponent = null;
        ShowDragPreview = false;
        IsGroupDragging = false;
        GroupDragStartPositions.Clear();
    }

    /// <summary>
    /// Resets connection drag state.
    /// </summary>
    public void ResetConnectionDrag()
    {
        ConnectionDragStartPin = null;
    }

    /// <summary>
    /// Resets placement preview state.
    /// </summary>
    public void ResetPlacementPreview()
    {
        ShowPlacementPreview = false;
        PlacementPreviewTemplate = null;
    }

    /// <summary>
    /// Resets group template placement preview state.
    /// </summary>
    public void ResetGroupTemplatePlacementPreview()
    {
        ShowGroupTemplatePlacementPreview = false;
        GroupTemplatePlacementPreview = null;
    }

    /// <summary>
    /// Resets all Cut-tool state (issue #798), e.g. when leaving Cut mode.
    /// </summary>
    public void ResetCutTool()
    {
        CutGuideLines = Array.Empty<PinGuideLine>();
        CutCandidates = Array.Empty<ManualCrossingCandidate>();
        HoveredCutCandidate = null;
    }
}
