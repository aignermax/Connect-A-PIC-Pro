using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.Services.GdsImport.LayerVisibility;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.GdsImport.LayerVisibility;

/// <summary>
/// Pixel-level proof of the per-layer view filter (issue #858): with a hidden
/// (11, 0), the metal stripe paints nothing while the waveguide stripe still
/// paints; with a faded (11, 0), it paints dimmer than at full opacity. Mirrors
/// the RenderTargetBitmap probe of <see cref="Rendering.PerLayerOutlineRenderingTests"/>.
/// </summary>
public class GdsLayerVisibilityRenderingTests
{
    // World == pixels at zoom 1 (no pan). Child bbox (20,20)..(80,60); the left stripe
    // (layer 1,0) covers world (20,30)..(50,50), the right stripe (11,0) (50,30)..(80,50).
    private const double ChildX = 20, ChildY = 20, ChildWidth = 60, ChildHeight = 40;

    /// <summary>Inside the left (waveguide) stripe, away from anti-aliased edges.</summary>
    private static readonly PixelRect WaveguideRegion = new(24, 34, 20, 12);

    /// <summary>Inside the right (metal) stripe, away from anti-aliased edges.</summary>
    private static readonly PixelRect MetalRegion = new(54, 34, 20, 12);

    [AvaloniaFact]
    public void HiddenLayer_PaintsNothing_WhileOtherLayerStillPaints()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(11, 0, isVisible: false, opacity: 1.0);

        using var fullBitmap = RenderTwoLayerComponent(layerVisibility: null);
        using var hiddenBitmap = RenderTwoLayerComponent(state);

        CountPixels(fullBitmap, MetalRegion, (r, g, b) => r > 25 && r > b)
            .ShouldBeGreaterThan(100, "without the filter the metal stripe paints amber");
        CountPixels(hiddenBitmap, MetalRegion, (r, g, b) => r > b)
            .ShouldBe(0, "the hidden (11, 0) stripe must not paint any amber pixel");
        CountPixels(hiddenBitmap, WaveguideRegion, (r, g, b) => b > 30 && b > r)
            .ShouldBeGreaterThan(100, "the visible (1, 0) stripe must still paint");
    }

    [AvaloniaFact]
    public void FadedLayer_PaintsDimmer_ThanFullOpacity()
    {
        var faded = new GdsLayerVisibilityState();
        faded.Set(11, 0, isVisible: true, opacity: 0.3);

        using var fullBitmap = RenderTwoLayerComponent(layerVisibility: null);
        using var fadedBitmap = RenderTwoLayerComponent(faded);

        // Metal fill (210,160,70 @ α46 over black) reaches r ≈ 38 at full opacity;
        // at 30 % it stays below r ≈ 12 but must still paint something.
        CountPixels(fullBitmap, MetalRegion, (r, g, b) => r > 25)
            .ShouldBeGreaterThan(100, "full opacity paints the bright metal amber");
        CountPixels(fadedBitmap, MetalRegion, (r, g, b) => r > 25)
            .ShouldBe(0, "at 30 % opacity no pixel reaches the full-opacity brightness");
        CountPixels(fadedBitmap, MetalRegion, (r, g, b) => r > 3 && r > b)
            .ShouldBeGreaterThan(100, "the faded stripe is dimmed, not hidden");
    }

    private static RenderTargetBitmap RenderTwoLayerComponent(GdsLayerVisibilityState? layerVisibility)
    {
        var canvas = new DesignCanvasViewModel();
        var group = new ComponentGroup("G");
        group.AddChild(CreateTwoLayerChild());
        canvas.AddComponent(group);
        var rc = new CanvasRenderContext
        {
            ViewModel = canvas,
            InteractionState = new CanvasInteractionState(),
            Zoom = 1.0,
            Bounds = new Rect(0, 0, 100, 80),
            LayerVisibility = layerVisibility,
        };

        var bitmap = new RenderTargetBitmap(new PixelSize(100, 80));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, 100, 80));
            new ComponentRenderer().Render(ctx, rc);
        }
        return bitmap;
    }

    /// <summary>A 60×40 child with the left-half stripe on (1, 0) and the right-half
    /// stripe on (11, 0) — the two halves share the bbox edge at local x=30.</summary>
    private static Component CreateTwoLayerChild()
    {
        static OutlinePolygon Stripe(int layer, int dataType, double x0, double x1) => new()
        {
            Layer = layer,
            DataType = dataType,
            Points = new[]
            {
                new OutlinePoint(x0, 10), new OutlinePoint(x1, 10),
                new OutlinePoint(x1, 30), new OutlinePoint(x0, 30),
                new OutlinePoint(x0, 10)
            }
        };

        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "nazca_twolayer",
            nazcaFunctionParams: "",
            parts: new Part[1, 1] { { new Part() } },
            typeNumber: 0,
            identifier: $"twolayer_{Guid.NewGuid():N}",
            rotationCounterClock: DiscreteRotation.R0,
            physicalPins: new List<PhysicalPin>())
        {
            PhysicalX = ChildX,
            PhysicalY = ChildY,
            WidthMicrometers = ChildWidth,
            HeightMicrometers = ChildHeight,
            OutlinePolygons = new[] { Stripe(1, 0, 0, 30), Stripe(11, 0, 30, 60) }
        };
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
