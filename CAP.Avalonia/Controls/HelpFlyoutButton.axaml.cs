using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;

namespace CAP.Avalonia.Controls;

/// <summary>
/// Reusable "?" help button: any panel or window drops this in and supplies its own
/// <see cref="HelpContent"/> (plain-language sections, an illustrative animation, ...)
/// instead of hand-rolling a Button + Flyout pair. Generalizes the one-off pattern in
/// <c>CAP.Avalonia/Views/Panels/TransientHelpFlyout.axaml</c>. On open, the flyout content
/// fades and slides up into place, staggered item-by-item for a "stacking" feel (#682).
/// </summary>
public partial class HelpFlyoutButton : UserControl
{
    /// <summary>Bold header text shown at the top of the flyout. Hidden when null/empty.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<HelpFlyoutButton, string?>(nameof(Title));

    /// <summary>Caller-supplied explanatory content (any control tree) shown inside the flyout.</summary>
    public static readonly StyledProperty<object?> HelpContentProperty =
        AvaloniaProperty.Register<HelpFlyoutButton, object?>(nameof(HelpContent));

    /// <summary>Delay step between consecutively staggered children, in milliseconds.</summary>
    private const int StaggerStepMs = 45;

    /// <summary>Caps the number of stagger steps so long content doesn't take forever to reveal.</summary>
    private const int MaxStaggerSteps = 10;

    /// <summary>Duration of each item's fade + slide-in animation.</summary>
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(260);

    /// <summary>Initializes the control and wires the entrance animation to the flyout opening.</summary>
    public HelpFlyoutButton()
    {
        InitializeComponent();

        // The Flyout itself isn't a Control, so it isn't exposed as a generated
        // x:Name field; reach it via the button that hosts it instead.
        if (HelpButton.Flyout is FlyoutBase flyout)
            flyout.Opened += (_, _) => AnimateEntrance();
    }

    /// <summary>Bold header text shown at the top of the flyout.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Caller-supplied explanatory content shown inside the flyout.</summary>
    public object? HelpContent
    {
        get => GetValue(HelpContentProperty);
        set => SetValue(HelpContentProperty, value);
    }

    /// <summary>
    /// Fades and slides the flyout's content into place. Works for any content: if the
    /// top-level <see cref="HelpContent"/> is a <see cref="Panel"/> (the common case, a
    /// StackPanel of sections), each direct child staggers in one after another; otherwise
    /// the whole content animates as a single block.
    /// </summary>
    private void AnimateEntrance()
    {
        var targets = GetStaggerTargets();
        var transitions = CreateEntranceTransitions();

        // Snap every target to the hidden state first, before attaching transitions, so
        // re-opening the flyout always replays the animation from scratch rather than
        // animating from wherever the last close left things.
        foreach (var target in targets)
        {
            target.Transitions = null;
            target.Opacity = 0;
            target.RenderTransform = TransformOperations.Parse("translateY(14px)");
        }

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            target.Transitions = transitions;
            _ = RevealAsync(target, Math.Min(i, MaxStaggerSteps) * StaggerStepMs);
        }
    }

    /// <summary>Waits <paramref name="delayMs"/> then transitions one target to its visible state.</summary>
    private static async Task RevealAsync(Control target, int delayMs)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs);

        target.Opacity = 1;
        target.RenderTransform = TransformOperations.Identity;
    }

    /// <summary>Builds the shared opacity + slide transition used by every staggered item.</summary>
    private static Transitions CreateEntranceTransitions() => new()
    {
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = EntranceDuration, Easing = new CubicEaseOut() },
        new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = EntranceDuration, Easing = new CubicEaseOut() },
    };

    /// <summary>
    /// Items to animate: the header (if visible), then either the content panel's own
    /// direct children (staggered) or the whole content control as one block.
    /// </summary>
    private List<Control> GetStaggerTargets()
    {
        var targets = new List<Control>();

        if (TitleText.IsVisible)
            targets.Add(TitleText);

        if (HelpContent is Panel panel)
            targets.AddRange(panel.Children.OfType<Control>());
        else if (HelpContent is Control content)
            targets.Add(content);

        return targets;
    }
}
