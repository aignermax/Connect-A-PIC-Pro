using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
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
    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;

    /// <summary>Initializes a new instance of <see cref="ConnectionGestureRecognizer"/>.</summary>
    public ConnectionGestureRecognizer(CanvasInteractionState state, Action invalidate)
    {
        _state = state;
        _invalidate = invalidate;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Connect) return false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;

        var pin = canvas.HighlightedPin?.Pin ?? DesignCanvasHitTesting.HitTestPin(canvasPoint, canvas);
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
        if (targetPin != null && targetPin != _state.ConnectionDragStartPin &&
            targetPin.ParentComponent != _state.ConnectionDragStartPin.ParentComponent)
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
        bool isValidTarget = targetPin != null && targetPin != startPin &&
            targetPin.ParentComponent != startPin.ParentComponent;

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
