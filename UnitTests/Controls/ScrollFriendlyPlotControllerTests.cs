using CAP.Avalonia.Controls.Plotting;
using Moq;
using OxyPlot;
using Shouldly;
using Xunit;

namespace UnitTests.Controls;

/// <summary>
/// Tests for <see cref="ScrollFriendlyPlotController"/> (issue #693): analysis charts
/// must not swallow plain mouse-wheel events (the panel should scroll), while
/// Ctrl/Cmd + wheel zooms and the default pan/tracker bindings stay intact.
/// </summary>
public class ScrollFriendlyPlotControllerTests
{
    [Fact]
    public void Create_PlainMouseWheel_IsNotBound()
    {
        var controller = ScrollFriendlyPlotController.Create();

        controller.InputCommandBindings
            .Any(b => b.Gesture is OxyMouseWheelGesture gesture && gesture.Modifiers == OxyModifierKeys.None)
            .ShouldBeFalse();
    }

    [Fact]
    public void Create_CtrlMouseWheel_BindsZoomWheel()
    {
        var controller = ScrollFriendlyPlotController.Create();

        var binding = controller.InputCommandBindings
            .Where(b => b.Gesture is OxyMouseWheelGesture gesture && gesture.Modifiers == OxyModifierKeys.Control)
            .ShouldHaveSingleItem();
        binding.Command.ShouldBeSameAs(PlotCommands.ZoomWheel);
    }

    [Fact]
    public void Create_CmdMouseWheel_BindsZoomWheel_ForMacOs()
    {
        var controller = ScrollFriendlyPlotController.Create();

        // Avalonia reports macOS Cmd as KeyModifiers.Meta, which OxyPlot.Avalonia
        // converts to OxyModifierKeys.Windows.
        var binding = controller.InputCommandBindings
            .Where(b => b.Gesture is OxyMouseWheelGesture gesture && gesture.Modifiers == OxyModifierKeys.Windows)
            .ShouldHaveSingleItem();
        binding.Command.ShouldBeSameAs(PlotCommands.ZoomWheel);
    }

    [Fact]
    public void HandleMouseWheel_WithoutModifier_ReturnsFalse_SoScrollViewerScrolls()
    {
        var controller = ScrollFriendlyPlotController.Create();
        var view = new Mock<IPlotView>();
        var args = new OxyMouseWheelEventArgs { ModifierKeys = OxyModifierKeys.None, Delta = 120 };

        controller.HandleMouseWheel(view.Object, args).ShouldBeFalse();
    }

    [Fact]
    public void Create_KeepsDefaultPanAndTrackerBindings()
    {
        var controller = ScrollFriendlyPlotController.Create();
        var defaults = new PlotController();

        var defaultMouseDownGestures = defaults.InputCommandBindings
            .Where(b => b.Gesture is OxyMouseDownGesture)
            .Select(b => b.Gesture)
            .ToList();

        defaultMouseDownGestures.ShouldNotBeEmpty();
        foreach (var gesture in defaultMouseDownGestures)
        {
            controller.InputCommandBindings.ShouldContain(b => b.Gesture.Equals(gesture));
        }
    }
}
