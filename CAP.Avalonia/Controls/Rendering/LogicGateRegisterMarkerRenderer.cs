using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Components.Core;
using System.Globalization;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Draws the register marker (issue #1112) of every register-designated gate group:
/// a small rounded "R" chip at the group's top-left corner, in the same dark style
/// as the live 0/1 badges (<see cref="LogicGateStateBadgeRenderer"/>, issue #994) —
/// same backing, border, corner radius, and font — so a student can tell at a glance
/// which groups on the canvas hold state (the SR latch's two NAND registers) and
/// which are plain combinational gates. The marker reads the persisted
/// <see cref="TruthTablePinAssignment.IsRegister"/> flag directly, so it renders on
/// load without a built network and follows the Truth Table panel's Register toggle
/// (issue #1098) live: the toggle writes the flag and requests a canvas repaint.
/// Combinational groups, ungrouped components, and imported black-box cells without
/// the flag get no marker.
/// </summary>
internal static class LogicGateRegisterMarkerRenderer
{
    private const double MarkerSize = 16;
    private const double MarkerMargin = 4;
    private const double MarkerCornerRadius = 3;
    private const double MarkerFontSize = 11;

    private static readonly Color BackingColor = Color.FromArgb(220, 30, 30, 30);
    private static readonly Color BorderColor = Color.FromArgb(160, 120, 120, 120);
    private static readonly Color TextColor = Color.FromRgb(158, 180, 255); // muted blue — distinct from the green/gray badges without fighting them

    private static readonly IBrush BackingBrush = new SolidColorBrush(BackingColor);
    private static readonly Pen BorderPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly IBrush TextBrush = new SolidColorBrush(TextColor);

    /// <summary>The marker glyph — a plain capital R, language-neutral like the 0/1 digits.</summary>
    internal const string MarkerText = "R";

    /// <summary>
    /// The marker's tooltip sentence ("Register: holds its committed output until the
    /// next clock step"), localized through the shipped string tables. Canvas-drawn
    /// chips cannot carry an Avalonia <c>ToolTip</c>, so the text is exposed here for
    /// the hover layer to read — and for the localization completeness tests to pin.
    /// </summary>
    public static string TooltipText =>
        LocalizationService.Instance.Translate("LogicGate.RegisterMarker.Tooltip");

    /// <summary>Draws one register marker per register-designated gate group.</summary>
    /// <param name="context">Drawing context.</param>
    /// <param name="rc">The render context carrying the canvas ViewModel with the components.</param>
    public static void Render(DrawingContext context, CanvasRenderContext rc)
    {
        foreach (var comp in rc.ViewModel.Components)
        {
            if (comp.Component is not ComponentGroup group)
                continue;
            if (group.TruthTablePinAssignment?.IsRegister != true)
                continue;

            DrawMarker(context, ComponentGroupRenderer.CalculateGroupBounds(group));
        }
    }

    /// <summary>Draws one chip at the group's top-left corner: dark backing, thin border, centered "R".</summary>
    private static void DrawMarker(DrawingContext context, Rect groupBounds)
    {
        var text = new FormattedText(
            MarkerText,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial", FontStyle.Normal, FontWeight.Bold),
            MarkerFontSize,
            TextBrush);

        var rect = new Rect(
            groupBounds.Left + MarkerMargin,
            groupBounds.Top + MarkerMargin,
            MarkerSize,
            MarkerSize);
        context.DrawRectangle(BackingBrush, BorderPen, rect, MarkerCornerRadius, MarkerCornerRadius);

        context.DrawText(text, new Point(
            rect.Center.X - text.Width / 2,
            rect.Center.Y - text.Height / 2));
    }
}
