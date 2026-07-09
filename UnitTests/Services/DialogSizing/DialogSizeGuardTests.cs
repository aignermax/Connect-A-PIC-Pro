using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services.DialogSizing;
using Shouldly;
using Xunit;

namespace UnitTests.Services.DialogSizing;

/// <summary>
/// Tests for <see cref="DialogSizeGuard"/>, the central workaround for issue #697
/// (small dialogs rendering collapsed on every other opening on Linux/X11).
/// </summary>
public class DialogSizeGuardTests
{
    private const double RequestedWidth = 480;
    private const double RequestedHeight = 180;

    [Theory]
    [InlineData(480, 10, true)]   // collapsed far below requested
    [InlineData(480, 475, true)]  // just outside tolerance
    [InlineData(480, 477, false)] // within tolerance
    [InlineData(480, 480, false)] // exact
    [InlineData(480, 900, false)] // grown — must be left alone
    public void IsCollapsed_DetectsOnlySizesBelowRequested(double requested, double actual, bool expected)
    {
        DialogSizeGuard.IsCollapsed(requested, actual).ShouldBe(expected);
    }

    [Fact]
    public void IsCollapsed_UnsetRequestedSize_IsNeverCollapsed()
    {
        DialogSizeGuard.IsCollapsed(double.NaN, 1).ShouldBeFalse();
        DialogSizeGuard.IsCollapsed(0, 1).ShouldBeFalse();
    }

    [AvaloniaFact]
    public void MinimumSizeFallback_NonResizableFixedSizeDialog_GetsRequestedSizeAsMinimum()
    {
        var window = new Window { Width = RequestedWidth, Height = RequestedHeight, CanResize = false };

        DialogSizeGuard.ApplyMinimumSizeFallback(window, RequestedWidth, RequestedHeight, SizeToContent.Manual);

        window.MinWidth.ShouldBe(RequestedWidth);
        window.MinHeight.ShouldBe(RequestedHeight);
    }

    [AvaloniaFact]
    public void MinimumSizeFallback_SizeToContentHeightDialog_GetsFallbackMinHeight()
    {
        // Mirrors MessageBoxService.ShowChoicePromptAsync (Width fixed, height content-sized).
        var window = new Window { Width = RequestedWidth, SizeToContent = SizeToContent.Height, CanResize = false };

        DialogSizeGuard.ApplyMinimumSizeFallback(window, RequestedWidth, double.NaN, SizeToContent.Height);

        window.MinWidth.ShouldBe(RequestedWidth);
        window.MinHeight.ShouldBe(DialogSizeGuard.FallbackMinHeightPx);
    }

    [AvaloniaFact]
    public void MinimumSizeFallback_ResizableWindow_IsLeftUntouched()
    {
        // Mirrors ProcessManagementWindow, which defines its own smaller MinWidth/MinHeight.
        var window = new Window { Width = 820, Height = 680, MinWidth = 500, MinHeight = 400 };

        DialogSizeGuard.ApplyMinimumSizeFallback(window, 820, 680, SizeToContent.Manual);

        window.MinWidth.ShouldBe(500);
        window.MinHeight.ShouldBe(400);
    }

    [AvaloniaFact]
    public void Enforce_CollapsedWindow_IsRestoredToRequestedSize()
    {
        var window = new Window { Width = RequestedWidth, Height = RequestedHeight };
        window.Show();
        window.Width = 10;   // simulate the platform collapsing the window after opening
        window.Height = 20;
        Dispatcher.UIThread.RunJobs();

        DialogSizeGuard.EnforceRequestedSize(window, RequestedWidth, RequestedHeight, SizeToContent.Manual);
        Dispatcher.UIThread.RunJobs();

        window.Width.ShouldBe(RequestedWidth);
        window.Height.ShouldBe(RequestedHeight);
        window.Close();
    }

    [AvaloniaFact]
    public void Enforce_WindowGrownBeyondRequestedSize_IsLeftAlone()
    {
        var window = new Window { Width = RequestedWidth, Height = RequestedHeight };
        window.Show();
        window.Width = 900;  // e.g. user or window manager grew the window
        window.Height = 700;
        Dispatcher.UIThread.RunJobs();

        DialogSizeGuard.EnforceRequestedSize(window, RequestedWidth, RequestedHeight, SizeToContent.Manual);
        Dispatcher.UIThread.RunJobs();

        window.Width.ShouldBe(900);
        window.Height.ShouldBe(700);
        window.Close();
    }

    [AvaloniaFact]
    public void Enforce_SizeToContentResetToManual_IsRestored()
    {
        var window = new Window { Width = RequestedWidth, SizeToContent = SizeToContent.Height };
        window.Show();
        window.SizeToContent = SizeToContent.Manual; // simulate the X11 ConfigureNotify race
        Dispatcher.UIThread.RunJobs();

        DialogSizeGuard.EnforceRequestedSize(window, RequestedWidth, double.NaN, SizeToContent.Height);

        window.SizeToContent.ShouldBe(SizeToContent.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void Attach_ThenOpen_KeepsRequestedSize()
    {
        var window = new Window { Width = RequestedWidth, Height = RequestedHeight, CanResize = false };
        DialogSizeGuard.Attach(window);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Width.ShouldBe(RequestedWidth);
        window.Height.ShouldBe(RequestedHeight);
        window.MinWidth.ShouldBe(RequestedWidth);
        window.Close();
    }

    [AvaloniaFact]
    public void Attach_WindowCollapsedRightAfterOpen_IsRestoredByPostedEnforcement()
    {
        var window = new Window { Width = RequestedWidth, Height = RequestedHeight };
        DialogSizeGuard.Attach(window);

        window.Show();
        window.Width = 10;   // collapse arrives after Opened but before the posted enforcement
        window.Height = 20;
        Dispatcher.UIThread.RunJobs();

        window.Width.ShouldBe(RequestedWidth);
        window.Height.ShouldBe(RequestedHeight);
        window.Close();
    }
}
