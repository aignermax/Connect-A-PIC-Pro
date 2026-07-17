using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.PinKinds;
using CAP_Core.Routing.InterconnectRouting;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Figma-style bend-radius editing (issue #574): grabs the radius handle of a bend on the
/// selected waveguide connection and drags it along the corner bisector to set the radius live.
/// Registered first so a handle grab wins over selection and component drag. Handles write the
/// radius directly onto the connection (there is no number panel); the whole drag is committed
/// as one <see cref="BendRadiusCommand"/> on release so Ctrl+Z reverts it exactly.
/// </summary>
public sealed class BendHandleGestureRecognizer : IGestureRecognizer
{
    private const double GrabRadiusPx = 8.0;
    private const double MeaningfulChangeMicrometers = 1e-3;

    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;
    private readonly Func<double> _getZoom;

    private WaveguideConnectionViewModel? _connection;
    private int _bendIndex;
    private (double X, double Y) _corner;
    private (double X, double Y) _bisector;
    private double _handleFactor;
    private double _radiusAtPress;
    private double _lastValidRadius;

    /// <summary>Initializes a new instance of <see cref="BendHandleGestureRecognizer"/>.</summary>
    /// <param name="state">Shared interaction state carrying the active-bend highlight fields.</param>
    /// <param name="invalidate">Requests a canvas repaint.</param>
    /// <param name="getZoom">Current canvas zoom, used to keep the grab radius screen-constant.</param>
    public BendHandleGestureRecognizer(CanvasInteractionState state, Action invalidate, Func<double> getZoom)
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
        foreach (var corner in BendRadiusEditor.GetBendCorners(selected.Connection.GetPathSegments()))
        {
            var (hx, hy) = BendHandleGeometry.HandlePoint(corner);
            if (Distance(canvasPoint.X, canvasPoint.Y, hx, hy) <= grab)
            {
                Capture(selected, corner);
                return true;
            }
        }
        return false;
    }

    private void Capture(WaveguideConnectionViewModel connection, BendCorner corner)
    {
        _connection = connection;
        _bendIndex = corner.BendIndex;
        _corner = corner.Corner;
        _bisector = corner.Bisector;
        _handleFactor = corner.HandleFactor;
        _radiusAtPress = corner.RadiusMicrometers;
        _lastValidRadius = corner.RadiusMicrometers;
        _state.ActiveBendIndex = corner.BendIndex;
        _state.ActiveBendClamped = false;
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
        if (_connection == null) return;

        double distance = BendHandleGeometry.ProjectDistance(_corner, _bisector, (canvasPoint.X, canvasPoint.Y));
        double newRadius = BendHandleGeometry.RadiusFromDistance(distance, _handleFactor);

        if (BendRadiusEditor.TryApplyOverride(_connection.Connection, _bendIndex, newRadius, out _))
        {
            _lastValidRadius = newRadius;
            _state.ActiveBendClamped = false;
            _connection.NotifyPathChanged();
        }
        else
        {
            // Requested radius rejected (too large / too small): keep the last valid geometry and
            // let the renderer paint the handle red.
            _state.ActiveBendClamped = true;
        }
        _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_connection == null) return;

        if (mainVm != null && Math.Abs(_lastValidRadius - _radiusAtPress) > MeaningfulChangeMicrometers)
        {
            mainVm.CommandManager.ExecuteCommand(
                new BendRadiusCommand(_connection, _bendIndex, _radiusAtPress, _lastValidRadius, _invalidate));
        }
        ResetDrag();
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        if (_connection != null)
        {
            BendRadiusEditor.TryApplyOverride(_connection.Connection, _bendIndex, _radiusAtPress, out _);
            _connection.NotifyPathChanged();
        }
        ResetDrag();
    }

    private void ResetDrag()
    {
        _state.ActiveBendIndex = -1;
        _state.ActiveBendClamped = false;
        _connection = null;
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
