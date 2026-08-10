using System.Collections.Generic;
using System.Linq;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// Represents an axis-aligned bounding box in micrometers.
/// </summary>
public readonly struct BoundingBox
{
    /// <summary>Minimum X coordinate.</summary>
    public double MinX { get; }

    /// <summary>Minimum Y coordinate.</summary>
    public double MinY { get; }

    /// <summary>Maximum X coordinate.</summary>
    public double MaxX { get; }

    /// <summary>Maximum Y coordinate.</summary>
    public double MaxY { get; }

    /// <summary>Width of the bounding box.</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height of the bounding box.</summary>
    public double Height => MaxY - MinY;

    /// <summary>Center X coordinate.</summary>
    public double CenterX => (MinX + MaxX) / 2;

    /// <summary>Center Y coordinate.</summary>
    public double CenterY => (MinY + MaxY) / 2;

    /// <summary>Whether this bounding box has zero area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>
    /// Creates a new bounding box.
    /// </summary>
    public BoundingBox(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }
}

/// <summary>
/// Calculates bounding boxes for collections of components.
/// </summary>
public static class BoundingBoxCalculator
{
    /// <summary>
    /// Calculates the bounding box that encloses all components.
    /// Returns null if there are no components.
    /// </summary>
    public static BoundingBox? Calculate(
        IEnumerable<ComponentViewModel> components)
    {
        var list = components.ToList();
        if (list.Count == 0) return null;

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (var comp in list)
        {
            // For ComponentGroups, account for the offset between the group's position
            // and the minimum corner of its children
            double offsetX = 0;
            double offsetY = 0;

            if (comp.Component is CAP_Core.Components.Core.ComponentGroup group)
            {
                offsetX = group.MinChildOffsetX;
                offsetY = group.MinChildOffsetY;
            }

            double compMinX = comp.X + offsetX;
            double compMinY = comp.Y + offsetY;
            double compMaxX = compMinX + comp.Width;
            double compMaxY = compMinY + comp.Height;

            if (compMinX < minX) minX = compMinX;
            if (compMinY < minY) minY = compMinY;
            if (compMaxX > maxX) maxX = compMaxX;
            if (compMaxY > maxY) maxY = compMaxY;
        }

        return new BoundingBox(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Expands a bounding box by a fractional padding amount on each side.
    /// For example, a padding of 0.1 adds 10% of the dimension to each side.
    /// </summary>
    public static BoundingBox WithPadding(
        BoundingBox box, double paddingFraction)
    {
        double padX = box.Width * paddingFraction;
        double padY = box.Height * paddingFraction;

        return new BoundingBox(
            box.MinX - padX,
            box.MinY - padY,
            box.MaxX + padX,
            box.MaxY + padY);
    }

    /// <summary>
    /// Fraction of padding to apply around the design for zoom-to-fit.
    /// 0.1 means 10% padding on each side.
    /// </summary>
    public const double DefaultPaddingFraction = 0.1;

    /// <summary>
    /// Calculates zoom level and pan offsets to fit a bounding box
    /// into a viewport of the given dimensions.
    /// </summary>
    /// <param name="box">The bounding box to fit.</param>
    /// <param name="viewportWidth">Viewport width in screen pixels.</param>
    /// <param name="viewportHeight">Viewport height in screen pixels.</param>
    /// <param name="minZoom">Minimum allowed zoom level.</param>
    /// <param name="maxZoom">Maximum allowed zoom level.</param>
    /// <returns>The zoom level and pan offsets to apply.</returns>
    public static (double zoom, double panX, double panY) CalculateZoomToFit(
        BoundingBox box,
        double viewportWidth,
        double viewportHeight,
        double minZoom = CanvasZoomLimits.Min,
        double maxZoom = CanvasZoomLimits.Max)
    {
        double zoomX = viewportWidth / box.Width;
        double zoomY = viewportHeight / box.Height;
        double zoom = Math.Min(zoomX, zoomY);
        zoom = Math.Clamp(zoom, minZoom, maxZoom);

        double panX = viewportWidth / 2 - box.CenterX * zoom;
        double panY = viewportHeight / 2 - box.CenterY * zoom;

        return (zoom, panX, panY);
    }
}
