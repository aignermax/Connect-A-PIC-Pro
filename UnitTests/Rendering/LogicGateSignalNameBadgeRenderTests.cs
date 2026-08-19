using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using UnitTests.Integration;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Render smoke tests for the named canvas logic-state badges (issue #1051, rung 4→5 of
/// the NAND game): over the shipped <c>examples/Logic Gate 4-Bit Adder.lun</c> — whose
/// operand pins carry the persisted signal names A0–A3, B0–B3 and Cin since #1032/#1034 —
/// the Logic panel's build puts a named badge (<c>A0 = 1</c>) next to the live bit on
/// every gate group reading a named signal, and the badge renderer actually paints those
/// chips headlessly. A synthetic geometry test pins the chip layout: the named chip
/// widens to fit its label while the anonymous output badge keeps its exact square.
/// Renders the production renderer into a <see cref="RenderTargetBitmap"/> and samples
/// real pixels — same pattern as <see cref="ComponentGroupOutlineRenderingTests"/>.
/// </summary>
public class LogicGateSignalNameBadgeRenderTests
    : IClassFixture<LogicGateFourBitAdderExampleTests.FourBitAdderFixture>
{
    // Badge layout constants mirrored from LogicGateStateBadgeRenderer: with the bitmap
    // origin 70px left and 10px above the group's top-right corner, badge row N spans
    // local y ∈ [14 + N·18, 30 + N·18] and every chip's right edge sits at local x = 66;
    // the anonymous square chip spans local x ∈ [50, 66].
    private const int BitmapWidth = 80;
    private const int BitmapHeight = 80;
    private const int RowPitch = 18; // BadgeSize + BadgeSpacing
    private const int RowTop = 14;   // BadgeMargin + 10 origin offset
    private const int SquareLeftEdge = 50;

    /// <summary>The nine network signals of the 4-bit adder (issues #1025/#1034).</summary>
    private static readonly string[] NetworkSignals =
        { "A0", "A1", "A2", "A3", "B0", "B1", "B2", "B3", "Cin" };

    private readonly LogicGateFourBitAdderExampleTests.FourBitAdderFixture _fixture;

    /// <summary>Attaches the shared 4-bit-adder fixture.</summary>
    public LogicGateSignalNameBadgeRenderTests(LogicGateFourBitAdderExampleTests.FourBitAdderFixture fixture) =>
        _fixture = fixture;

    [AvaloniaFact]
    public async Task FourBitAdder_NamedSignalBadges_ShowOnInputGatesAndRender()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        var badges = _fixture.Canvas.LogicGateStates.Badges;
        var named = badges.Where(b => b.HasSignalName).ToList();
        named.Select(b => b.SignalName).Distinct().ShouldBe(NetworkSignals, ignoreOrder: true,
            customMessage: "every named operand signal shows on the canvas badges of its input gates");
        named.ShouldAllBe(b => b.LabelText == $"{b.SignalName} = {(b.IsOne ? "1" : "0")}");
        badges.Where(b => !b.HasSignalName).ShouldAllBe(
            b => b.PinName == "Y" && b.LabelText == b.BitText,
            "the anonymous per-output badges stay exactly as before");

        vm.Inputs.Single(i => i.PinName == "A2").IsOn = true;
        badges.Where(b => b.SignalName == "A2").ShouldAllBe(
            b => b.IsOne && b.LabelText == "A2 = 1",
            "a toggled signal flips its canvas badges with the panel toggle");

        // Render smoke: gate T0H1N5 reads A0 and B0 — its badge corner must paint three
        // chips (Y, A0, B0) with visible label text on the named rows.
        var group = _fixture.Groups.Single(g => g.GroupName == "T0H1N5");
        var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);
        using var bitmap = RenderBadgeCorner(_fixture.Canvas, bounds);
        var pixels = ReadPixels(bitmap);

        CountInRow(pixels, row: 0, (r, g, b) => r > 15 || g > 15 || b > 15)
            .ShouldBeGreaterThan(0, "the anonymous output badge of 'T0H1N5' renders");
        CountInRow(pixels, row: 1, IsGrayTextPixel)
            .ShouldBeGreaterThan(10, "the A0 chip paints its label text (inputs are off — gray)");
        CountInRow(pixels, row: 2, IsGrayTextPixel)
            .ShouldBeGreaterThan(10, "the B0 chip paints its label text (inputs are off — gray)");
    }

    [AvaloniaFact]
    public void NamedBadge_WidensTheChip_AnonymousBadgeKeepsItsExactSquare()
    {
        var canvas = new DesignCanvasViewModel();
        var group = TestComponentFactory.CreateComponentGroup("G");
        var child = TestComponentFactory.CreateStraightWaveGuide();
        child.PhysicalX = 0;
        child.PhysicalY = 0;
        child.WidthMicrometers = 100;
        child.HeightMicrometers = 60;
        group.AddChild(child);
        canvas.AddComponent(group);
        canvas.LogicGateStates.ShowStates(new[]
        {
            new LogicGateBadgeState("G", "Y", false),
            new LogicGateBadgeState("G", "A", true, "A0"),
        });
        var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);

        using var bitmap = RenderBadgeCorner(canvas, bounds);
        var pixels = ReadPixels(bitmap);

        var row0Left = LeftmostPainted(pixels, row: 0);
        row0Left.ShouldNotBeNull("the anonymous output badge renders");
        row0Left.Value.ShouldBeInRange(SquareLeftEdge - 2, SquareLeftEdge + 2,
            "the anonymous output badge keeps its exact 16px square at the group's right edge");
        var row1Left = LeftmostPainted(pixels, row: 1);
        row1Left.ShouldNotBeNull("the named badge renders");
        row1Left.Value.ShouldBeLessThan(SquareLeftEdge - 6,
            "the named badge widens left past the square to fit its 'A0 = 1' label");
        CountInRow(pixels, row: 1, (r, g, b) => g > 150 && g > r + 30)
            .ShouldBeGreaterThan(10, "the named badge paints its green 'A0 = 1' label text");
    }

    /// <summary>Gray badge text (the adder's inputs start off) — above the border's brightness.</summary>
    private static bool IsGrayTextPixel(byte r, byte g, byte b) => r > 110 && g > 110 && b > 110;

    /// <summary>
    /// Renders the badge chips of <paramref name="canvas"/> into a bitmap whose origin
    /// sits 70px left and 10px above the group's top-right corner, so badge rows land at
    /// the local coordinates documented on the class constants.
    /// </summary>
    private static RenderTargetBitmap RenderBadgeCorner(DesignCanvasViewModel canvas, Rect groupBounds)
    {
        var origin = new Point(groupBounds.Right - 70, groupBounds.Top - 10);
        var rc = new CanvasRenderContext
        {
            ViewModel = canvas,
            InteractionState = new CanvasInteractionState(),
            Zoom = 1.0,
            Bounds = new Rect(origin, new Size(BitmapWidth, BitmapHeight)),
        };
        var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, BitmapWidth, BitmapHeight));
            using (ctx.PushTransform(Matrix.CreateTranslation(-origin.X, -origin.Y)))
            {
                LogicGateStateBadgeRenderer.Render(ctx, rc);
            }
        }
        return bitmap;
    }

    /// <summary>The leftmost painted pixel column of one badge row, or null when the row is black.</summary>
    private static int? LeftmostPainted(byte[] pixels, int row)
    {
        for (var x = 0; x < BitmapWidth; x++)
        {
            for (var y = RowTop + row * RowPitch; y < RowTop + row * RowPitch + 16; y++)
            {
                if (IsPainted(pixels, x, y))
                    return x;
            }
        }
        return null;
    }

    /// <summary>Counts the pixels of one badge row matching the predicate.</summary>
    private static int CountInRow(byte[] pixels, int row, Func<byte, byte, byte, bool> matches)
    {
        var count = 0;
        for (var y = RowTop + row * RowPitch; y < RowTop + row * RowPitch + 16; y++)
        {
            for (var x = 0; x < BitmapWidth; x++)
            {
                var i = (y * BitmapWidth + x) * 4;
                if (matches(pixels[i + 2], pixels[i + 1], pixels[i]))
                    count++;
            }
        }
        return count;
    }

    private static bool IsPainted(byte[] pixels, int x, int y)
    {
        var i = (y * BitmapWidth + x) * 4;
        return pixels[i] > 15 || pixels[i + 1] > 15 || pixels[i + 2] > 15;
    }

    private static byte[] ReadPixels(RenderTargetBitmap bitmap)
    {
        var stride = BitmapWidth * 4;
        var buffer = Marshal.AllocHGlobal(stride * BitmapHeight);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, BitmapWidth, BitmapHeight), buffer, stride * BitmapHeight, stride);
            var bytes = new byte[stride * BitmapHeight];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
