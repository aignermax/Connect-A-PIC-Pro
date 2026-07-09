using System.Collections.ObjectModel;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Canvas.Services;

/// <summary>
/// Handles pin highlighting and nearest-pin search for connection interactions.
/// </summary>
public class PinHighlightService
{
    private readonly ObservableCollection<PinViewModel> _allPins;
    private readonly Func<PhysicalPin, WaveguideConnectionViewModel?> _getConnectionForPin;

    /// <summary>
    /// The currently highlighted pin.
    /// </summary>
    public PinViewModel? HighlightedPin { get; private set; }

    /// <summary>
    /// Distance threshold for pin highlighting (in micrometers).
    /// </summary>
    public double PinHighlightDistance { get; set; } = 15.0;

    /// <summary>
    /// Raised when the highlighted pin changes.
    /// </summary>
    public event Action? HighlightChanged;

    /// <summary>
    /// Initializes the pin highlight service.
    /// </summary>
    public PinHighlightService(
        ObservableCollection<PinViewModel> allPins,
        Func<PhysicalPin, WaveguideConnectionViewModel?> getConnectionForPin)
    {
        _allPins = allPins;
        _getConnectionForPin = getConnectionForPin;
    }

    /// <summary>
    /// Finds and highlights the nearest pin to the given position.
    /// </summary>
    public PinViewModel? UpdatePinHighlight(double x, double y, PhysicalPin? excludePin = null)
    {
        if (HighlightedPin != null)
        {
            HighlightedPin.SetHighlighted(false);
            HighlightedPin = null;
        }

        PinViewModel? nearest = null;
        double nearestDistance = double.MaxValue;

        foreach (var pinVm in _allPins)
        {
            if (excludePin != null)
            {
                if (pinVm.Pin == excludePin) continue;
                if (pinVm.Pin.ParentComponent == excludePin.ParentComponent) continue;
                // Polarization-incompatible pins (TE↔TM) are never valid
                // connection targets, so don't highlight them during a drag.
                if (!PolarizationRules.CanConnect(excludePin.Polarization, pinVm.Pin.Polarization)) continue;
            }

            var (pinX, pinY) = pinVm.Pin.GetAbsolutePosition();
            double dist = Math.Sqrt(Math.Pow(x - pinX, 2) + Math.Pow(y - pinY, 2));

            if (dist < nearestDistance && dist <= PinHighlightDistance)
            {
                nearest = pinVm;
                nearestDistance = dist;
            }
        }

        if (nearest != null)
        {
            nearest.SetHighlighted(true);
            nearest.HasConnection = _getConnectionForPin(nearest.Pin) != null;
            HighlightedPin = nearest;
        }

        HighlightChanged?.Invoke();
        return nearest;
    }

    /// <summary>
    /// Clears all pin highlighting.
    /// </summary>
    public void ClearPinHighlight()
    {
        if (HighlightedPin != null)
        {
            HighlightedPin.SetHighlighted(false);
            HighlightedPin = null;
            HighlightChanged?.Invoke();
        }
    }

    /// <summary>
    /// Gets the nearest pin at or near the given position.
    /// </summary>
    public PhysicalPin? GetPinAt(double x, double y, double tolerance = 15.0)
    {
        PhysicalPin? nearest = null;
        double nearestDistance = double.MaxValue;

        foreach (var pinVm in _allPins)
        {
            var (pinX, pinY) = pinVm.Pin.GetAbsolutePosition();
            double dist = Math.Sqrt(Math.Pow(x - pinX, 2) + Math.Pow(y - pinY, 2));

            if (dist < nearestDistance && dist <= tolerance)
            {
                nearest = pinVm.Pin;
                nearestDistance = dist;
            }
        }
        return nearest;
    }
}
