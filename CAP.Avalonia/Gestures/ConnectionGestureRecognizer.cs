using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Handles pin-to-pin waveguide connection drawing in Connect interaction mode.
/// Detects pin clicks, shows drag preview, and creates or deletes connections on release.
/// </summary>
public class ConnectionGestureRecognizer : IGestureRecognizer
{
    /// <summary>World-space pin snap/hover distance below the screen-space cap (µm),
    /// matching <see cref="DesignCanvasHitTesting"/>'s click hit radius.</summary>
    private const double BaseHighlightDistanceMicrometers = 15.0;

    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;
    private readonly Func<double> _getZoom;

    /// <summary>Initializes a new instance of <see cref="ConnectionGestureRecognizer"/>.</summary>
    public ConnectionGestureRecognizer(CanvasInteractionState state, Action invalidate, Func<double> getZoom)
    {
        _state = state;
        _invalidate = invalidate;
        _getZoom = getZoom;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Connect) return false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;

        var pin = canvas.HighlightedPin?.Pin ?? DesignCanvasHitTesting.HitTestPin(canvasPoint, canvas, _getZoom());
        if (pin != null)
        {
            _state.ConnectionDragStartPin = pin;
            _state.ConnectionDragCurrentPoint = canvasPoint;
            mainVm.StatusText = $"Drag to another pin to connect from {pin.Name}...";
            _invalidate();
            return true;
        }

        // No pin found — switch to Select mode so user can drag components
        mainVm.CanvasInteraction.CurrentMode = InteractionMode.Select;
        canvas.ClearPinHighlight();
        _invalidate();
        return false;
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Connect) return;

        // The highlight distance is screen-space capped the same way the hit radius and the
        // pin glyph itself are — otherwise a hover ring drawn at the capped (small) glyph size
        // could still snap from a much larger, uncapped world-space distance at high zoom.
        canvas.PinHighlight.PinHighlightDistance =
            PinScreenSize.CapWorldRadius(BaseHighlightDistanceMicrometers, _getZoom());

        if (_state.ConnectionDragStartPin == null)
        {
            mainVm.CanvasMouseMove(canvasPoint.X, canvasPoint.Y);
        }
        else
        {
            canvas.UpdatePinHighlight(canvasPoint.X, canvasPoint.Y, _state.ConnectionDragStartPin);
        }

        _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_state.ConnectionDragStartPin == null) return;

        _state.ConnectionDragCurrentPoint = canvasPoint;

        var targetPin = canvas.HighlightedPin?.Pin;
        // A different pin of the SAME component is a valid target (feedback
        // loops, ring-resonator self-coupling, black-box GDS imports); only the
        // start pin itself is not.
        if (targetPin != null && targetPin != _state.ConnectionDragStartPin)
        {
            if (mainVm != null)
            {
                mainVm.StatusText =
                    !PinKindHelper.AreKindsCompatible(_state.ConnectionDragStartPin, targetPin)
                        ? PinKindHelper.DescribeIncompatibility(_state.ConnectionDragStartPin, targetPin)
                    : !PolarizationRules.CanConnect(_state.ConnectionDragStartPin.Polarization, targetPin.Polarization)
                        ? PolarizationRules.GetMismatchMessage(_state.ConnectionDragStartPin, targetPin)
                    : $"Release to connect {_state.ConnectionDragStartPin.Name} to {targetPin.Name}";
            }
        }
        else
        {
            if (mainVm != null)
                mainVm.StatusText = $"Drag to another pin to connect from {_state.ConnectionDragStartPin.Name}...";
        }

        _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_state.ConnectionDragStartPin == null) return;

        var startPin = _state.ConnectionDragStartPin;
        var targetPin = canvas.HighlightedPin?.Pin;
        bool isValidTarget = targetPin != null && targetPin != startPin;

        if (isValidTarget && !PinKindHelper.AreKindsCompatible(startPin, targetPin!))
        {
            // Cross-domain connection (optical ↔ electrical) is physically meaningless — reject.
            if (mainVm != null)
                mainVm.StatusText = PinKindHelper.DescribeIncompatibility(startPin, targetPin!);
        }
        else if (isValidTarget)
        {
            // TE↔TM connections are physically meaningless — refuse at the
            // gesture layer with an inline message (issue #534).
            if (!PolarizationRules.CanConnect(_state.ConnectionDragStartPin.Polarization, targetPin.Polarization))
            {
                if (mainVm != null)
                    mainVm.StatusText = PolarizationRules.GetMismatchMessage(_state.ConnectionDragStartPin, targetPin);
                _state.ConnectionDragStartPin = null;
                _invalidate();
                return;
            }

            var cmd = new CreateConnectionCommand(canvas, _state.ConnectionDragStartPin, targetPin);
            mainVm?.CommandManager.ExecuteCommand(cmd);
            if (mainVm != null)
                mainVm.StatusText = $"Connected {_state.ConnectionDragStartPin.Name} to {targetPin.Name}";
        }
        else
        {
            var existingConnection = canvas.GetConnectionForPin(_state.ConnectionDragStartPin);
            if (existingConnection != null)
            {
                var deleteCmd = new DeleteConnectionCommand(canvas, existingConnection);
                mainVm?.CommandManager.ExecuteCommand(deleteCmd);
                if (mainVm != null)
                    mainVm.StatusText = $"Deleted connection from {_state.ConnectionDragStartPin.Name}";
            }
            else
            {
                if (mainVm != null)
                    mainVm.StatusText = "Connect mode: Drag from a pin to another pin to connect";
            }
        }

        _state.ConnectionDragStartPin = null;
        _invalidate();
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        _state.ConnectionDragStartPin = null;
        _invalidate();
    }
}
