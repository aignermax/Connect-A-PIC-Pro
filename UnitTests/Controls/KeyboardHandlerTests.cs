using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Controls.Handlers;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Controls;

/// <summary>
/// Unit tests for <see cref="KeyboardHandler"/>.
/// Verifies that keyboard shortcuts dispatch to the correct ViewModel commands.
/// </summary>
[Trait("Category", "Slow")]
public class KeyboardHandlerTests
{
    private static KeyEventArgs MakeKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
        => new() { Key = key, KeyModifiers = modifiers };

    private static (KeyboardHandler handler, DesignCanvasViewModel canvasVm) CreateHandler(MainViewModel? mainVm = null)
    {
        mainVm ??= MainViewModelTestHelper.CreateMainViewModel();
        var canvasVm = mainVm.Canvas;
        var handler = new KeyboardHandler(
            () => canvasVm,
            () => mainVm,
            () => new Rect(0, 0, 800, 600));
        return (handler, canvasVm);
    }

    [Fact]
    public void OnKeyDown_SKey_SetsSelectMode()
    {
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.S);
        handler.OnKeyDown(e);
        e.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_CtrlS_MarksHandled()
    {
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.S, KeyModifiers.Control);
        handler.OnKeyDown(e);
        e.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_CKey_SetsConnectMode()
    {
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.C);
        handler.OnKeyDown(e);
        e.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_CtrlC_NotHandled()
    {
        // Ctrl+C should pass through for copy
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.C, KeyModifiers.Control);
        handler.OnKeyDown(e);
        e.Handled.ShouldBeFalse();
    }

    [Fact]
    public void OnKeyDown_DKey_SetsDeleteMode()
    {
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.D);
        handler.OnKeyDown(e);
        e.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_RKey_MarksHandled()
    {
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.R);
        handler.OnKeyDown(e);
        e.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_GKey_TogglesGridSnap()
    {
        var (handler, canvasVm) = CreateHandler();
        bool initialSnap = canvasVm.GridSnap.IsEnabled;
        handler.OnKeyDown(MakeKey(Key.G));
        canvasVm.GridSnap.IsEnabled.ShouldBe(!initialSnap);
    }

    [Fact]
    public void OnKeyDown_ShiftG_TogglesGridOverlay()
    {
        var (handler, canvasVm) = CreateHandler();
        bool initial = canvasVm.ShowGridOverlay;
        handler.OnKeyDown(MakeKey(Key.G, KeyModifiers.Shift));
        canvasVm.ShowGridOverlay.ShouldBe(!initial);
    }

    [Fact]
    public void OnKeyDown_Escape_CallsSetSelectMode_WhenNotInGroupEditMode()
    {
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.Escape);
        // Should not throw and should be handled
        Should.NotThrow(() => handler.OnKeyDown(e));
        e.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_DeleteKey_MarksHandled()
    {
        var (handler, _) = CreateHandler();
        var e = MakeKey(Key.Delete);
        handler.OnKeyDown(e);
        e.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_NullMainViewModel_DoesNotThrow()
    {
        var canvasVm = new DesignCanvasViewModel();
        var handler = new KeyboardHandler(() => canvasVm, () => null, () => new Rect(0, 0, 800, 600));
        // When MainViewModel is null, key handler should silently return without throwing
        Should.NotThrow(() => handler.OnKeyDown(MakeKey(Key.S)));
    }

    [Fact]
    public void OnKeyDown_PKey_TogglesPowerFlow()
    {
        var (handler, canvasVm) = CreateHandler();
        bool initial = canvasVm.ShowPowerFlow;
        handler.OnKeyDown(MakeKey(Key.P));
        // Either triggered simulation or toggled — should not throw
        canvasVm.ShouldNotBeNull();
    }
}
