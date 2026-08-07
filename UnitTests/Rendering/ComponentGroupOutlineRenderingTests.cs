using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Pixel-level regression test for outline rendering INSIDE a <see cref="ComponentGroup"/>:
/// the GDS import executor groups every multi-component import, so a grouped outlined
/// child must render its outline polygons — pre-fix, <see cref="ComponentRenderer"/>
/// drew every group child as the plain fill+border rectangle and imported outlines were
/// invisible in the default flow. Renders the production renderer composition into a
/// <see cref="RenderTargetBitmap"/> and samples real pixels (Avalonia's DrawingContext
/// cannot be faked — same pattern as <see cref="CanvasLabelZOrderTests"/>); the outline
/// transform math itself is pinned by <see cref="ComponentOutlineRendererTests"/>.
/// </summary>
public class ComponentGroupOutlineRenderingTests
{
    // World == pixels at zoom 1 (no pan). Child bbox (20,20)..(80,60); its outline stripe
    // covers only the LEFT half (local (0,10)..(30,30) → world (20,30)..(50,50)), so the
    // bbox's right half separates "outline drawn" from "rectangle body drawn".
    private const double ChildX = 20, ChildY = 20, ChildWidth = 60, ChildHeight = 40;

    /// <summary>Inside the outline stripe, away from its anti-aliased edges.</summary>
    private static readonly PixelRect OutlineRegion = new(24, 34, 20, 12);

    /// <summary>Inside the child bbox but OUTSIDE the stripe (and clear of the bbox
    /// border, the group border and the lock icon at the bounds' top-right corner).</summary>
    private static readonly PixelRect UncoveredRegion = new(53, 43, 24, 14);

    [AvaloniaFact]
    public void GroupedOutlinedChild_RendersOutlineGeometry_NotPlainRectangle()
    {
        var canvas = new DesignCanvasViewModel();
        var group = new ComponentGroup("G");
        group.AddChild(CreateOutlinedChild());
        canvas.AddComponent(group);
        var rc = new CanvasRenderContext
        {
            ViewModel = canvas,
            InteractionState = new CanvasInteractionState(),
            Zoom = 1.0,
            Bounds = new Rect(0, 0, 100, 80),
        };

        using var bitmap = new RenderTargetBitmap(new PixelSize(100, 80));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, 100, 80));
            new ComponentRenderer().Render(ctx, rc);
        }

        // The outline fill (46,100,160,220) blends to ~(18,29,40) over the black canvas —
        // faint but blue-dominant, and clearly darker than the rectangle body (40,50,70).
        CountPixels(bitmap, OutlineRegion, (r, g, b) => b > 30 && b > r && r < 35)
            .ShouldBeGreaterThan(100,
                "the stripe area must be painted by the outline geometry, not left black");

        // The regression signature: the plain group-child rectangle fill (40,50,70) must
        // NOT cover the bbox area the stripe does not occupy — with the bug every pixel
        // here is that fill; with outlines rendered it stays black background.
        CountPixels(bitmap, UncoveredRegion, (r, g, b) => r > 20 || g > 20 || b > 20)
            .ShouldBe(0,
                "no rectangle body may be drawn for an outlined group child");
    }

    /// <summary>A 60×40 child whose only outline is the left-half stripe (no pins needed —
    /// the group renderer path under test does not draw child pins).</summary>
    private static Component CreateOutlinedChild()
    {
        var child = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "nazca_outlined",
            nazcaFunctionParams: "",
            parts: new Part[1, 1] { { new Part() } },
            typeNumber: 0,
            identifier: $"outlined_{Guid.NewGuid():N}",
            rotationCounterClock: DiscreteRotation.R0,
            physicalPins: new List<PhysicalPin>())
        {
            PhysicalX = ChildX,
            PhysicalY = ChildY,
            WidthMicrometers = ChildWidth,
            HeightMicrometers = ChildHeight,
            OutlinePolygons = new[]
            {
                new OutlinePolygon
                {
                    Layer = 1,
                    DataType = 0,
                    Points = new[]
                    {
                        new OutlinePoint(0, 10), new OutlinePoint(30, 10),
                        new OutlinePoint(30, 30), new OutlinePoint(0, 30),
                        new OutlinePoint(0, 10) // closed ring: first point repeated at the end
                    }
                }
            }
        };
        return child;
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
