using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// Carries the design's active fabrication-process label for the canvas status HUD
/// (<c>CanvasOverlayRenderer</c>), so the process is visible in the grid overlay next to
/// zoom / component / connection counts instead of only at the bottom of the PDK panel.
/// Kept in sync by <c>MainViewModel.RefreshProcessIndicator</c>.
/// </summary>
public partial class DesignCanvasViewModel
{
    /// <summary>
    /// User-facing label of the active fabrication process (e.g. "Process: Demo SOI 220nm",
    /// "Playground — not manufacturable", or "No process selected"). Empty until set.
    /// </summary>
    [ObservableProperty]
    private string _activeProcessLabel = "";
}
