using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Z-order regression tests for the deferred canvas label layer. Renders the production
/// renderer composition — <see cref="WaveguideConnectionRenderer"/>, then
/// <see cref="ComponentRenderer"/>, then the label flush, exactly the order
/// <c>DesignCanvas.Render</c> uses — into a <see cref="RenderTargetBitmap"/> and samples real
/// pixels. The scene puts component B's opaque body over component A's name label, over the
/// hovered connection's length label, and over the hovered pin's name label: every one of
/// those texts must still rasterize ON TOP of B's body fill (bright pixels present), never
/// underneath it (body colour only). Avalonia's <see cref="DrawingContext"/> cannot be faked
/// (its constructor is internal), so the assertion is on rasterized pixels; the queue-level
/// assertions complement them by pinning which labels land in the deferred pass at all.
/// </summary>
public class CanvasLabelZOrderTests
{
    // World == pixels at zoom 1 (no pan). B sits above-right of A so B's body covers A's
    // name-label band (A label origin (5,25)) while the two name labels themselves never
    // overlap (A's spans y 25..~40, B's y 5..~20) — keeping the declutter tie-break out of
    // this test. The connection runs (40,25)→(80,25): its midpoint length label at (60,10)
    // and A's hovered pin name at (55,10) both sit squarely under B's body (8..108, 0..40).
    private const double ComponentAX = 0, ComponentAY = 20;
    private const double ComponentBX = 8, ComponentBY = 0, ComponentBWidth = 100, ComponentBHeight = 40;
    private const double PinAX = 40, PinAY = 25; // A's pin, absolute
    private const double PinBX = 80, PinBY = 25; // B's pin, absolute

    /// <summary>Inside B's body AND inside A's name-label glyph band ("lphaLong…").</summary>
    private static readonly PixelRect NameLabelRegion = new(12, 27, 40, 11);

    /// <summary>Inside B's body AND inside the hovered connection's length-label glyph band.</summary>
    private static readonly PixelRect LengthLabelRegion = new(63, 12, 30, 9);

    /// <summary>Inside B's body AND inside A's hovered pin-name glyph band ("outA" at (55,10)).</summary>
    private static readonly PixelRect PinNameRegion = new(56, 11, 18, 9);

    [AvaloniaFact]
    public void ComponentName_CoveredByLaterComponentBody_RasterizesOnTopOfThatBody()
    {
        var scene = BuildScene(hoverConnection: true);

        using var bitmap = RenderScene(scene);

        CountLightTextPixels(bitmap, NameLabelRegion).ShouldBeGreaterThan(20,
            "A's name label must draw AFTER B's opaque body fill — pre-deferral, B's body "
            + "painted over the name and left zero text pixels in this region");
    }

    [AvaloniaFact]
    public void HoveredConnectionLengthLabel_UnderComponentBody_RasterizesOnTopOfThatBody()
    {
        var scene = BuildScene(hoverConnection: true);

        using var bitmap = RenderScene(scene);

        CountLightTextPixels(bitmap, LengthLabelRegion).ShouldBeGreaterThan(20,
            "the hover-revealed length label must draw after all component bodies — "
            + "pre-deferral, the connection pass drew it before B's body covered it");
    }

    [AvaloniaFact]
    public void HoveredPinName_LandsInDeferredTopPass_AndRasterizesOnTopOfComponentBody()
    {
        var scene = BuildScene(hoverConnection: false);
        scene.Canvas.UpdatePinHighlight(PinAX, PinAY);
        scene.Canvas.HighlightedPin.ShouldNotBeNull("the pin highlight is the connect/hover affordance under test");

        using var bitmap = RenderScene(scene);

        // Queue level: the pin name went through the deferred layer (the flush consumed it).
        scene.Rc.Labels.Pending.ShouldBeEmpty("RenderScene already flushed the queue");
        CountCyanTextPixels(bitmap, PinNameRegion).ShouldBeGreaterThan(5,
            "the hover-during-connect pin name must draw after all component bodies");
    }

    [AvaloniaFact]
    public void RendererPass_EnqueuesNameAndPinNameLabels_AtTheirAnchors()
    {
        var scene = BuildScene(hoverConnection: true);
        scene.Canvas.UpdatePinHighlight(PinAX, PinAY);

        using (var rtb = new RenderTargetBitmap(new PixelSize(150, 75)))
        using (var ctx = rtb.CreateDrawingContext())
        {
            new WaveguideConnectionRenderer().Render(ctx, scene.Rc);
            new ComponentRenderer().Render(ctx, scene.Rc);
            // No flush on purpose: assertions target the queue itself.
        }

        var pending = scene.Rc.Labels.Pending;
        pending.Count.ShouldBe(4,
            "A's name + B's name + hovered pin name + hovered connection length label");
        pending.ShouldContain(e => e.Origin == new Point(ComponentAX + 5, ComponentAY + 5)
            && HasBrushColor(e.Foreground, 255, 255, 255),
            "component names must be enqueued at their top-left anchor, not drawn inline");
        pending.ShouldContain(e => e.Origin == new Point(PinAX + 15, PinAY - 15)
            && HasBrushColor(e.Foreground, 0, 255, 255),
            "the hovered/connect pin name must be enqueued (cyan) for the topmost pass");
        pending.ShouldContain(e => e.Origin == new Point((PinAX + PinBX) / 2, PinAY - 15),
            "the hovered connection's length label must be enqueued for the topmost pass");
    }

    // ── Scene ────────────────────────────────────────────────────────────────

    private sealed record Scene(DesignCanvasViewModel Canvas, CanvasInteractionState State, CanvasRenderContext Rc);

    private static Scene BuildScene(bool hoverConnection)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting();

        var a = MakeComponent("AlphaLongComponentName", ComponentAX, ComponentAY, 60, 40,
            pinName: "outA", pinOffsetX: PinAX - ComponentAX, pinOffsetY: PinAY - ComponentAY, pinAngle: 0);
        var b = MakeComponent("B", ComponentBX, ComponentBY, ComponentBWidth, ComponentBHeight,
            pinName: "inB", pinOffsetX: PinBX - ComponentBX, pinOffsetY: PinBY - ComponentBY, pinAngle: 180);
        canvas.AddComponent(a.Component);
        canvas.AddComponent(b.Component);

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(PinAX, PinAY, PinBX, PinBY, 0));
        var connection = canvas.ConnectPinsWithCachedRoute(a.Pin, b.Pin, route);
        connection.ShouldNotBeNull();

        var state = new CanvasInteractionState();
        if (hoverConnection)
            state.HoveredConnection = connection;

        var rc = new CanvasRenderContext
        {
            ViewModel = canvas,
            InteractionState = state,
            Zoom = 1.0,
            Bounds = new Rect(0, 0, 150, 75),
        };
        return new Scene(canvas, state, rc);
    }

    /// <summary>Renders the composition in <c>DesignCanvas.Render</c>'s order onto a black
    /// background (matching the canvas) and returns the rasterized frame. Set
    /// <c>ZORDER_DEBUG_PNG</c> to a file path to also dump the frame — the first debugging
    /// step when a pixel assertion fails on a machine with different font metrics.</summary>
    private static RenderTargetBitmap RenderScene(Scene scene)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(150, 75));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, 150, 75));
            new WaveguideConnectionRenderer().Render(ctx, scene.Rc);
            new ComponentRenderer().Render(ctx, scene.Rc);
            scene.Rc.Labels.Flush(ctx, scene.Rc.Zoom);
        }
        if (Environment.GetEnvironmentVariable("ZORDER_DEBUG_PNG") is { } debugPath)
            rtb.Save(debugPath);
        return rtb;
    }

    private static (Component Component, PhysicalPin Pin) MakeComponent(
        string name, double x, double y, double width, double height,
        string pinName, double pinOffsetX, double pinOffsetY, double pinAngle)
    {
        var logicalPin = new Pin("opt", 0, MatterType.Light, RectSide.Right);
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin> { logicalPin });
        var physicalPin = new PhysicalPin
        {
            Name = pinName,
            OffsetXMicrometers = pinOffsetX,
            OffsetYMicrometers = pinOffsetY,
            AngleDegrees = pinAngle,
            LogicalPin = logicalPin,
        };
        var sMatrix = new SMatrix(new List<Guid> { logicalPin.IDInFlow, logicalPin.IDOutFlow }, new());
        var component = new Component(
            new Dictionary<int, SMatrix> { { StandardWaveLengths.RedNM, sMatrix } },
            new List<Slider>(), "test", "", parts, 0, name,
            DiscreteRotation.R0, new List<PhysicalPin> { physicalPin })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = width,
            HeightMicrometers = height,
        };
        physicalPin.ParentComponent = component;
        return (component, physicalPin);
    }

    /// <summary>True when the brush is a solid colour with the given RGB — a static helper
    /// because ShouldContain's expression trees reject C# pattern matching.</summary>
    private static bool HasBrushColor(IBrush brush, byte r, byte g, byte b) =>
        brush is ISolidColorBrush solid && solid.Color.R == r && solid.Color.G == g && solid.Color.B == b;

    /// <summary>Counts pixels of light (white/LightGray) label text: even anti-aliased glyph
    /// edges keep every channel above 100, while everything else in the scene — body fills
    /// (40,50,70), grey borders, orange waveguides, green/cyan pins, the near-black halo —
    /// has at least one channel at or below 100.</summary>
    private static int CountLightTextPixels(RenderTargetBitmap bitmap, PixelRect region) =>
        CountPixels(bitmap, region, (r, g, b) => r > 100 && g > 100 && b > 100);

    /// <summary>Counts pixels of the cyan (0,255,255) hover/connect pin name.</summary>
    private static int CountCyanTextPixels(RenderTargetBitmap bitmap, PixelRect region) =>
        CountPixels(bitmap, region, (r, g, b) => r < 100 && g > 200 && b > 200);

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
