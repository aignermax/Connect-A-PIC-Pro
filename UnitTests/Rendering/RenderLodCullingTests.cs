using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Perf guard for the render LOD / viewport culling added for huge GDS imports
/// (KLayout-style zoom behavior): at full zoom-out, sub-pixel outline polygons must
/// never reach <see cref="DrawingContext"/>, frozen-path segments outside the cull
/// rect must not paint, and connections fully outside the viewport must be skipped.
/// The renderers expose issued/culled counters as the test seam these assertions use;
/// pixel probes into a <see cref="RenderTargetBitmap"/> pin both ends — culled work
/// leaves no trace, visible work still renders (same pattern as
/// <see cref="ComponentGroupOutlineRenderingTests"/>).
/// </summary>
public class RenderLodCullingTests
{
    private const int BitmapWidth = 100, BitmapHeight = 80;

    /// <summary>Full zoom-out: a 10 µm component is 0.5 px on screen.</summary>
    private const double ZoomedOut = 0.05;

    // ── Outline polygon LOD: draw-call counters ─────────────────────────────

    [AvaloniaFact]
    public void ZoomedOut_ManyOutlineComponents_AllSubPixelPolygonsShortCircuitTheDrawCall()
    {
        // Synthetic many-outline set: 200 components sharing one 10-polygon template —
        // the GDS-import pattern where all placed instances share the outline list
        // (see ComponentTemplates.CreateFromTemplate). 1 µm polygons ≈ 0.05 px here.
        var outlines = CreateOutlineTemplate(polygonCount: 10, polygonSize: 1.0);
        var renderer = new ComponentOutlineRenderer();

        using var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, BitmapWidth, BitmapHeight));
            using (ctx.PushTransform(Matrix.CreateScale(ZoomedOut, ZoomedOut)))
            {
                for (int i = 0; i < 200; i++)
                    renderer.Draw(ctx, i * 20.0, 0, 10, 10, 0, outlines, false, ZoomedOut);
            }
        }

        // Without the per-polygon LOD this scene issues 200 × 10 = 2000 DrawGeometry calls.
        renderer.IssuedGeometryCount.ShouldBe(0);
        renderer.CulledGeometryCount.ShouldBe(2000);
    }

    [AvaloniaFact]
    public void ZoomedOut_MixedPolygonSizes_OnlySubPixelPolygonsAreCulled()
    {
        OutlinePolygon[] outlines =
        {
            CreateSquarePolygon(size: 1.0),  // 0.05 px → culled
            CreateSquarePolygon(size: 30.0), // exactly 1.5 px → drawn (threshold is strict)
            CreateSquarePolygon(size: 40.0), // 2 px → drawn
        };
        var renderer = new ComponentOutlineRenderer();

        using var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
            renderer.Draw(ctx, 0, 0, 100, 100, 0, outlines, false, ZoomedOut);

        renderer.IssuedGeometryCount.ShouldBe(2);
        renderer.CulledGeometryCount.ShouldBe(1);
    }

    [AvaloniaFact]
    public void NormalZoom_PolygonsAboveThreshold_AreAllIssued()
    {
        var outlines = CreateOutlineTemplate(polygonCount: 10, polygonSize: 10.0); // 10 px at zoom 1
        var renderer = new ComponentOutlineRenderer();

        using var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
            renderer.Draw(ctx, 0, 0, 60, 60, 0, outlines, false, 1.0);

        renderer.IssuedGeometryCount.ShouldBe(10);
        renderer.CulledGeometryCount.ShouldBe(0);
    }

    // ── Outline polygon LOD: pixels ─────────────────────────────────────────

    [AvaloniaFact]
    public void NormalZoom_OutlinePixels_RenderWhereExpected()
    {
        // 60×40 component at (20,20); its outline stripe covers local (0,10)..(30,30)
        // → world (20,30)..(50,50).
        var outlines = new[] { CreateStripePolygon() };
        var renderer = new ComponentOutlineRenderer();

        using var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, BitmapWidth, BitmapHeight));
            renderer.Draw(ctx, 20, 20, 60, 40, 0, outlines, false, 1.0);
        }

        renderer.IssuedGeometryCount.ShouldBe(1);
        // The outline fill (46,100,160,220) blends to ~(18,29,40) over black — faint
        // but blue-dominant.
        CountPixels(bitmap, new PixelRect(24, 34, 20, 12), (r, g, b) => b > 30 && b > r && r < 35)
            .ShouldBeGreaterThan(100, "the outline stripe must be painted at normal zoom");
    }

    [AvaloniaFact]
    public void ZoomedOut_CulledPolygonLeavesNoPixels()
    {
        // A 28 µm square at zoom 0.05 is 1.4 px on screen — just under the 1.5 px
        // threshold. It lands at screen (20,20)..(21.4,21.4); if the cull regressed,
        // the fill (alpha 46, blue 220) would paint pixel (20,20) at blue ≈ 40.
        var outlines = new[] { CreateSquarePolygon(size: 28.0) };
        var renderer = new ComponentOutlineRenderer();

        using var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, BitmapWidth, BitmapHeight));
            using (ctx.PushTransform(Matrix.CreateScale(ZoomedOut, ZoomedOut)))
                renderer.Draw(ctx, 400, 400, 28, 28, 0, outlines, false, ZoomedOut);
        }

        renderer.CulledGeometryCount.ShouldBe(1);
        CountPixels(bitmap, new PixelRect(18, 18, 6, 6), (r, g, b) => r > 0 || g > 0 || b > 0)
            .ShouldBe(0, "a culled polygon must not rasterize a single pixel");
    }

    // ── Frozen paths: per-segment cull ──────────────────────────────────────

    [AvaloniaFact]
    public void FrozenPath_SegmentOutsideCullRect_IsNotDrawn()
    {
        // Segment A (10,10)-(30,10) lies inside the cull rect; segment B (60,60)-(90,60)
        // lies outside the cull rect but inside the bitmap, so skipping it is observable.
        var frozen = CreateTwoSegmentPath();
        var cullRect = new Rect(-5, -5, 50, 50);

        using var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, BitmapWidth, BitmapHeight));
            ComponentGroupRenderer.RenderFrozenWaveguidePath(ctx, frozen, cullRect: cullRect);
        }

        CountPixels(bitmap, new PixelRect(10, 8, 20, 5), (r, g, b) => r > 150 && g > 80)
            .ShouldBeGreaterThan(20, "the segment inside the cull rect must render");
        CountPixels(bitmap, new PixelRect(60, 58, 30, 5), (r, g, b) => r > 20 || g > 20 || b > 20)
            .ShouldBe(0, "the segment outside the cull rect must be skipped");
    }

    [AvaloniaFact]
    public void FrozenPath_WithoutCullRect_DrawsAllSegments()
    {
        // Control for the cull test above: with no cull rect both segments paint,
        // proving the off-cull segment would have been visible.
        var frozen = CreateTwoSegmentPath();

        using var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, BitmapWidth, BitmapHeight));
            ComponentGroupRenderer.RenderFrozenWaveguidePath(ctx, frozen);
        }

        CountPixels(bitmap, new PixelRect(10, 8, 20, 5), (r, g, b) => r > 150 && g > 80)
            .ShouldBeGreaterThan(20);
        CountPixels(bitmap, new PixelRect(60, 58, 30, 5), (r, g, b) => r > 150 && g > 80)
            .ShouldBeGreaterThan(20);
    }

    // ── Waveguide connections: viewport cull ────────────────────────────────

    [AvaloniaFact]
    public void ConnectionFullyOutsideViewport_IsCulledBeforeDrawing()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting();

        // Visible connection: unrouted, so it draws its straight fallback line
        // (250,125)→(400,125). The second connection sits at y ≈ 10125, far outside
        // the 500×160 viewport whose inflated cull rect ends at y = 200.
        AddFallbackConnectionAt(canvas, x: 0, y: 0);
        AddFallbackConnectionAt(canvas, x: 0, y: 10000);

        var rc = new CanvasRenderContext
        {
            ViewModel = canvas,
            InteractionState = new CanvasInteractionState(),
            Zoom = 1.0,
            Bounds = new Rect(0, 0, 500, 160),
        };
        var renderer = new WaveguideConnectionRenderer();

        using var bitmap = new RenderTargetBitmap(new PixelSize(500, 160));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, 500, 160));
            renderer.Render(ctx, rc);
        }

        renderer.IssuedConnectionCount.ShouldBe(1);
        renderer.CulledConnectionCount.ShouldBe(1);

        // The in-viewport connection still paints its orange fallback line (no over-culling).
        CountPixels(bitmap, new PixelRect(280, 122, 80, 7), (r, g, b) => r > 200 && g > 100)
            .ShouldBeGreaterThan(50, "the in-viewport connection must render its line");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static OutlinePolygon CreateSquarePolygon(double size) => new()
    {
        Layer = 1,
        DataType = 0,
        Points = new[]
        {
            new OutlinePoint(0, 0), new OutlinePoint(size, 0),
            new OutlinePoint(size, size), new OutlinePoint(0, size),
            new OutlinePoint(0, 0) // closed ring: first point repeated at the end
        }
    };

    private static OutlinePolygon CreateStripePolygon() => new()
    {
        Layer = 1,
        DataType = 0,
        Points = new[]
        {
            new OutlinePoint(0, 10), new OutlinePoint(30, 10),
            new OutlinePoint(30, 30), new OutlinePoint(0, 30),
            new OutlinePoint(0, 10)
        }
    };

    private static OutlinePolygon[] CreateOutlineTemplate(int polygonCount, double polygonSize)
    {
        var polygons = new OutlinePolygon[polygonCount];
        for (int i = 0; i < polygonCount; i++)
            polygons[i] = CreateSquarePolygon(polygonSize);
        return polygons;
    }

    private static FrozenWaveguidePath CreateTwoSegmentPath()
    {
        var frozen = new FrozenWaveguidePath { Path = new RoutedPath() };
        frozen.Path.Segments.Add(new StraightSegment(10, 10, 30, 10, 0));
        frozen.Path.Segments.Add(new StraightSegment(60, 60, 90, 60, 0));
        return frozen;
    }

    private static void AddFallbackConnectionAt(DesignCanvasViewModel canvas, double x, double y)
    {
        var start = TestComponentFactory.CreateBasicComponent();
        start.PhysicalX = x;
        start.PhysicalY = y;
        var end = TestComponentFactory.CreateBasicComponent();
        end.PhysicalX = x + 400;
        end.PhysicalY = y;
        canvas.AddComponent(start);
        canvas.AddComponent(end);

        var connection = TestComponentFactory.CreateConnection(start, end);
        canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
        canvas.ConnectionManager.AddExistingConnection(connection);
    }

    private static int CountPixels(RenderTargetBitmap bitmap, PixelRect region,
        Func<byte, byte, byte, bool> matches)
    {
        int bufferSize = region.Width * region.Height * 4;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(region, buffer, bufferSize, region.Width * 4);
            int count = 0;
            for (int i = 0; i < region.Width * region.Height; i++)
            {
                byte blue = Marshal.ReadByte(buffer, i * 4);
                byte green = Marshal.ReadByte(buffer, i * 4 + 1);
                byte red = Marshal.ReadByte(buffer, i * 4 + 2);
                if (matches(red, green, blue))
                    count++;
            }
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
