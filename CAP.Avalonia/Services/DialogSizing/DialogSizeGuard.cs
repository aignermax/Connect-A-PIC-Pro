using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Threading;

namespace CAP.Avalonia.Services.DialogSizing;

// Workaround for issue #697 (AvaloniaUI/Avalonia#12581): a late X11 ConfigureNotify makes
// Avalonia treat owned dialogs as user-resized and collapse them on every other opening.
public static class DialogSizeGuard
{
    internal const double CollapseTolerancePx = 4.0;
    internal const double FallbackMinHeightPx = 80.0;
    private const double NudgePx = 1.0;

    private static IDisposable? _classHandler;

    public static void Initialize()
    {
        if (_classHandler != null)
            return;

        // Trigger on the Owner assignment, not IsVisible: Avalonia sets IsVisible before
        // Owner in ShowCore/ShowDialog, so an IsVisible handler would never attach.
        _classHandler = WindowBase.OwnerProperty.Changed.AddClassHandler<Window>(OnOwnerChanged);
    }

    internal static void ResetForTesting()
    {
        _classHandler?.Dispose();
        _classHandler = null;
    }

    private static void OnOwnerChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        // The unowned main window may legitimately be clamped by the WM and must not be re-enlarged.
        if (e.NewValue is WindowBase)
            Attach(window);
    }

    public static void Attach(Window window)
    {
        var requestedWidth = window.Width;
        var requestedHeight = window.Height;
        var requestedSizeToContent = window.SizeToContent;

        ApplyMinimumSizeFallback(window, requestedWidth, requestedHeight, requestedSizeToContent);

        void Enforce() => EnforceRequestedSize(window, requestedWidth, requestedHeight, requestedSizeToContent);

        EventHandler? onOpened = null;
        onOpened = (_, _) =>
        {
            window.Opened -= onOpened;
            // Two passes so the late ConfigureNotify is caught after pending platform events.
            Dispatcher.UIThread.Post(Enforce, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(Enforce, DispatcherPriority.Background);
        };
        window.Opened += onOpened;
    }

    // Fallback so a collapsed dialog stays usable; resizable windows keep their own minimum.
    internal static void ApplyMinimumSizeFallback(
        Window window, double requestedWidth, double requestedHeight, SizeToContent requestedSizeToContent)
    {
        if (window.CanResize)
            return;

        // The WM must stay able to clamp the dialog on very small screens.
        var workArea = GetOwnerWorkArea(window);

        if (window.MinWidth <= 0 && !AffectsWidth(requestedSizeToContent) && IsFixed(requestedWidth))
            window.MinWidth = CapAtWorkArea(requestedWidth, workArea?.Width);

        if (window.MinHeight <= 0)
        {
            if (!AffectsHeight(requestedSizeToContent) && IsFixed(requestedHeight))
                window.MinHeight = CapAtWorkArea(requestedHeight, workArea?.Height);
            else if (AffectsHeight(requestedSizeToContent))
                window.MinHeight = CapAtWorkArea(FallbackMinHeightPx, workArea?.Height);
        }
    }

    // One-directional on purpose: windows grown by user or WM are left alone, which keeps
    // this safe on Windows/macOS where the bug does not occur.
    internal static void EnforceRequestedSize(
        Window window, double requestedWidth, double requestedHeight, SizeToContent requestedSizeToContent)
    {
        // The X11 race resets SizeToContent to Manual; restoring it re-measures from content.
        if (requestedSizeToContent != SizeToContent.Manual && window.SizeToContent == SizeToContent.Manual)
            window.SizeToContent = requestedSizeToContent;

        if (!AffectsWidth(requestedSizeToContent) && IsCollapsed(requestedWidth, window.ClientSize.Width))
            ForceDimension(window, Window.WidthProperty, requestedWidth);

        if (!AffectsHeight(requestedSizeToContent) && IsCollapsed(requestedHeight, window.ClientSize.Height))
            ForceDimension(window, Window.HeightProperty, requestedHeight);
    }

    internal static bool IsCollapsed(double requested, double actual)
        => IsFixed(requested) && actual < requested - CollapseTolerancePx;

    private static (double Width, double Height)? GetOwnerWorkArea(Window window)
    {
        // TopLevel.Screens is nullable; WindowBase.Screens throws when the backend has no screen impl.
        var screens = ((TopLevel)window).Screens;
        if (screens == null)
            return null;

        var screen = window.Owner is { } owner
            ? screens.ScreenFromWindow(owner)
            : screens.Primary;
        if (screen == null)
            return null;

        return (screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
    }

    private static double CapAtWorkArea(double value, double? workArea)
        => workArea.HasValue ? Math.Min(value, workArea.Value) : value;

    private static void ForceDimension(Window window, StyledProperty<double> property, double requested)
    {
        // Nudge first when the property already holds the requested value, otherwise no
        // change notification reaches the platform window.
        if (Math.Abs(window.GetValue(property) - requested) < double.Epsilon)
            window.SetValue(property, requested - NudgePx);
        window.SetValue(property, requested);
    }

    private static bool IsFixed(double value) => !double.IsNaN(value) && value > 0;

    private static bool AffectsWidth(SizeToContent sizeToContent)
        => sizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;

    private static bool AffectsHeight(SizeToContent sizeToContent)
        => sizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
}
