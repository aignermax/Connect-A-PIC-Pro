using Avalonia;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.Gestures;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Gestures;

/// <summary>
/// Tests that <see cref="ConnectionGestureRecognizer"/> keeps the Connect-mode pin
/// snap/hover distance screen-space capped, consistent with the capped pin glyph and hit
/// radius, so the visible hover ring and the actual snap distance never diverge at high zoom.
/// </summary>
public class ConnectionGestureRecognizerTests
{
    [Fact]
    public void UpdatePassiveState_AtZoomOne_LeavesHighlightDistanceAtWorldSpaceValue()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        mainVm.CanvasInteraction.CurrentMode = InteractionMode.Connect;
        var recognizer = new ConnectionGestureRecognizer(new CanvasInteractionState(), () => { }, () => 1.0);

        recognizer.UpdatePassiveState(new Point(0, 0), canvas, mainVm);

        canvas.PinHighlight.PinHighlightDistance.ShouldBe(15.0,
            "at zoom 1 the cap (16 px) exceeds the base distance (15 µm), so it passes through unchanged");
    }

    [Fact]
    public void UpdatePassiveState_AtHighZoom_CapsHighlightDistanceScreenSpace()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        mainVm.CanvasInteraction.CurrentMode = InteractionMode.Connect;
        const double zoom = 50.0;
        var recognizer = new ConnectionGestureRecognizer(new CanvasInteractionState(), () => { }, () => zoom);

        recognizer.UpdatePassiveState(new Point(0, 0), canvas, mainVm);

        canvas.PinHighlight.PinHighlightDistance.ShouldBe(PinScreenSize.MaxRadiusPx / zoom, 1e-9,
            "the hover/snap distance must cap the same way the rendered pin glyph and hit radius do");
    }
}
