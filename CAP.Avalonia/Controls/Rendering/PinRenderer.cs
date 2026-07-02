using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Renders component pins, pin direction indicators, and component name labels.
/// Used internally by <see cref="ComponentRenderer"/>.
/// </summary>
internal sealed class PinRenderer
{
    /// <summary>
    /// Renders all physical pins of a component.
    /// </summary>
    public void DrawComponentPins(DrawingContext context, ComponentViewModel comp, CanvasRenderContext rc, bool isDimmed = false)
    {
        bool isConnectMode = rc.MainViewModel?.CanvasInteraction.CurrentMode == InteractionMode.Connect;
        var highlightedPin = rc.ViewModel.HighlightedPin?.Pin;
        var dragStartPin = rc.InteractionState.ConnectionDragStartPin;
        byte baseAlpha = (byte)(isDimmed ? 128 : 255);

        foreach (var pin in comp.Component.PhysicalPins)
        {
            var (pinX, pinY) = pin.GetAbsolutePosition();
            bool isHighlighted = pin == highlightedPin;

            // During a connection drag, dim polarization-incompatible pins so
            // the user sees which targets are valid (issue #534).
            bool isIncompatibleTarget = dragStartPin != null && pin != dragStartPin &&
                !PolarizationRules.CanConnect(dragStartPin.Polarization, pin.Polarization);
            byte alpha = isIncompatibleTarget ? (byte)(baseAlpha / 3) : baseAlpha;

            double pinSize = isConnectMode ? 8 : 5;
            IBrush pinBrush = GetPinBrush(isHighlighted, isConnectMode, pin, alpha);

            if (isHighlighted)
            {
                pinSize = 12;
                var glowBrush = new SolidColorBrush(Color.FromArgb((byte)(100 * alpha / 255), 0, 255, 255));
                context.DrawEllipse(glowBrush, null, new Point(pinX, pinY), pinSize * 1.5, pinSize * 1.5);
            }

            DrawPinShape(context, pin, pinBrush, pinX, pinY, pinSize, alpha);
            DrawPinDirectionIndicator(context, pin, pinX, pinY, isHighlighted, isDimmed);

            if (isHighlighted)
            {
                var pinText = new FormattedText(
                    pin.Name,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Arial"),
                    10,
                    new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 255)));
                context.DrawText(pinText, new Point(pinX + 15, pinY - 15));
            }
        }
    }

    /// <summary>
    /// Renders the component name label at the top-left of the component.
    /// </summary>
    public void DrawComponentName(DrawingContext context, ComponentViewModel comp, bool isDimmed = false)
    {
        byte alpha = (byte)(isDimmed ? 128 : 255);
        var text = new FormattedText(
            comp.Name,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial"),
            12,
            new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)));
        context.DrawText(text, new Point(comp.X + 5, comp.Y + 5));
    }

    /// <summary>
    /// Draws the pin marker with a polarization-specific shape:
    /// TE = solid circle (historical default), TM = solid square,
    /// Both = circle with a square outline (accepts either polarization).
    /// </summary>
    private static void DrawPinShape(DrawingContext context, PhysicalPin pin, IBrush brush,
        double pinX, double pinY, double pinSize, byte alpha)
    {
        switch (pin.Polarization)
        {
            case PolarizationKind.TM:
                context.DrawRectangle(brush, null,
                    new Rect(pinX - pinSize, pinY - pinSize, pinSize * 2, pinSize * 2));
                break;
            case PolarizationKind.Both:
                context.DrawEllipse(brush, null, new Point(pinX, pinY), pinSize, pinSize);
                var outlinePen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), 1);
                context.DrawRectangle(null, outlinePen,
                    new Rect(pinX - pinSize, pinY - pinSize, pinSize * 2, pinSize * 2));
                break;
            default:
                context.DrawEllipse(brush, null, new Point(pinX, pinY), pinSize, pinSize);
                break;
        }
    }

    private static IBrush GetPinBrush(bool isHighlighted, bool isConnectMode, PhysicalPin pin, byte alpha)
    {
        if (isHighlighted)
            return new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 255));
        if (isConnectMode)
            return new SolidColorBrush(Color.FromArgb(alpha, 255, 200, 0));
        if (pin.LogicalPin != null)
            return new SolidColorBrush(Color.FromArgb(alpha, 100, 200, 100));
        return new SolidColorBrush(Color.FromArgb(alpha, 200, 100, 100));
    }

    private static void DrawPinDirectionIndicator(DrawingContext context, PhysicalPin pin, double pinX, double pinY, bool isHighlighted, bool isDimmed)
    {
        byte alpha = (byte)(isDimmed ? 128 : 255);
        var dirBrush = isHighlighted
            ? new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 255))
            : new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        var dirPen = new Pen(dirBrush, isHighlighted ? 2 : 1);
        double angle = pin.GetAbsoluteAngle() * Math.PI / 180;
        double dirLength = isHighlighted ? 20 : 15;
        context.DrawLine(dirPen,
            new Point(pinX, pinY),
            new Point(pinX + Math.Cos(angle) * dirLength, pinY + Math.Sin(angle) * dirLength));
    }
}
