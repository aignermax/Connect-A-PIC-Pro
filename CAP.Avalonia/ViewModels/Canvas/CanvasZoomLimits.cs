namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// The main canvas zoom range, shared by the wheel handler, the toolbar
/// buttons and zoom-to-fit so the limits can never drift apart. The minimum
/// is deliberately low: large imported GDS layouts (centimeter-scale dies)
/// must still fit the screen.
/// </summary>
public static class CanvasZoomLimits
{
    /// <summary>Minimum zoom factor (2 %).</summary>
    public const double Min = 0.02;

    /// <summary>Maximum zoom factor (1000 %).</summary>
    public const double Max = 10.0;
}
