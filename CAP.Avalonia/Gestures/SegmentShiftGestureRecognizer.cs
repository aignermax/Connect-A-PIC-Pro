using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.PinKinds;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Parallel-shift editing of straight waveguide segments (issue #791): grabs the midpoint
/// handle of a straight segment on the selected connection and drags it along the segment
/// normal — movement along the segment is ignored, so the segment moves parallel to itself
/// with the adjoining bends re-fitted live. Registered right after the bend-radius recognizer
/// so a bend handle grab wins when the two handles overlap. The whole drag is committed as one
/// <see cref="SegmentShiftCommand"/> on release so Ctrl+Z reverts it exactly; a drag past a
/// segment-collapse limit keeps the last valid geometry and paints the handle red. On drop the
/// router's component collision check is re-run and flagged via the design-issue pipeline.
/// </summary>
public sealed class SegmentShiftGestureRecognizer : IGestureRecognizer
{
    private const double GrabRadiusPx = 8.0;
    private const double MeaningfulChangeMicrometers = 1e-3;

    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;
    private readonly Func<double> _getZoom;

    private WaveguideConnectionViewModel? _connection;
    private StraightSegmentHandle? _handle;
    private double _offsetAtPress;
    private double _lastValidOffset;

    /// <summary>Initializes a new instance of <see cref="SegmentShiftGestureRecognizer"/>.</summary>
    /// <param name="state">Shared interaction state carrying the active-handle highlight fields.</param>
    /// <param name="invalidate">Requests a canvas repaint.</param>
    /// <param name="getZoom">Current canvas zoom, used to keep the grab radius screen-constant.</param>
    public SegmentShiftGestureRecognizer(CanvasInteractionState state, Action invalidate, Func<double> getZoom)
    {
        _state = state;
        _invalidate = invalidate;
        _getZoom = getZoom;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm == null || mainVm.CanvasInteraction.CurrentMode != InteractionMode.Select) return false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;

        var selected = mainVm.CanvasInteraction.SelectedWaveguideConnection;
        if (selected == null || !IsOptical(selected)) return false;

        double grab = GrabRadiusPx / Zoom();
        foreach (var handle in SegmentShiftGeometry.GetHandles(selected.Connection.GetPathSegments()))
        {
            if (Distance(canvasPoint.X, canvasPoint.Y, handle.Midpoint.X, handle.Midpoint.Y) <= grab)
            {
                Capture(selected, handle);
                return true;
            }
        }
        return false;
    }

    private void Capture(WaveguideConnectionViewModel connection, StraightSegmentHandle handle)
    {
        _connection = connection;
        _handle = handle;
        _offsetAtPress = connection.Connection.StraightShiftOffsets
            .TryGetValue(handle.StraightIndex, out double stored) ? stored : 0.0;
        _lastValidOffset = _offsetAtPress;
        _state.ActiveShiftStraightIndex = handle.StraightIndex;
        _state.ActiveShiftClamped = false;
        _state.ActiveShiftDeltaMicrometers = 0.0;
        _invalidate();
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        // Hover highlighting is owned by HoverHighlightGestureRecognizer; nothing to do here.
    }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_connection == null || _handle == null) return;

        // Perpendicular constraint: only the pointer's projection onto the segment normal
        // counts; the along-segment component of the drag is discarded.
        double targetOffset = _offsetAtPress
            + SegmentShiftGeometry.ProjectOffset(_handle, (canvasPoint.X, canvasPoint.Y));

        if (SegmentShiftEditor.TryApplyShift(_connection.Connection, _handle.StraightIndex, targetOffset, out _))
        {
            _lastValidOffset = targetOffset;
            _state.ActiveShiftClamped = false;
            _connection.NotifyPathChanged();
        }
        else
        {
            // Requested shift rejected (a segment would collapse): keep the last valid
            // geometry and let the renderer paint the handle red.
            _state.ActiveShiftClamped = true;
        }
        _state.ActiveShiftDeltaMicrometers = _lastValidOffset - _offsetAtPress;
        _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_connection == null || _handle == null) return;

        if (mainVm != null && Math.Abs(_lastValidOffset - _offsetAtPress) > MeaningfulChangeMicrometers)
        {
            // Collision honesty: after every apply (drop, undo, redo) re-run the router's
            // component collision check so a shift into an obstacle is flagged, not silent.
            var connection = _connection;
            void AfterApply()
            {
                SegmentShiftEditor.RefreshComponentCollision(connection.Connection, canvas.Router);
                _invalidate();
            }
            mainVm.CommandManager.ExecuteCommand(
                new SegmentShiftCommand(_connection, _handle.StraightIndex,
                                        _offsetAtPress, _lastValidOffset, AfterApply));
        }
        ResetDrag();
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        if (_connection != null && _handle != null)
        {
            SegmentShiftEditor.TryApplyShift(_connection.Connection, _handle.StraightIndex, _offsetAtPress, out _);
            _connection.NotifyPathChanged();
        }
        ResetDrag();
    }

    private void ResetDrag()
    {
        _state.ActiveShiftStraightIndex = -1;
        _state.ActiveShiftClamped = false;
        _state.ActiveShiftDeltaMicrometers = 0.0;
        _connection = null;
        _handle = null;
        _invalidate();
    }

    private static bool IsOptical(WaveguideConnectionViewModel conn) =>
        !(PinKindHelper.IsElectrical(conn.Connection.StartPin)
          && PinKindHelper.IsElectrical(conn.Connection.EndPin));

    private double Zoom()
    {
        double zoom = _getZoom();
        return zoom <= 0 ? 1.0 : zoom;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
