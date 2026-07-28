using CAP.Avalonia.Controls.Rendering;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Tests for <see cref="WaveguideConnectionRenderer.ShouldShowLengthLossLabel"/>: pins down that
/// hover/selection gating applies to exactly the length/loss label and nothing else. A PR review
/// found the gate had originally been wrapped around the connection's entire text-overlay draw
/// call, which also hid the always-on power-flow readout and manual-style badge whenever the
/// connection wasn't hovered or selected — this predicate is the isolated, directly-testable
/// piece of that decision so it can't silently regress back to gating everything again.
/// </summary>
public class WaveguideConnectionRendererTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldShowLengthLossLabel_MatchesHoverOrSelection(bool isHovered, bool isSelected, bool expected)
    {
        WaveguideConnectionRenderer.ShouldShowLengthLossLabel(isHovered, isSelected).ShouldBe(expected);
    }
}
