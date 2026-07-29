namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Screen-space size cap shared by pin glyph rendering, the Connect-mode hover/snap
/// highlight, and pin hit-testing, so a pin never grows to fill the screen at high zoom
/// while still shrinking proportionally with the world at low zoom (unchanged below the cap).
/// Mirrors the pattern <see cref="Canvas.CutTool.CutToolOverlayRenderer"/> already uses for its
/// screen-constant markers, generalized with a world-space floor: those markers have no
/// inherent size of their own (always <c>ConstPx / zoom</c>), while a pin's world size should
/// still shrink when zooming out.
/// </summary>
public static class PinScreenSize
{
    /// <summary>Maximum on-screen pin radius/half-extent in pixels, regardless of zoom.</summary>
    public const double MaxRadiusPx = 16.0;

    /// <summary>
    /// Maximum on-screen label font size in pixels, regardless of zoom — the same
    /// screen-constant-cap idea as <see cref="MaxRadiusPx"/>, applied to canvas text labels
    /// (component names, waveguide length/loss labels) so they stop growing once zooming in
    /// would otherwise let them dominate the canvas.
    /// </summary>
    public const double MaxLabelFontSizePx = 14.0;

    /// <summary>
    /// Smallest on-screen font size in pixels a label is ever drawn at. Below this, zooming out
    /// further would only produce illegible shrinking text, so <see cref="ClampWorldFontSize"/>
    /// floors the effective size here instead — the label never disappears (a hovered/selected
    /// label especially must stay visible; density is instead regulated by overlap-based
    /// thinning, not by hiding individual labels).
    /// </summary>
    public const double MinLabelFontSizePx = 6.0;

    /// <summary>
    /// Caps a world-space size so that, after the canvas' zoom transform, it never exceeds
    /// <see cref="MaxRadiusPx"/> screen pixels: <c>min(worldSize, MaxRadiusPx / zoom)</c>.
    /// Below the cap the size is returned unchanged, so zooming out still shrinks the pin
    /// with the rest of the world exactly as before.
    /// </summary>
    /// <param name="worldSize">The size (radius, half-extent, or length) in world units (µm).</param>
    /// <param name="zoom">Current canvas zoom factor (screen pixels per world unit).</param>
    public static double CapWorldRadius(double worldSize, double zoom)
    {
        double safeZoom = zoom <= 0 ? 1.0 : zoom;
        return Math.Min(worldSize, MaxRadiusPx / safeZoom);
    }

    /// <summary>
    /// Clamps a world-space font size so its actual on-screen size — <c>worldFontSize * zoom</c>
    /// — never leaves <c>[MinLabelFontSizePx, MaxLabelFontSizePx]</c>, then converts the clamped
    /// screen size back to world units for the caller to pass to <c>FormattedText</c>. Unlike a
    /// one-sided cap, a label never becomes illegible or invisible at any zoom: it stops growing
    /// at <see cref="MaxLabelFontSizePx"/> zooming in, and stops shrinking at
    /// <see cref="MinLabelFontSizePx"/> zooming out instead of vanishing. Canvas-wide clutter at
    /// low zoom is regulated separately, by overlap-based thinning (see
    /// <c>LabelDeclutter.LabelOverlapResolver</c>) — a hovered or selected label especially must
    /// never disappear just because the user zoomed out.
    /// </summary>
    /// <param name="worldFontSize">The label's font size in world units (µm-scaled points).</param>
    /// <param name="zoom">Current canvas zoom factor (screen pixels per world unit).</param>
    public static double ClampWorldFontSize(double worldFontSize, double zoom)
    {
        double safeZoom = zoom <= 0 ? 1.0 : zoom;
        double screenPx = Math.Clamp(worldFontSize * safeZoom, MinLabelFontSizePx, MaxLabelFontSizePx);
        return screenPx / safeZoom;
    }
}
