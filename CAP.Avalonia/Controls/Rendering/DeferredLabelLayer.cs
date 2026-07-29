using Avalonia;
using Avalonia.Media;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Deferred topmost text layer. The geometry passes (connections, then components) ENQUEUE
/// their free-floating text labels here instead of drawing them inline, and
/// <see cref="DesignCanvas"/> flushes the queue in one pass after all component bodies and
/// connection lines are drawn. This is required because each component is painted atomically:
/// without deferral, a later component's body fill covers an earlier component's name label
/// (and every connection label), so labels ended up underneath neighboring components.
/// <para>
/// Every flushed label is drawn twice: first in a near-black "halo" offset by one screen
/// pixel, then in its original foreground on top — the cheap outline that keeps light label
/// text readable on whitish GDS previews and selected connections without a background box.
/// The halo is only a thin offset copy, so on the dark canvas background it is invisible and
/// the label reads exactly as before.
/// </para>
/// </summary>
public sealed class DeferredLabelLayer
{
    /// <summary>Halo offset in SCREEN pixels, converted to world units at flush time so it
    /// stays a constant 1 px at any zoom — the same screen-constant idea as the label
    /// font-size clamp in <see cref="PinScreenSize"/>.</summary>
    internal const double HaloOffsetPx = 1.0;

    /// <summary>Near-black halo brush: dark enough to separate light label text from whitish
    /// component fills. Shared by all labels — one static brush, zero per-frame allocation.</summary>
    private static readonly IBrush HaloBrush = new SolidColorBrush(Color.FromArgb(255, 16, 16, 20));

    private readonly List<QueuedLabel> _pending = new();

    /// <summary>A label waiting for the topmost pass. The foreground travels with the entry
    /// because <see cref="FormattedText"/> bakes its brush at creation; the flush re-applies
    /// it (and the halo brush) via <see cref="FormattedText.SetForegroundBrush"/>, so the same
    /// shaped layout is reused for both the halo and the visible copy — no second text
    /// measurement, the expensive part of building a <see cref="FormattedText"/>.</summary>
    public readonly record struct QueuedLabel(FormattedText Text, IBrush Foreground, Point Origin);

    /// <summary>One concrete draw call produced by the flush — text, brush to apply, origin.</summary>
    internal readonly record struct PlannedDraw(FormattedText Text, IBrush Brush, Point Origin);

    /// <summary>Labels queued so far this frame (the geometry passes add, the flush consumes).</summary>
    public IReadOnlyList<QueuedLabel> Pending => _pending;

    /// <summary>Queues a label for the topmost pass instead of drawing it immediately.</summary>
    public void Enqueue(FormattedText text, IBrush foreground, Point origin)
        => _pending.Add(new QueuedLabel(text, foreground, origin));

    /// <summary>
    /// Draws every queued label (halo copy, then foreground copy) and empties the queue.
    /// Must be called AFTER all component/connection geometry passes so no body fill or
    /// connection line can paint over a label.
    /// </summary>
    public void Flush(DrawingContext context, double zoom)
    {
        foreach (var draw in BuildDrawOperations(zoom))
        {
            draw.Text.SetForegroundBrush(draw.Brush);
            context.DrawText(draw.Text, draw.Origin);
        }
        _pending.Clear();
    }

    /// <summary>
    /// Expands each queued label into its two draw operations: halo first (dark, offset one
    /// screen pixel down-right — never up/left, so the halo can never shift the readable glyph
    /// position), then the label itself at its exact origin with its original foreground.
    /// Split out from <see cref="Flush"/> so tests can assert the halo/foreground pairing
    /// without a <see cref="DrawingContext"/>, whose constructor is internal to Avalonia and
    /// therefore cannot be faked from the test assembly.
    /// </summary>
    internal IReadOnlyList<PlannedDraw> BuildDrawOperations(double zoom)
    {
        double safeZoom = zoom <= 0 ? 1.0 : zoom;
        double offset = HaloOffsetPx / safeZoom;
        var draws = new List<PlannedDraw>(_pending.Count * 2);
        foreach (var (text, foreground, origin) in _pending)
        {
            draws.Add(new PlannedDraw(text, HaloBrush, new Point(origin.X + offset, origin.Y + offset)));
            draws.Add(new PlannedDraw(text, foreground, origin));
        }
        return draws;
    }
}
