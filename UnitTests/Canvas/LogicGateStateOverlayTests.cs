using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;

namespace UnitTests.Canvas;

/// <summary>
/// View-model tests for the canvas logic-state overlay's named badges (issue #1051,
/// rung 4→5 of the NAND game): a gate input pin carrying a persisted signal name gets a
/// badge that shows the name next to the live bit (<c>A0 = 1</c>), so a student watching
/// the adder compute can tell which badge is which signal. Unnamed pins keep the plain
/// 0/1 chip exactly, and a name that disappears from the design disappears from the
/// badge on the next rebuild — the overlay mirrors exactly the states it is handed.
/// </summary>
public class LogicGateStateOverlayTests
{
    [Fact]
    public void ShowStates_NamedInputPin_BadgeShowsSignalNameAndBit()
    {
        var overlay = new LogicGateStateOverlay();

        overlay.ShowStates(new[]
        {
            new LogicGateBadgeState("NAND1A", "Y", true),
            new LogicGateBadgeState("NAND1A", "A", true, "A0"),
        });

        var named = overlay.Badges.Single(b => b.PinName == "A");
        named.HasSignalName.ShouldBeTrue();
        named.SignalName.ShouldBe("A0");
        named.LabelText.ShouldBe("A0 = 1");
    }

    [Fact]
    public void ShowStates_UnnamedPin_BadgeKeepsPlainBitText()
    {
        var overlay = new LogicGateStateOverlay();

        overlay.ShowStates(new[] { new LogicGateBadgeState("NAND1A", "Y", false) });

        var badge = overlay.Badges.Single();
        badge.HasSignalName.ShouldBeFalse();
        badge.SignalName.ShouldBeNull();
        badge.LabelText.ShouldBe(badge.BitText);
        badge.LabelText.ShouldBe("0");
    }

    [Fact]
    public void ShowStates_NameRemovedOnRebuild_LabelDisappears()
    {
        var overlay = new LogicGateStateOverlay();
        overlay.ShowStates(new[] { new LogicGateBadgeState("NAND1A", "A", true, "A0") });
        overlay.Badges.Single().HasSignalName.ShouldBeTrue();

        // The rebuild hands the overlay the freshly read states; without the persisted
        // name the same pin comes back as an anonymous badge.
        overlay.ShowStates(new[] { new LogicGateBadgeState("NAND1A", "A", true) });

        var badge = overlay.Badges.Single();
        badge.HasSignalName.ShouldBeFalse();
        badge.LabelText.ShouldBe("1");
    }

    [Fact]
    public void ShowStates_NamedBadge_NotifiesCanvasRepaint()
    {
        var overlay = new LogicGateStateOverlay();
        var notified = 0;
        overlay.StatesChanged += (_, _) => notified++;

        overlay.ShowStates(new[] { new LogicGateBadgeState("NAND1A", "A", true, "A0") });

        notified.ShouldBe(1, "the canvas repaints once per badge rebuild");
    }
}
