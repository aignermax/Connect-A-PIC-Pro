using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services.DialogSizing;
using Shouldly;
using Xunit;

namespace UnitTests.Services.DialogSizing;

[Collection("AvaloniaGlobalHook")]
public class DialogSizeGuardTests
{
    private const double RequestedWidth = 480;
    private const double RequestedHeight = 180;

    [Theory]
    [InlineData(480, 10, true)]
    [InlineData(480, 475, true)]
    [InlineData(480, 477, false)]
    [InlineData(480, 480, false)]
    [InlineData(480, 900, false)]
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
        // Shape mirrors MessageBoxService.ShowChoicePromptAsync.
        var window = new Window { Width = RequestedWidth, SizeToContent = SizeToContent.Height, CanResize = false };

        DialogSizeGuard.ApplyMinimumSizeFallback(window, RequestedWidth, double.NaN, SizeToContent.Height);

        window.MinWidth.ShouldBe(RequestedWidth);
        window.MinHeight.ShouldBe(DialogSizeGuard.FallbackMinHeightPx);
    }

    [AvaloniaFact]
    public void MinimumSizeFallback_ResizableWindow_IsLeftUntouched()
    {
        // Shape mirrors ProcessManagementWindow.
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
        window.Width = 10;
        window.Height = 20;
        PumpUntilClientSize(window, 10, 20);

        DialogSizeGuard.EnforceRequestedSize(window, RequestedWidth, RequestedHeight, SizeToContent.Manual);
        PumpUntilClientSize(window, RequestedWidth, RequestedHeight);

        window.Width.ShouldBe(RequestedWidth);
        window.Height.ShouldBe(RequestedHeight);
        window.Close();
    }

    [AvaloniaFact]
    public void Enforce_WindowGrownBeyondRequestedSize_IsLeftAlone()
    {
        var window = new Window { Width = RequestedWidth, Height = RequestedHeight };
        window.Show();
        window.Width = 900;
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
        window.SizeToContent = SizeToContent.Manual;
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
        PumpUntilClientSize(window, RequestedWidth, RequestedHeight);

        window.Width = 10;
        window.Height = 20;
        PumpUntilRestored(window, RequestedWidth, RequestedHeight);

        window.Width.ShouldBe(RequestedWidth);
        window.Height.ShouldBe(RequestedHeight);
        window.Close();
    }

    [AvaloniaFact]
    public void Initialize_ShowOwnedDialog_GuardAttachesViaOwnerAssignment()
    {
        // Goes through the real Show(owner) wiring, not Attach directly.
        DialogSizeGuard.ResetForTesting();
        DialogSizeGuard.Initialize();
        try
        {
            var owner = new Window { Width = 800, Height = 600 };
            owner.Show();

            var dialog = new Window { Width = RequestedWidth, Height = RequestedHeight, CanResize = false };
            dialog.Show(owner);

            // The min-size fallback ran only if the guard attached.
            dialog.MinWidth.ShouldBe(RequestedWidth);
            dialog.MinHeight.ShouldBe(RequestedHeight);

            dialog.Close();
            owner.Close();
        }
        finally
        {
            // Drain the shared headless dispatcher so posted passes can't leak into later tests.
            Dispatcher.UIThread.RunJobs();
            DialogSizeGuard.ResetForTesting();
        }
    }

    [AvaloniaFact]
    public void Initialize_OwnedDialogCollapsedRightAfterOpen_IsRestoredByPostedEnforcement()
    {
        DialogSizeGuard.ResetForTesting();
        DialogSizeGuard.Initialize();
        try
        {
            var owner = new Window { Width = 800, Height = 600 };
            owner.Show();

            // Resizable so no min-size fallback clamps the shrink.
            var dialog = new Window { Width = RequestedWidth, Height = RequestedHeight };
            _ = dialog.ShowDialog(owner);

            dialog.Width = 10;
            dialog.Height = 20;
            PumpUntilRestored(dialog, RequestedWidth, RequestedHeight);

            dialog.Width.ShouldBe(RequestedWidth);
            dialog.Height.ShouldBe(RequestedHeight);

            dialog.Close();
            owner.Close();
        }
        finally
        {
            // Drain the shared headless dispatcher so posted passes can't leak into later tests.
            Dispatcher.UIThread.RunJobs();
            DialogSizeGuard.ResetForTesting();
        }
    }

    // Width/Height reach ClientSize only asynchronously through layout in headless; the hard
    // assert turns a propagation failure into an honest precondition failure instead of a flake.
    private static void PumpUntilClientSize(Window window, double width, double height)
    {
        for (var i = 0; i < 50; i++)
        {
            if (window.ClientSize.Width == width && window.ClientSize.Height == height)
                return;
            RunLayoutAndDispatcherJobs();
            Thread.Sleep(5);
        }

        window.ClientSize.Width.ShouldBe(width,
            $"ClientSize did not converge — IsVisible={window.IsVisible} IsActive={window.IsActive} "
            + $"IsMeasureValid={window.IsMeasureValid} IsArrangeValid={window.IsArrangeValid} "
            + $"Bounds={window.Bounds.Size} Desired={window.DesiredSize} State={window.WindowState}");
        window.ClientSize.Height.ShouldBe(height);
    }

    private static void PumpUntilRestored(Window window, double width, double height)
    {
        for (var i = 0; i < 50; i++)
        {
            if (window.Width == width && window.Height == height)
                return;
            RunLayoutAndDispatcherJobs();
            Thread.Sleep(5);
        }

        window.Width.ShouldBe(width, "posted enforcement did not restore the window");
        window.Height.ShouldBe(height);
    }

    // Under headless PerTest isolation the MediaContext can stay bound to a dispatcher from an
    // earlier scope, and then UIThread.RunJobs() never drives layout or rendering — pump both.
    private static void RunLayoutAndDispatcherJobs()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (GetMediaContextDispatcher() is { } mc && !ReferenceEquals(mc, Dispatcher.UIThread))
            mc.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static Dispatcher? GetMediaContextDispatcher()
    {
        try
        {
            var mcType = typeof(Dispatcher).Assembly.GetType("Avalonia.Media.MediaContext");
            var instance = mcType
                ?.GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(null);
            return mcType
                ?.GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(instance) as Dispatcher;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
