using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
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
[Collection("AvaloniaGlobalHook")]
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
        // Precondition, hard-asserted: the collapse must have reached ClientSize, which is
        // what EnforceRequestedSize inspects (on X11 the collapse arrives via HandleResized,
        // which updates ClientSize itself — this propagation is a headless artifact).
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
        // Precondition, hard-asserted: the window reached its requested size before the race.
        PumpUntilClientSize(window, RequestedWidth, RequestedHeight);

        window.Width = 10;   // collapse arrives after Opened but before the posted enforcement
        window.Height = 20;
        // The guard posted its two passes at Loaded/Background priority during Opened; headless
        // propagates the collapse to ClientSize ahead of Loaded-priority jobs, so the first
        // pumped RunJobs lets the posted passes observe the collapsed client size and restore
        // it (verified on Avalonia 11.3.x headless). If that priority ordering ever changes,
        // the bounded pump below fails honestly instead of flaking downstream.
        PumpUntilRestored(window, RequestedWidth, RequestedHeight);

        window.Width.ShouldBe(RequestedWidth);
        window.Height.ShouldBe(RequestedHeight);
        window.Close();
    }

    [AvaloniaFact]
    public void Initialize_ShowOwnedDialog_GuardAttachesViaOwnerAssignment()
    {
        // Exercises the real production wiring (OwnerProperty class handler), not Attach
        // directly: Avalonia assigns Owner during Show(owner), before OnOpened.
        DialogSizeGuard.ResetForTesting();
        DialogSizeGuard.Initialize();
        try
        {
            var owner = new Window { Width = 800, Height = 600 };
            owner.Show();

            var dialog = new Window { Width = RequestedWidth, Height = RequestedHeight, CanResize = false };
            dialog.Show(owner);

            // Observable proof the guard attached: the min-size fallback ran.
            dialog.MinWidth.ShouldBe(RequestedWidth);
            dialog.MinHeight.ShouldBe(RequestedHeight);

            dialog.Close();
            owner.Close();
        }
        finally
        {
            // Drain enforcement passes the guard posted for the now-closed windows: the
            // headless dispatcher queue is shared process-wide, so leftover callbacks would
            // fire inside an unrelated later test's RunJobs().
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

            // Resizable, so no min-size fallback clamps the client size: setting Width/Height
            // below the requested size really shrinks ClientSize, mimicking the platform
            // collapse the posted enforcement must repair.
            var dialog = new Window { Width = RequestedWidth, Height = RequestedHeight };
            _ = dialog.ShowDialog(owner); // completes when the dialog closes; result unused here

            dialog.Width = 10;   // simulate the late X11 ConfigureNotify collapsing the window
            dialog.Height = 20;
            // Bounded pump (render-timer tick + RunJobs): the posted passes must observe the
            // collapsed ClientSize — a plain RunJobs() can starve under full-suite load.
            PumpUntilRestored(dialog, RequestedWidth, RequestedHeight);

            dialog.Width.ShouldBe(RequestedWidth);
            dialog.Height.ShouldBe(RequestedHeight);

            dialog.Close();
            owner.Close();
        }
        finally
        {
            // Drain enforcement passes the guard posted for the now-closed windows: the
            // headless dispatcher queue is shared process-wide, so leftover callbacks would
            // fire inside an unrelated later test's RunJobs().
            Dispatcher.UIThread.RunJobs();
            DialogSizeGuard.ResetForTesting();
        }
    }

    /// <summary>
    /// Pumps the headless layout/render pipeline until <paramref name="window"/>'s ClientSize
    /// reaches the given size. Width/Height propagate to ClientSize asynchronously through
    /// layout; the bounded loop plus hard assert turns a propagation failure into an honest
    /// precondition failure instead of a downstream flake.
    /// </summary>
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

    /// <summary>
    /// Pumps the headless layout/render pipeline until the window's Width/Height properties are
    /// back at the requested size — i.e. until the guard's posted enforcement passes have run
    /// and restored them. Hard-asserts on exhaustion so a missed enforcement fails here instead
    /// of surfacing as a downstream flake.
    /// </summary>
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

    /// <summary>
    /// Runs one pump iteration over every dispatcher that can carry layout/render work. Under
    /// the headless xunit runner's default PerTest isolation, the MediaContext that layout
    /// invalidations flow into can stay bound to a dispatcher from an earlier scope — then
    /// <see cref="Dispatcher.UIThread"/> is a different instance and a plain
    /// <c>UIThread.RunJobs()</c> never drives layout or rendering at all. Pump the
    /// MediaContext's dispatcher first (layout propagates the collapse to ClientSize), then
    /// the UI thread's (the guard's posted passes observe it).
    /// </summary>
    private static void RunLayoutAndDispatcherJobs()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (GetMediaContextDispatcher() is { } mc && !ReferenceEquals(mc, Dispatcher.UIThread))
            mc.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The dispatcher the current MediaContext schedules render/layout work on.</summary>
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
