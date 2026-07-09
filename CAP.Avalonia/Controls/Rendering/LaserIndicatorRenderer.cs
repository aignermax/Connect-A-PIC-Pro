using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.ComponentHelpers;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Draws the per-coupler laser on/off indicator (#690) at each light-source
/// component. A coloured dot (wavelength colour) with rays = laser on (input);
/// a gray dot with a slash = laser off (the coupler is listen-only, an output).
/// Enabled lasers stay subtly visible at all times so it is always clear where
/// light enters; off-state icons only appear while a simulation mode is active.
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

    private static readonly Color RedLaser = Color.FromRgb(255, 82, 82);
    private static readonly Color GreenLaser = Color.FromRgb(105, 240, 174);
    private static readonly Color BlueLaser = Color.FromRgb(68, 138, 255);
    private static readonly Color OffGray = Color.FromRgb(158, 158, 158);

    /// <summary>
    /// True when the component's laser icon should be drawn/clickable.
    /// Enabled lasers are always visible; disabled ones only in simulation mode.
    /// </summary>
    /// <param name="comp">The component to test.</param>
    /// <param name="simulationActive">Whether a simulation overlay mode is active.</param>
    public static bool IsIconVisible(ComponentViewModel comp, bool simulationActive)
    {
        if (comp.LaserConfig == null) return false;
        return comp.LaserConfig.IsEnabled || simulationActive;
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
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.75), 30, 30, 30)),
            null, center, radius, radius);

        double dotRadius = radius * 0.45;
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
            null, center, dotRadius, dotRadius);

        if (cfg.IsEnabled)
            DrawRays(context, center, radius, color, alpha);
        else
            DrawOffSlash(context, center, radius, alpha);

        if (isHovered)
            context.DrawEllipse(null, new Pen(Brushes.White, HoverRingThickness), center, radius, radius);
    }

    /// <summary>Maps a configured wavelength to its display colour.</summary>
    /// <param name="wavelengthNm">Wavelength in nanometers.</param>
    public static Color GetWavelengthColor(int wavelengthNm)
    {
        if (wavelengthNm == StandardWaveLengths.GreenNM) return GreenLaser;
        if (wavelengthNm == StandardWaveLengths.BlueNM) return BlueLaser;
        return RedLaser;
    }

    private static void DrawRays(DrawingContext context, Point center, double radius, Color color, byte alpha)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), 1.2);
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
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 220, 220, 220)), 1.5);
        double d = radius * 0.7071; // 45° offset
        context.DrawLine(pen,
            new Point(center.X - d, center.Y + d),
            new Point(center.X + d, center.Y - d));
    }
}
