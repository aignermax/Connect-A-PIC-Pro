using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using System.Globalization;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Draws the live logic-state badge (issue #994) of every gate group while the Logic
/// panel's network is built: a small dark chip at the group's top-right corner carrying
/// the evaluated bit of one gate output pin — LightGreen for 1, gray for 0, the same
/// colors the Logic panel's output list uses. A multi-output gate gets one chip per
/// output pin, stacked downward. Pins carrying a persisted signal name get a wider
/// chip instead of the square one, showing the signal name next to its live bit —
/// the gate input chips (<c>A0 = 1</c>, issue #1051) and, symmetric to them, the
/// named output taps (<c>S0 = 1</c>, issue #1067); unnamed pins keep the plain
/// square 0/1 chip exactly.
/// The chips only ever sit on top of the group — the
/// group itself is never repainted — and they vanish with the network (rebuild, cancel,
/// design edit, load), driven entirely by <see cref="LogicGateStateOverlay"/>.
/// </summary>
internal static class LogicGateStateBadgeRenderer
{
    private const double BadgeSize = 16;
    private const double BadgeMargin = 4;
    private const double BadgeSpacing = 2;
    private const double BadgeCornerRadius = 3;
    private const double BadgeFontSize = 11;
    private const double BadgeTextPaddingX = 3;

    private static readonly Color BackingColor = Color.FromArgb(220, 30, 30, 30);
    private static readonly Color BorderColor = Color.FromArgb(160, 120, 120, 120);
    private static readonly Color OneColor = Color.FromRgb(144, 238, 144); // LightGreen — matches the Logic panel
    private static readonly Color ZeroColor = Color.FromRgb(158, 158, 158); // Gray — matches the Logic panel

    private static readonly IBrush BackingBrush = new SolidColorBrush(BackingColor);
    private static readonly Pen BorderPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly IBrush OneBrush = new SolidColorBrush(OneColor);
    private static readonly IBrush ZeroBrush = new SolidColorBrush(ZeroColor);

    /// <summary>Draws one state badge per gate output pin on every gate group of the network.</summary>
    /// <param name="context">Drawing context.</param>
    /// <param name="rc">The render context carrying the canvas ViewModel with the badge states.</param>
    public static void Render(DrawingContext context, CanvasRenderContext rc)
    {
        var badges = rc.ViewModel.LogicGateStates.Badges;
        if (badges.Count == 0)
            return;

        var badgesByGroup = badges
            .GroupBy(badge => badge.GroupName)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var comp in rc.ViewModel.Components)
        {
            if (comp.Component is not ComponentGroup group)
                continue;
            if (!badgesByGroup.TryGetValue(group.GroupName, out var groupBadges))
                continue;

            var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);
            for (var i = 0; i < groupBadges.Count; i++)
            {
                DrawBadge(context, bounds, i, groupBadges[i]);
            }
        }
    }

    /// <summary>Draws one chip: dark backing, thin border, centered bit — named badges
    /// widen the chip to fit their <c>name = bit</c> label, growing left from the
    /// group's right edge so the unnamed square chip keeps its exact geometry.</summary>
    private static void DrawBadge(DrawingContext context, Rect groupBounds, int index, LogicGateBadgeViewModel badge)
    {
        var text = new FormattedText(
            badge.LabelText,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial", FontStyle.Normal, FontWeight.Bold),
            BadgeFontSize,
            badge.IsOne ? OneBrush : ZeroBrush);

        var width = badge.HasSignalName
            ? Math.Max(BadgeSize, text.Width + 2 * BadgeTextPaddingX)
            : BadgeSize;
        var rect = new Rect(
            groupBounds.Right - width - BadgeMargin,
            groupBounds.Top + BadgeMargin + index * (BadgeSize + BadgeSpacing),
            width,
            BadgeSize);
        context.DrawRectangle(BackingBrush, BorderPen, rect, BadgeCornerRadius, BadgeCornerRadius);

        context.DrawText(text, new Point(
            rect.Center.X - text.Width / 2,
            rect.Center.Y - text.Height / 2));
    }
}
