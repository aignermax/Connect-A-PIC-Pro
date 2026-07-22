using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CAP.Avalonia.Views;

/// <summary>
/// Zoom/pan popup for a single rendered component-preview bitmap, opened from the "New
/// Component" thumbnail (see <see cref="NewComponentWindow"/>). Mousewheel zooms toward the
/// cursor and left-drag pans, mirroring <see cref="CAP.Avalonia.Controls.DesignCanvas"/>'s
/// zoom-at-cursor convention (factor 1.1/0.9 per notch, clamped 0.1-10).
/// </summary>
public partial class ComponentPreviewWindow : Window
{
    private const double ZoomInFactor = 1.1;
    private const double ZoomOutFactor = 0.9;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 10.0;

    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _translate = new();

    private Point? _lastPanPosition;
    private bool _isPanning;

    /// <summary>Initializes the popup with the bitmap to display.</summary>
    /// <param name="bitmap">The already-rendered preview bitmap to show, zoomed/panned in place.</param>
    public ComponentPreviewWindow(Bitmap bitmap)
    {
        InitializeComponent();
        PreviewImage.Source = bitmap;
        PreviewImage.RenderTransform = new TransformGroup { Children = { _scale, _translate } };
    }

    /// <summary>
    /// Zooms toward the cursor: the image point under the pointer stays fixed on screen while
    /// the scale changes, matching <c>DesignCanvas.OnPointerWheelChanged</c>'s convention.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var point = e.GetPosition(ViewportBorder);
        var factor = e.Delta.Y > 0 ? ZoomInFactor : ZoomOutFactor;
        var newScale = Math.Clamp(_scale.ScaleX * factor, MinZoom, MaxZoom);

        // Image-space point currently under the cursor (inverse of the render transform).
        var imageX = (point.X - _translate.X) / _scale.ScaleX;
        var imageY = (point.Y - _translate.Y) / _scale.ScaleY;

        _scale.ScaleX = newScale;
        _scale.ScaleY = newScale;

        // Re-derive the translate so that image point renders back under the cursor.
        _translate.X = point.X - (imageX * newScale);
        _translate.Y = point.Y - (imageY * newScale);

        e.Handled = true;
    }

    /// <summary>Starts a drag-pan on left-button press; double-click resets zoom/pan.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(ViewportBorder).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            ResetView();
            return;
        }

        _isPanning = true;
        _lastPanPosition = e.GetPosition(ViewportBorder);
        e.Pointer.Capture(ViewportBorder);
    }

    /// <summary>Applies the pointer delta to the pan translation while dragging.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isPanning || _lastPanPosition is not { } last)
        {
            return;
        }

        var current = e.GetPosition(ViewportBorder);
        _translate.X += current.X - last.X;
        _translate.Y += current.Y - last.Y;
        _lastPanPosition = current;
    }

    /// <summary>Ends the drag-pan gesture and releases pointer capture.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _isPanning = false;
        _lastPanPosition = null;
        e.Pointer.Capture(null);
    }

    /// <summary>Resets zoom to 1x and pan to the origin (double-click on the preview).</summary>
    private void ResetView()
    {
        _scale.ScaleX = 1.0;
        _scale.ScaleY = 1.0;
        _translate.X = 0.0;
        _translate.Y = 0.0;
    }
}
