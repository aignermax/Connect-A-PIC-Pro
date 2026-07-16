using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;

namespace CAP.Avalonia.Controls.Canvas.ComponentPreview;

/// <summary>
/// Draws GDS polygons for a component onto the design canvas.
/// Handles coordinate transform from Nazca µm space to canvas-pixel space,
/// Y-axis flip, component rotation, and layer-based colouring.
/// </summary>
public static class GdsPolygonRenderer
{
    // ── Layer colour palette (static readonly — never allocate per-frame) ───
    // Add new layers here without touching any other file.

    private static readonly IBrush WaveguideBrush = new SolidColorBrush(Color.FromArgb(180, 100, 160, 220)); // layer 1
    private static readonly IBrush DefaultBrush   = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)); // all other layers

    private const int WaveguideLayer = 1;

    /// <summary>
    /// Bitmap width/height used when rasterising a preview at exact pixel dimensions.
    /// Used as a lower bound to avoid zero-size bitmaps.
    /// </summary>
    private const int MinBitmapPixels = 16;

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renders GDS polygons for <paramref name="comp"/> using
    /// <paramref name="previewData"/>. No-ops when the result has no polygons.
    /// When a pre-rasterised <see cref="GdsPreviewData.Bitmap"/> is available the
    /// method blits it directly (O(1) per frame). Otherwise it falls back to
    /// rebuilding geometry — this only occurs in the brief window between cache
    /// population and bitmap creation on the UI thread.
    /// </summary>
    /// <param name="context">Avalonia drawing context (world-space transform already active).</param>
    /// <param name="previewData">Cached preview data for the component template.</param>
    /// <param name="comp">Target component providing position and rotation.</param>
    public static void DrawGdsPreview(
        DrawingContext context,
        GdsPreviewData previewData,
        ComponentViewModel comp)
    {
        var result = previewData.Result;
        if (result.Polygons.Count == 0)
            return;

        double centerX = comp.X + comp.Width  / 2.0;
        double centerY = comp.Y + comp.Height / 2.0;
        double rotationDegrees = comp.Component.RotationDegrees;

        // comp.Width/Height are the CURRENT footprint — the rotate command has already
        // swapped them at 90°/270°. The bitmap holds UNROTATED geometry, so it must be
        // drawn into the unrotated-size rect (centred on the footprint centre); the
        // rotation transform then maps it exactly onto the rotated footprint. Drawing
        // straight into the swapped footprint would apply the 90° swap twice and leave
        // the preview visually unrotated and misaligned with the pins.
        var destRect = GetUnrotatedDestRect(comp.X, comp.Y, comp.Width, comp.Height, rotationDegrees);

        using (context.PushTransform(BuildRotationMatrix(rotationDegrees, centerX, centerY)))
        {
            if (previewData.Bitmap != null)
            {
                context.DrawImage(previewData.Bitmap, destRect);
                return;
            }

            // Fallback: rebuild geometry (only during the brief pre-bitmap window)
            DrawPolygonsAsGeometry(context, result, destRect.X, destRect.Y, destRect.Width, destRect.Height);
        }
    }

    /// <summary>
    /// Rasterises all polygons in <paramref name="result"/> to a
    /// <see cref="RenderTargetBitmap"/> at the requested pixel dimensions.
    /// Must be called on the UI thread.
    /// Returns <c>null</c> if the bbox is degenerate or bitmap creation fails.
    /// </summary>
    internal static RenderTargetBitmap? RasterizeToBitmap(NazcaPreviewResult result, int width, int height)
    {
        double bboxW = result.XMax - result.XMin;
        double bboxH = result.YMax - result.YMin;
        if (bboxW <= 0 || bboxH <= 0)
            return null;

        double scaleX = width  / bboxW;
        double scaleY = height / bboxH;

        try
        {
            var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
            using var ctx = bitmap.CreateDrawingContext();
            foreach (var poly in result.Polygons)
            {
                var geo = BuildPolygonGeometry(poly.Vertices, result.XMin, result.YMax, scaleX, scaleY, 0, 0);
                ctx.DrawGeometry(GetBrushForLayer(poly.Layer), null, geo);
            }
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // ── Internal helpers (internal for unit-testing transform math) ─────────

    /// <summary>
    /// Transforms a single Nazca-space vertex to canvas-pixel space.
    /// Exposed as <c>internal</c> to allow transform-math unit tests.
    /// </summary>
    internal static (double CanvasX, double CanvasY) TransformVertex(
        double nazcaX, double nazcaY,
        double xMin, double yMax,
        double scaleX, double scaleY,
        double compX, double compY)
    {
        return (
            compX + (nazcaX - xMin) * scaleX,
            compY + (yMax  - nazcaY) * scaleY   // Y-flip: Nazca Y-up → screen Y-down
        );
    }

    /// <summary>
    /// Footprint size at RotationDegrees = 0. The rotate command swaps the component's
    /// live Width/Height at each 90° step, so odd quarter-turn states swap them back.
    /// Accepts any 90°-multiple (normalises negatives and ≥360°).
    /// </summary>
    internal static (double Width, double Height) GetUnrotatedSize(
        double rotationDegrees, double currentWidth, double currentHeight)
    {
        int quarterTurns = (((int)Math.Round(rotationDegrees / 90.0)) % 4 + 4) % 4;
        return quarterTurns % 2 == 0
            ? (currentWidth, currentHeight)
            : (currentHeight, currentWidth);
    }

    /// <summary>
    /// The rect the UNROTATED preview must be drawn into so that, after applying
    /// <see cref="BuildRotationMatrix"/> around the footprint centre, the image covers the
    /// component's current (rotation-swapped) footprint exactly — also for non-square
    /// components, where a 90° rotation swaps the bounding-box width and height.
    /// </summary>
    internal static Rect GetUnrotatedDestRect(
        double compX, double compY, double compWidth, double compHeight, double rotationDegrees)
    {
        var (unrotatedW, unrotatedH) = GetUnrotatedSize(rotationDegrees, compWidth, compHeight);
        double centerX = compX + compWidth  / 2.0;
        double centerY = compY + compHeight / 2.0;
        return new Rect(centerX - unrotatedW / 2.0, centerY - unrotatedH / 2.0, unrotatedW, unrotatedH);
    }

    /// <summary>
    /// Builds a rotation-around-centre matrix.
    /// Positive <paramref name="degrees"/> = clockwise on screen (Y-down canvas).
    /// </summary>
    internal static Matrix BuildRotationMatrix(double degrees, double cx, double cy)
    {
        if (degrees == 0)
            return Matrix.Identity;

        double radians = degrees * Math.PI / 180.0;
        return Matrix.CreateTranslation(-cx, -cy)
             * Matrix.CreateRotation(radians)
             * Matrix.CreateTranslation(cx, cy);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    internal static void DrawPolygonsAsGeometry(
        DrawingContext context,
        NazcaPreviewResult result,
        double compX, double compY,
        double compWidth, double compHeight)
    {
        double bboxW = result.XMax - result.XMin;
        double bboxH = result.YMax - result.YMin;
        if (bboxW <= 0 || bboxH <= 0)
            return;

        double scaleX = compWidth  / bboxW;
        double scaleY = compHeight / bboxH;

        foreach (var poly in result.Polygons)
        {
            var geo = BuildPolygonGeometry(poly.Vertices, result.XMin, result.YMax, scaleX, scaleY, compX, compY);
            context.DrawGeometry(GetBrushForLayer(poly.Layer), null, geo);
        }
    }

    private static StreamGeometry BuildPolygonGeometry(
        IReadOnlyList<(double X, double Y)> vertices,
        double xMin, double yMax,
        double scaleX, double scaleY,
        double compX, double compY)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        bool first = true;
        foreach (var (nx, ny) in vertices)
        {
            var (px, py) = TransformVertex(nx, ny, xMin, yMax, scaleX, scaleY, compX, compY);
            if (first) { ctx.BeginFigure(new Point(px, py), true); first = false; }
            else ctx.LineTo(new Point(px, py));
        }
        if (!first) ctx.EndFigure(true);
        return geo;
    }

    internal static IBrush GetBrushForLayer(int layer) => layer switch
    {
        WaveguideLayer => WaveguideBrush,
        _              => DefaultBrush,
    };
}
