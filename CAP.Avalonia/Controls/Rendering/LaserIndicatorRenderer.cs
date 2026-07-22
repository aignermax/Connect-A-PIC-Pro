using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.ComponentHelpers;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Draws the per-coupler laser on/off indicator (#690) at each light-source
/// component. A coloured dot (wavelength colour) with rays = laser on (input);
/// a gray dot with a slash = laser off (the coupler is listen-only, an output).
/// Both states stay subtly visible at all times (brighter while a simulation
/// mode is active) so the toggle can never vanish under the cursor.
/// </summary>
internal static class LaserIndicatorRenderer
{
    private const double MinIconSize = 12;
    private const double MaxIconSize = 24;
    private const double IconSizeFactor = 0.25;
    private const double IconMargin = 4;

    private const byte SubtleAlpha = 140;
    private const byte ActiveAlpha = 235;
    private const double RayLengthFactor = 0.32;
    private const double HoverRingThickness = 1.5;
    private const double BackingDiscAlphaFactor = 0.75;
    private const double DotRadiusFactor = 0.45;
    private const double RayPenThickness = 1.2;
    private const double SlashPenThickness = 1.5;
    private const double SlashExtentFactor = 0.7071; // sin/cos 45°
    private static readonly Color BackingDisc = Color.FromRgb(30, 30, 30);
    private static readonly Color SlashGray = Color.FromRgb(220, 220, 220);

    private static readonly Color RedLaser = Color.FromRgb(255, 82, 82);
    private static readonly Color GreenLaser = Color.FromRgb(105, 240, 174);
    private static readonly Color BlueLaser = Color.FromRgb(68, 138, 255);
    private static readonly Color OffGray = Color.FromRgb(158, 158, 158);

    /// <summary>
    /// True when the component's laser icon should be drawn/clickable. Visible for
    /// every light source in both states: hiding the off icon outside simulation
    /// mode created a one-way trap — one click switched the laser off and the
    /// control vanished under the cursor with no on-canvas way back.
    /// </summary>
    /// <param name="comp">The component to test.</param>
    /// <param name="simulationActive">Whether a simulation overlay mode is active
    /// (kept for signature stability; both states render regardless).</param>
    public static bool IsIconVisible(ComponentViewModel comp, bool simulationActive)
    {
        return comp.LaserConfig != null;
    }

    /// <summary>Icon bounds in canvas coordinates (top-left corner of the component).</summary>
    /// <param name="comp">The component whose icon bounds to compute.</param>
    public static Rect CalculateIconBounds(ComponentViewModel comp)
    {
        double size = Math.Clamp(Math.Min(comp.Width, comp.Height) * IconSizeFactor, MinIconSize, MaxIconSize);
        return new Rect(comp.X + IconMargin, comp.Y + IconMargin, size, size);
    }

    /// <summary>Draws the laser indicator for a light-source component.</summary>
    /// <param name="context">Drawing context.</param>
    /// <param name="comp">The light-source component.</param>
    /// <param name="isHovered">Whether the pointer hovers the icon (draws a highlight ring).</param>
    /// <param name="simulationActive">Whether a simulation overlay mode is active.</param>
    public static void Draw(DrawingContext context, ComponentViewModel comp, bool isHovered, bool simulationActive)
    {
        if (!IsIconVisible(comp, simulationActive)) return;

        var cfg = comp.LaserConfig!;
        var bounds = CalculateIconBounds(comp);
        var center = bounds.Center;
        double radius = bounds.Width / 2;

        byte alpha = simulationActive ? ActiveAlpha : SubtleAlpha;
        var color = cfg.IsEnabled ? GetWavelengthColor(cfg.WavelengthNm) : OffGray;

        // Dark backing disc so the icon reads on any component fill.
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(
            (byte)(alpha * BackingDiscAlphaFactor), BackingDisc.R, BackingDisc.G, BackingDisc.B)),
            null, center, radius, radius);

        double dotRadius = radius * DotRadiusFactor;
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
            null, center, dotRadius, dotRadius);

        if (cfg.IsEnabled)
            DrawRays(context, center, radius, color, alpha);
        else
            DrawOffSlash(context, center, radius, alpha);

        if (isHovered)
            context.DrawEllipse(null, new Pen(Brushes.White, HoverRingThickness), center, radius, radius);
    }

    /// <summary>
    /// Maps a configured wavelength to its display colour via the simulation's own
    /// wavelength classification, so icon colour and simulated laser type can never
    /// disagree when new wavelengths are added.
    /// </summary>
    /// <param name="wavelengthNm">Wavelength in nanometers.</param>
    public static Color GetWavelengthColor(int wavelengthNm)
    {
        var laserType = Services.SimulationService.GetLaserTypeForWavelength(wavelengthNm);
        if (laserType == CAP_Core.ExternalPorts.LaserType.Green) return GreenLaser;
        if (laserType == CAP_Core.ExternalPorts.LaserType.Blue) return BlueLaser;
        return RedLaser;
    }

    private static void DrawRays(DrawingContext context, Point center, double radius, Color color, byte alpha)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), RayPenThickness);
        double inner = radius * (1 - RayLengthFactor);
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            context.DrawLine(pen,
                new Point(center.X + inner * cos, center.Y + inner * sin),
                new Point(center.X + radius * cos, center.Y + radius * sin));
        }
    }

    private static void DrawOffSlash(DrawingContext context, Point center, double radius, byte alpha)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, SlashGray.R, SlashGray.G, SlashGray.B)), SlashPenThickness);
        double d = radius * SlashExtentFactor;
        context.DrawLine(pen,
            new Point(center.X - d, center.Y + d),
            new Point(center.X + d, center.Y - d));
    }
}
