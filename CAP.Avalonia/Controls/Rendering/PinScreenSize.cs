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
    /// Smallest on-screen font size in pixels still considered legible. Below this, zooming out
    /// further only produces illegible text, so callers should hide the label entirely — see
    /// <see cref="IsLabelReadable"/> — rather than keep drawing shrinking noise.
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
    /// Caps a world-space font size the same way <see cref="CapWorldRadius"/> caps a pin
    /// radius: <c>min(worldFontSize, MaxLabelFontSizePx / zoom)</c>. Below the cap the size is
    /// returned unchanged, so a label still shrinks proportionally with the world when zooming
    /// out — only zooming in is capped, so text never grows to dominate the screen.
    /// </summary>
    /// <param name="worldFontSize">The label's font size in world units (µm-scaled points).</param>
    /// <param name="zoom">Current canvas zoom factor (screen pixels per world unit).</param>
    public static double CapWorldFontSize(double worldFontSize, double zoom)
    {
        double safeZoom = zoom <= 0 ? 1.0 : zoom;
        return Math.Min(worldFontSize, MaxLabelFontSizePx / safeZoom);
    }

    /// <summary>
    /// Whether a label at <paramref name="worldFontSize"/> is still legible at
    /// <paramref name="zoom"/>: its actual on-screen size — <c>worldFontSize</c> capped by
    /// <see cref="CapWorldFontSize"/>, then scaled back to screen pixels — must reach
    /// <see cref="MinLabelFontSizePx"/>. The cap only ever shrinks large text, so this still
    /// correctly goes false once zooming out makes the (uncapped, proportionally shrinking)
    /// text too small to read. Callers should skip drawing the label entirely rather than
    /// render illegible text.
    /// </summary>
    /// <param name="worldFontSize">The label's font size in world units (µm-scaled points).</param>
    /// <param name="zoom">Current canvas zoom factor (screen pixels per world unit).</param>
    public static bool IsLabelReadable(double worldFontSize, double zoom)
    {
        double safeZoom = zoom <= 0 ? 1.0 : zoom;
        double effectiveScreenPx = CapWorldFontSize(worldFontSize, zoom) * safeZoom;
        return effectiveScreenPx >= MinLabelFontSizePx;
    }
}
