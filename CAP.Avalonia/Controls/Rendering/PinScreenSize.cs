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
}
