using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// ViewModel for viewport control (zoom, pan, navigation).
/// Handles all canvas viewport manipulation and navigation.
/// Max 250 lines per CLAUDE.md guideline.
/// </summary>
public partial class ViewportControlViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    /// <summary>
    /// Callback to update status text in the UI.
    /// </summary>
    public Action<string>? UpdateStatus { get; set; }

    /// <summary>
    /// Callback to get the current canvas viewport size (width, height) in screen pixels.
    /// Set by the View code-behind (MainWindow) after initialization.
    /// </summary>
    public Func<(double width, double height)>? GetViewportSize { get; set; }

    public ViewportControlViewModel(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
    }

    [RelayCommand]
    private void ZoomIn()
    {
        ZoomLevel = Math.Min(ZoomLevel * 1.2, CanvasZoomLimits.Max);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ZoomLevel = Math.Max(ZoomLevel / 1.2, CanvasZoomLimits.Min);
    }

    [RelayCommand]
    private void ResetZoom()
    {
        ZoomLevel = 1.0;
    }

    [RelayCommand]
    private void ResetPan()
    {
        _canvas.PanX = 0;
        _canvas.PanY = 0;
    }

    /// <summary>
    /// Pans the canvas so the given coordinate (in micrometers) is centered in view.
    /// Preserves the current zoom level.
    /// </summary>
    /// <param name="centerX">X coordinate in micrometers to center on.</param>
    /// <param name="centerY">Y coordinate in micrometers to center on.</param>
    public void NavigateCanvasTo(double centerX, double centerY)
    {
        var (vpWidth, vpHeight) = GetViewportSize?.Invoke() ?? (900, 800);

        _canvas.PanX = vpWidth / 2 - centerX * ZoomLevel;
        _canvas.PanY = vpHeight / 2 - centerY * ZoomLevel;
    }

    /// <summary>
    /// Adjusts zoom and pan to fit all components in the viewport.
    /// Applies 10% padding around the design. Does nothing on empty canvas.
    /// </summary>
    /// <param name="viewportWidth">Viewport width in screen pixels.</param>
    /// <param name="viewportHeight">Viewport height in screen pixels.</param>
    public void ZoomToFit(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        var bounds = BoundingBoxCalculator.Calculate(_canvas.Components);
        if (bounds == null)
        {
            UpdateStatus?.Invoke("No components to fit");
            return;
        }

        var padded = BoundingBoxCalculator.WithPadding(
            bounds.Value, BoundingBoxCalculator.DefaultPaddingFraction);

        if (padded.IsEmpty) return;

        var (zoom, panX, panY) = BoundingBoxCalculator.CalculateZoomToFit(
            padded, viewportWidth, viewportHeight);

        ZoomLevel = zoom;
        _canvas.PanX = panX;
        _canvas.PanY = panY;
        UpdateStatus?.Invoke($"Zoom to fit: {zoom:P0}");
    }
}
