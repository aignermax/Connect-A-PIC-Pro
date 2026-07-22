using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Threading;

namespace CAP.Avalonia.Services.DialogSizing;

/// <summary>
/// Central workaround for issue #697: on Linux (Avalonia 11.2.x X11 backend) small windows
/// alternate between their requested size and a collapsed ("tiny") size on every other
/// opening. A late <c>ConfigureNotify</c> during the show handshake makes Avalonia believe
/// the user resized the window, which shrinks it and resets <see cref="Window.SizeToContent"/>
/// to <see cref="SizeToContent.Manual"/> (see AvaloniaUI/Avalonia#12581).
///
/// The guard is installed once via <see cref="Initialize"/> and then watches every
/// <see cref="Window"/> that receives an owner: it captures the size requested at show time,
/// adds a minimum-size fallback for non-resizable dialogs, and shortly after
/// <see cref="Window.Opened"/> re-applies the requested size / <see cref="Window.SizeToContent"/>
/// if the platform collapsed the window. Windows/macOS are unaffected in practice because the
/// enforcement only acts when the actual size ended up *smaller* than the size requested at
/// show time.
/// </summary>
public static class DialogSizeGuard
{
    /// <summary>Deviation below the requested size that counts as "collapsed".</summary>
    internal const double CollapseTolerancePx = 4.0;

    /// <summary>Fallback minimum height for dialogs whose height is content-sized.</summary>
    internal const double FallbackMinHeightPx = 80.0;

    /// <summary>Nudge used to force a platform resize when the property value is already correct.</summary>
    private const double NudgePx = 1.0;

    private static IDisposable? _classHandler;

    /// <summary>
    /// Installs the guard for the whole application. Call once during application start-up;
    /// subsequent calls are no-ops.
    /// </summary>
    public static void Initialize()
    {
        if (_classHandler != null)
            return;

        // The trigger must be the Owner assignment, not IsVisible: Avalonia 11.2.1 sets
        // IsVisible = true before Owner = owner in Window.ShowCore/ShowDialog (Window.cs
        // lines 719/739 and 800/822 on the release/11.2.1 tag), so an IsVisible class
        // handler always sees Owner == null and would never attach. Owner is still assigned
        // before OnOpened, so the Opened subscription in Attach fires as intended.
        _classHandler = WindowBase.OwnerProperty.Changed.AddClassHandler<Window>(OnOwnerChanged);
    }

    /// <summary>
    /// Uninstalls the class handler. Test seam (InternalsVisibleTo UnitTests) so wiring
    /// tests stay order-independent and the guard does not leak into unrelated tests.
    /// </summary>
    internal static void ResetForTesting()
    {
        _classHandler?.Dispose();
        _classHandler = null;
    }

    private static void OnOwnerChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        // Only windows that just received an owner (dialogs and tool windows opened via
        // Show(owner)/ShowDialog(owner)) are guarded; the Owner = null assignments on close
        // are ignored. The unowned main window may legitimately be clamped below its requested
        // 1800x900 by the window manager on small screens and must not be re-enlarged.
        if (e.NewValue is WindowBase)
            Attach(window);
    }

    /// <summary>
    /// Guards a single window that is about to be shown: captures the size it requested and
    /// re-enforces it shortly after <see cref="Window.Opened"/>. Runs once per show — the
    /// protected <see cref="WindowBase.Owner"/> setter is only assigned a non-null value
    /// inside Avalonia's show path, always before <c>OnOpened</c> — and the Opened handler
    /// unsubscribes itself, so hiding and re-showing a window simply re-attaches with a
    /// freshly captured size. Exposed for tests; production code relies on
    /// <see cref="Initialize"/> instead of calling this per dialog.
    /// </summary>
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
            // Two passes: right after layout of the opened window, and once more after any
            // pending platform (ConfigureNotify) events have been processed.
            Dispatcher.UIThread.Post(Enforce, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(Enforce, DispatcherPriority.Background);
        };
        window.Opened += onOpened;
    }

    /// <summary>
    /// Fallback so a collapsed window can never render below a usable size. Only applied to
    /// non-resizable windows without an explicit minimum, so resizable windows (which choose
    /// their own <c>MinWidth</c>/<c>MinHeight</c>) keep their configured resize range.
    /// </summary>
    internal static void ApplyMinimumSizeFallback(
        Window window, double requestedWidth, double requestedHeight, SizeToContent requestedSizeToContent)
    {
        if (window.CanResize)
            return;

        // Never demand more than the owner screen's work area: on very small screens the
        // window manager must stay able to clamp the dialog below its requested size.
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

    /// <summary>
    /// Re-applies the size the window requested at show time if the platform collapsed it.
    /// Deliberately one-directional: a window that ended up larger (e.g. user- or WM-grown)
    /// is left alone, which keeps this safe on macOS/Windows where the bug does not occur.
    /// Runs only in the two passes right after <see cref="Window.Opened"/> — a resizable
    /// window legitimately shrunk by the user or a tiling WM within that first frame is
    /// nudged back once, intentionally; enforcement then stops and never fights later resizes.
    /// </summary>
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

    /// <summary>Whether the actual size fell more than the tolerance below the requested size.</summary>
    internal static bool IsCollapsed(double requested, double actual)
        => IsFixed(requested) && actual < requested - CollapseTolerancePx;

    /// <summary>The owner screen's work area in DIPs; null when no screen is available (headless).</summary>
    private static (double Width, double Height)? GetOwnerWorkArea(Window window)
    {
        // TopLevel.Screens (nullable) rather than WindowBase.Screens, which throws when the
        // backend reports no screen implementation.
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

    /// <summary>
    /// Sets <paramref name="property"/> to <paramref name="requested"/>. If the property already
    /// holds that value (only the platform client size collapsed), nudges it first so the property
    /// change actually reaches the platform window.
    /// </summary>
    private static void ForceDimension(Window window, StyledProperty<double> property, double requested)
    {
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
