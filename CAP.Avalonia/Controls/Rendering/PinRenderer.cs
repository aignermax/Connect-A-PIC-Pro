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
    /// World-space font size for a component's name label before the <see cref="PinScreenSize"/>
    /// screen-space cap is applied. Shared with <see cref="LabelDeclutter.ComponentNameLabelComputer"/>
    /// so the overlap-resolution pass measures text at exactly the size actually drawn.
    /// </summary>
    internal const double NameLabelFontSizeWorld = 12.0;

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

            // During a connection drag, dim incompatible pins (wrong signal domain or wrong
            // polarization) so the user sees which targets are valid before releasing
            // (issue #534, extended to the domain check in #724 point 4).
            bool isIncompatibleTarget = dragStartPin != null && pin != dragStartPin &&
                PinConnectionAffordance.IsIncompatibleTarget(dragStartPin, pin);
            byte alpha = isIncompatibleTarget ? (byte)(baseAlpha / 3) : baseAlpha;

            double pinSize = PinScreenSize.CapWorldRadius(isConnectMode ? 8 : 5, rc.Zoom);
            IBrush pinBrush = GetPinBrush(isHighlighted, isConnectMode, pin, alpha);

            if (isHighlighted)
            {
                pinSize = PinScreenSize.CapWorldRadius(12, rc.Zoom);
                var glowBrush = new SolidColorBrush(Color.FromArgb((byte)(100 * alpha / 255), 0, 255, 255));
                context.DrawEllipse(glowBrush, null, new Point(pinX, pinY), pinSize * 1.5, pinSize * 1.5);
            }

            DrawPinShape(context, pin, pinBrush, pinX, pinY, pinSize, alpha);
            DrawPinDirectionIndicator(context, pin, pinX, pinY, isHighlighted, isDimmed, rc.Zoom);

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
    /// Renders the component name label at the top-left of the component. The font size is
    /// capped in screen pixels (<see cref="PinScreenSize.CapWorldFontSize"/>) so it stops
    /// growing at high zoom, and the label is skipped entirely once it would shrink below
    /// <see cref="PinScreenSize.MinLabelFontSizePx"/> at low zoom — callers should already have
    /// excluded such labels via <see cref="LabelDeclutter.ComponentNameLabelComputer"/>, but the
    /// check here keeps this method correct on its own.
    /// </summary>
    public void DrawComponentName(DrawingContext context, ComponentViewModel comp, double zoom, bool isDimmed = false)
    {
        if (!PinScreenSize.IsLabelReadable(NameLabelFontSizeWorld, zoom))
            return;

        byte alpha = (byte)(isDimmed ? 128 : 255);
        double fontSize = PinScreenSize.CapWorldFontSize(NameLabelFontSizeWorld, zoom);
        var text = new FormattedText(
            comp.Name,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial"),
            fontSize,
            new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)));
        context.DrawText(text, new Point(comp.X + 5, comp.Y + 5));
    }

    /// <summary>
    /// Draws the pin marker glyph resolved by <see cref="PinGlyphSelector"/> — the single
    /// place deciding shape from (MatterType × Polarization), so an electrical pin can never
    /// pick up a polarization shape and a TM pin can never look electrical (issue #724).
    /// Color already encodes <see cref="MatterType"/> via <see cref="GetPinBrush"/>.
    /// </summary>
    private static void DrawPinShape(DrawingContext context, PhysicalPin pin, IBrush brush,
        double pinX, double pinY, double pinSize, byte alpha)
    {
        switch (PinGlyphSelector.SelectGlyph(pin.MatterType, pin.Polarization))
        {
            case PinGlyph.ElectricalPad:
                DrawElectricalPad(context, brush, pinX, pinY, pinSize, alpha);
                break;
            case PinGlyph.OpticalDiamond:
                context.DrawGeometry(brush, null, BuildDiamondGeometry(pinX, pinY, pinSize));
                break;
            case PinGlyph.OpticalCircleWithDiamondOutline:
                context.DrawEllipse(brush, null, new Point(pinX, pinY), pinSize, pinSize);
                var outlinePen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), 1);
                context.DrawGeometry(null, outlinePen, BuildDiamondGeometry(pinX, pinY, pinSize));
                break;
            default:
                context.DrawEllipse(brush, null, new Point(pinX, pinY), pinSize, pinSize);
                break;
        }
    }

    /// <summary>
    /// Draws the electrical pad glyph: a filled square (the historical electrical shape) plus a
    /// darker contact-rim border, so it reads as a metal pad rather than a plain optical-domain
    /// square — and never collides with the optical <see cref="PinGlyph.OpticalDiamond"/> shape.
    /// </summary>
    private static void DrawElectricalPad(DrawingContext context, IBrush brush,
        double pinX, double pinY, double pinSize, byte alpha)
    {
        var rect = new Rect(pinX - pinSize, pinY - pinSize, pinSize * 2, pinSize * 2);
        context.DrawRectangle(brush, null, rect);
        var rimPen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 90, 60, 10)), 1.5);
        context.DrawRectangle(null, rimPen, rect);
    }

    /// <summary>Builds a diamond (rotated square) geometry centered at the pin position.</summary>
    private static StreamGeometry BuildDiamondGeometry(double pinX, double pinY, double pinSize)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(pinX, pinY - pinSize), true);
            ctx.LineTo(new Point(pinX + pinSize, pinY));
            ctx.LineTo(new Point(pinX, pinY + pinSize));
            ctx.LineTo(new Point(pinX - pinSize, pinY));
            ctx.EndFigure(true);
        }
        return geometry;
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

    private static void DrawPinDirectionIndicator(
        DrawingContext context, PhysicalPin pin, double pinX, double pinY,
        bool isHighlighted, bool isDimmed, double zoom)
    {
        byte alpha = (byte)(isDimmed ? 128 : 255);
        var dirBrush = isHighlighted
            ? new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 255))
            : new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        var dirPen = new Pen(dirBrush, isHighlighted ? 2 : 1);
        double angle = pin.GetAbsoluteAngle() * Math.PI / 180;
        double dirLength = PinScreenSize.CapWorldRadius(isHighlighted ? 20 : 15, zoom);
        context.DrawLine(dirPen,
            new Point(pinX, pinY),
            new Point(pinX + Math.Cos(angle) * dirLength, pinY + Math.Sin(angle) * dirLength));
    }
}
