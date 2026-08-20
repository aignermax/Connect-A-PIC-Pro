using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;
using Shouldly;
using UnitTests.Integration;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Render smoke tests for the Logic panel's waveform strip (issue #1129, rung 5
/// visualizer): the production <see cref="LogicWaveformRenderer"/> draws the
/// panel's waveform model into a <see cref="RenderTargetBitmap"/> and the test
/// samples real pixels — same pattern as <see cref="LogicGateRegisterMarkerRenderTests"/>.
/// On the shipped SR latch one clocked step shows four lanes (S̄, R̄, Q, Q̄), a
/// trace edge at the commit entry's x position, and the clock divider line; on
/// the combinational full adder the settle timeline draws its five named lanes
/// with no divider at all.
/// </summary>
public class LogicWaveformStripRenderTests
    : IClassFixture<LogicGateSrLatchExampleTests.SrLatchFixture>,
      IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private const string SetSignal = "S̄";
    private const string ResetSignal = "R̄";

    private readonly LogicGateSrLatchExampleTests.SrLatchFixture _latch;
    private readonly LogicGateFullAdderExampleTests.FullAdderFixture _adder;

    /// <summary>Attaches the shared example fixtures.</summary>
    public LogicWaveformStripRenderTests(
        LogicGateSrLatchExampleTests.SrLatchFixture latch,
        LogicGateFullAdderExampleTests.FullAdderFixture adder)
    {
        _latch = latch;
        _adder = adder;
    }

    [AvaloniaFact]
    public async Task SrLatch_AfterOneStep_LanesEdgeAndDividerRender()
    {
        var vm = await BuildLatchAtRest();
        vm.Inputs.Single(i => i.PinName == SetSignal).IsOn = false;
        vm.StepClockCommand.Execute(null);

        var model = vm.Waveform.ShouldNotBeNull("the stepped latch has a waveform");
        model.Lanes.Count.ShouldBe(4, "one lane per named signal: S̄, R̄, Q, Q̄");
        model.Lanes.Take(2).Select(l => l.SignalName).ShouldBe(
            new[] { SetSignal, ResetSignal }, ignoreOrder: true,
            customMessage: "the named inputs lead the lane order");
        model.Lanes.Skip(2).Select(l => l.SignalName).ShouldBe(
            new[] { "Q", "Q̄" }, ignoreOrder: true,
            customMessage: "the register outputs follow under their tap names");
        model.Dividers.ShouldHaveSingleItem("one stepped clock drew one boundary");

        const int width = 320;
        var pixels = RenderStrip(model, width, out var height);
        var lanes = model.Lanes.ToList();

        for (var lane = 0; lane < model.Lanes.Count; lane++)
        {
            CountGreen(pixels, width,
                    (int)LogicWaveformRenderer.TraceLeft, width - (int)LogicWaveformRenderer.RightPadding,
                    (int)LogicWaveformRenderer.LaneBandTop(lane) + 1,
                    (int)LogicWaveformRenderer.LaneBandTop(lane) + (int)LogicWaveformRenderer.LaneHeight - 1)
                .ShouldBeGreaterThan(20, $"lane '{model.Lanes[lane].SignalName}' must paint its trace");
        }

        var qLane = lanes.Single(l => l.SignalName == "Q");
        var edge = qLane.Edges.ShouldHaveSingleItem("Q commits exactly once on the first clock");
        edge.NewLevel.ShouldBeTrue();
        var edgeX = (int)Math.Round(LogicWaveformRenderer.MapX(edge.XFraction, width));
        var qIndex = lanes.IndexOf(qLane);
        var yHigh = (int)LogicWaveformRenderer.LaneHighY(qIndex);
        var yLow = (int)LogicWaveformRenderer.LaneLowY(qIndex);
        CountGreen(pixels, width, edgeX - 1, edgeX + 1, yHigh, yLow)
            .ShouldBeGreaterThan(6, "a vertical edge renders at the commit entry's x position");
        CountGreen(pixels, width, width - 20, width - 14, yHigh - 1, yHigh + 1)
            .ShouldBeGreaterThan(0, "right of the commit Q rests high");
        CountGreen(pixels, width, width - 20, width - 14, yLow - 1, yLow + 1)
            .ShouldBe(0, "right of the commit nothing remains on Q's low line");

        var dividerX = (int)Math.Round(LogicWaveformRenderer.MapX(model.Dividers[0].XFraction, width));
        CountGold(pixels, width, dividerX - 1, dividerX + 1, 2, height - 6)
            .ShouldBeGreaterThan(6, "the clock boundary draws its vertical line across the lanes");
    }

    [AvaloniaFact]
    public async Task FullAdder_SettleTimeline_LanesRenderWithoutDividers()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_adder.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasRegisters.ShouldBeFalse("the full adder is purely combinational");

        vm.Inputs.Single(i => i.PinName == "A").IsOn = true;

        var model = vm.Waveform.ShouldNotBeNull("the settle timeline has a waveform");
        model.Lanes.Select(l => l.SignalName).ShouldBe(
            new[] { "A", "B", "Cin", "S", "Cout" }, ignoreOrder: true,
            customMessage: "the named inputs and taps each get a lane");
        model.Dividers.ShouldBeEmpty("a combinational settle has no clock boundaries");

        const int width = 420;
        var pixels = RenderStrip(model, width, out var height);
        var lanes = model.Lanes.ToList();

        CountGold(pixels, width, 0, width - 1, 0, height - 1)
            .ShouldBe(0, "no divider pixels may render without a clock step");

        var sLane = lanes.Single(l => l.SignalName == "S");
        var sEdge = sLane.Edges.ShouldHaveSingleItem("the sum rises exactly once for A=1, B=Cin=0");
        sEdge.NewLevel.ShouldBeTrue();
        sLane.InitialLevel.ShouldBeFalse();
        var sIndex = lanes.IndexOf(sLane);
        var sHigh = (int)LogicWaveformRenderer.LaneHighY(sIndex);
        var sLow = (int)LogicWaveformRenderer.LaneLowY(sIndex);
        var sEdgeX = LogicWaveformRenderer.MapX(sEdge.XFraction, width);
        sEdgeX.ShouldBeGreaterThan(LogicWaveformRenderer.TraceLeft + 6,
            "the sum settles one gate delay after the toggle — its edge lands inside the trace area");
        var midX = (int)(LogicWaveformRenderer.TraceLeft + (sEdgeX - LogicWaveformRenderer.TraceLeft) / 2);
        CountGreen(pixels, width, midX - 2, midX + 2, sLow - 1, sLow + 1)
            .ShouldBeGreaterThan(0, "before its edge the sum lane rests low");
        CountGreen(pixels, width, midX - 2, midX + 2, sHigh - 1, sHigh + 1)
            .ShouldBe(0, "before its edge the sum lane has no high segment");
        CountGreen(pixels, width, (int)sEdgeX - 1, (int)sEdgeX + 1, sHigh, sLow)
            .ShouldBeGreaterThan(6, "the sum's edge renders as a vertical step");

        var aIndex = lanes.IndexOf(lanes.Single(l => l.SignalName == "A"));
        lanes[aIndex].Edges.ShouldBeEmpty("the toggled input holds its level");
        CountGreen(pixels, width, 100, 104,
                (int)LogicWaveformRenderer.LaneHighY(aIndex) - 1,
                (int)LogicWaveformRenderer.LaneHighY(aIndex) + 1)
            .ShouldBeGreaterThan(0, "the toggled input lane rests high");
        CountGreen(pixels, width, 100, 104,
                (int)LogicWaveformRenderer.LaneLowY(aIndex) - 1,
                (int)LogicWaveformRenderer.LaneLowY(aIndex) + 1)
            .ShouldBe(0, "the toggled input lane never touches the low line");

        var coutIndex = lanes.IndexOf(lanes.Single(l => l.SignalName == "Cout"));
        lanes[coutIndex].Edges.ShouldBeEmpty("majority(1,0,0) keeps Cout quiet");
        CountGreen(pixels, width, 100, 104,
                (int)LogicWaveformRenderer.LaneLowY(coutIndex) - 1,
                (int)LogicWaveformRenderer.LaneLowY(coutIndex) + 1)
            .ShouldBeGreaterThan(0, "the quiet carry lane rests low");
    }

    [AvaloniaFact]
    public async Task FullAdder_ReplayCursor_DrawsAtTheSelectedInstant()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_adder.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.Inputs.Single(i => i.PinName == "A").IsOn = true;
        var row = vm.TimelineEvents[vm.TimelineEvents.Count / 2];
        vm.SelectTimelineEventCommand.Execute(row);

        var model = vm.Waveform.ShouldNotBeNull();
        var cursor = model.CursorXFraction.ShouldNotBeNull("replaying marks the instant on the strip");

        const int width = 420;
        var pixels = RenderStrip(model, width, out var height);
        var cursorX = (int)Math.Round(LogicWaveformRenderer.MapX(cursor, width));
        CountWhite(pixels, width, cursorX - 1, cursorX + 1, 2, height - 6)
            .ShouldBeGreaterThan(4, "the replay cursor draws its line across the strip");
    }

    /// <summary>Builds the latch's network on the fixture canvas and rests both active-low inputs high.</summary>
    private async Task<LogicPanelViewModel> BuildLatchAtRest()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_latch.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.Inputs.Single(i => i.PinName == SetSignal).IsOn = true;
        vm.Inputs.Single(i => i.PinName == ResetSignal).IsOn = true;
        return vm;
    }

    /// <summary>Renders the strip on a black backing and returns the BGRA pixels.</summary>
    private static byte[] RenderStrip(LogicWaveformModel model, int width, out int height)
    {
        height = (int)LogicWaveformRenderer.DesiredHeight(model);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, width, height));
            LogicWaveformRenderer.Render(ctx, model, new Size(width, height));
        }
        return ReadPixels(bitmap, width, height);
    }

    /// <summary>Counts trace-green pixels in the inclusive rectangle — green channel clearly dominates.</summary>
    private static int CountGreen(byte[] pixels, int width, int x0, int x1, int y0, int y1) =>
        CountWhere(pixels, width, x0, x1, y0, y1,
            (b, g, r) => g > 120 && g > r + 25 && g > b + 25);

    /// <summary>Counts divider-gold pixels in the inclusive rectangle — red and green over a weak blue.</summary>
    private static int CountGold(byte[] pixels, int width, int x0, int x1, int y0, int y1) =>
        CountWhere(pixels, width, x0, x1, y0, y1,
            (b, g, r) => r > 120 && g > 100 && r > b + 40 && g > b + 20);

    /// <summary>Counts cursor-white pixels in the inclusive rectangle — every channel near full.</summary>
    private static int CountWhite(byte[] pixels, int width, int x0, int x1, int y0, int y1) =>
        CountWhere(pixels, width, x0, x1, y0, y1,
            (b, g, r) => r > 200 && g > 200 && b > 200);

    /// <summary>Counts BGRA pixels matching <paramref name="match"/> in the inclusive rectangle.</summary>
    private static int CountWhere(
        byte[] pixels, int width, int x0, int x1, int y0, int y1,
        Func<byte, byte, byte, bool> match)
    {
        var count = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var i = (y * width + x) * 4;
                if (match(pixels[i], pixels[i + 1], pixels[i + 2]))
                    count++;
            }
        }
        return count;
    }

    private static byte[] ReadPixels(RenderTargetBitmap bitmap, int width, int height)
    {
        var stride = width * 4;
        var buffer = Marshal.AllocHGlobal(stride * height);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, stride * height, stride);
            var bytes = new byte[stride * height];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
