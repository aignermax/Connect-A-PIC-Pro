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
    /// <summary>Copper/gold colour marking electrical pins (Issue #519).</summary>
    private static readonly Color ElectricalPinColor = Color.FromRgb(218, 165, 32);

    /// <summary>
    /// Renders all physical pins of a component.
    /// </summary>
    public void DrawComponentPins(DrawingContext context, ComponentViewModel comp, CanvasRenderContext rc, bool isDimmed = false)
    {
        bool isConnectMode = rc.MainViewModel?.CanvasInteraction.CurrentMode == InteractionMode.Connect;
        var highlightedPin = rc.ViewModel.HighlightedPin?.Pin;
        byte alpha = (byte)(isDimmed ? 128 : 255);

        foreach (var pin in comp.Component.PhysicalPins)
        {
            var (pinX, pinY) = pin.GetAbsolutePosition();
            bool isHighlighted = pin == highlightedPin;
            double pinSize = isConnectMode ? 8 : 5;
            IBrush pinBrush = GetPinBrush(isHighlighted, isConnectMode, pin, alpha);

            if (isHighlighted)
            {
                pinSize = 12;
                var glowBrush = new SolidColorBrush(Color.FromArgb((byte)(100 * alpha / 255), 0, 255, 255));
                context.DrawEllipse(glowBrush, null, new Point(pinX, pinY), pinSize * 1.5, pinSize * 1.5);
            }

            DrawPinShape(context, pin, pinBrush, pinX, pinY, pinSize);
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

    private static IBrush GetPinBrush(bool isHighlighted, bool isConnectMode, PhysicalPin pin, byte alpha)
    {
        if (isHighlighted)
            return new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 255));
        if (pin.MatterType == MatterType.Electricity)
            return new SolidColorBrush(Color.FromArgb(alpha, ElectricalPinColor.R, ElectricalPinColor.G, ElectricalPinColor.B));
        if (isConnectMode)
            return new SolidColorBrush(Color.FromArgb(alpha, 255, 200, 0));
        if (pin.LogicalPin != null)
            return new SolidColorBrush(Color.FromArgb(alpha, 100, 200, 100));
        return new SolidColorBrush(Color.FromArgb(alpha, 200, 100, 100));
    }

    /// <summary>
    /// Draws the pin marker: optical pins as circles, electrical pins as squares
    /// so the signal domain is visually distinguishable at a glance.
    /// </summary>
    private static void DrawPinShape(DrawingContext context, PhysicalPin pin, IBrush brush, double pinX, double pinY, double pinSize)
    {
        if (pin.MatterType == MatterType.Electricity)
        {
            context.DrawRectangle(brush, null, new Rect(pinX - pinSize, pinY - pinSize, pinSize * 2, pinSize * 2));
            return;
        }
        context.DrawEllipse(brush, null, new Point(pinX, pinY), pinSize, pinSize);
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
