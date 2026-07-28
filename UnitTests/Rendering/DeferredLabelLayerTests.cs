using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using CAP.Avalonia.Controls.Rendering;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Tests for <see cref="DeferredLabelLayer"/>: the flush expands every queued label into a
/// near-black halo copy offset one screen pixel down-right plus the label itself at its exact
/// origin with its original foreground — the cheap outline that keeps light label text
/// readable on whitish component fills without a background box, at no extra text-shaping cost
/// (both copies share the one <see cref="FormattedText"/>). <see cref="AvaloniaFactAttribute"/>
/// is required because building a <see cref="FormattedText"/> needs an initialized Avalonia
/// font manager. A <see cref="DrawingContext"/> cannot be faked (its constructor is internal
/// to Avalonia), so the assertions run against
/// <see cref="DeferredLabelLayer.BuildDrawOperations"/> — the exact sequence Flush replays.
/// </summary>
public class DeferredLabelLayerTests
{
    private static readonly Point Origin = new(40, 25);
    private static readonly IBrush Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 220, 255));

    [AvaloniaFact]
    public void EachLabel_ProducesDarkHaloThenUnchangedForeground_AtSameLayoutPosition()
    {
        var layer = new DeferredLabelLayer();
        var text = MakeText("label");
        layer.Enqueue(text, Foreground, Origin);

        var draws = layer.BuildDrawOperations(zoom: 1.0);

        draws.Count.ShouldBe(2, "every label must flush as halo copy + foreground copy");

        var haloBrush = draws[0].Brush.ShouldBeAssignableTo<ISolidColorBrush>();
        haloBrush.Color.R.ShouldBeLessThan((byte)40, "the halo must be near-black to outline light text on whitish fills");
        haloBrush.Color.G.ShouldBeLessThan((byte)40);
        haloBrush.Color.B.ShouldBeLessThan((byte)40);
        draws[0].Origin.ShouldBe(new Point(Origin.X + 1, Origin.Y + 1),
            "at zoom 1 the halo sits one world unit (= one screen pixel) down-right of the glyph origin");

        draws[1].Brush.ShouldBeSameAs(Foreground, "the visible copy keeps the label's original foreground");
        draws[1].Origin.ShouldBe(Origin, "the visible copy is drawn at the exact label position");

        draws[0].Text.ShouldBeSameAs(text);
        draws[1].Text.ShouldBeSameAs(text,
            "halo and foreground share the one shaped FormattedText — the flush must not re-measure text");
    }

    [AvaloniaFact]
    public void HaloOffset_ScalesInverselyWithZoom_ToStayOneScreenPixel()
    {
        var layer = new DeferredLabelLayer();
        layer.Enqueue(MakeText("label"), Foreground, Origin);

        layer.BuildDrawOperations(zoom: 2.0)[0].Origin.ShouldBe(new Point(Origin.X + 0.5, Origin.Y + 0.5));
        layer.BuildDrawOperations(zoom: 0.5)[0].Origin.ShouldBe(new Point(Origin.X + 2, Origin.Y + 2));
        layer.BuildDrawOperations(zoom: 0.0)[0].Origin.ShouldBe(new Point(Origin.X + 1, Origin.Y + 1),
            "a non-positive zoom falls back to 1.0 instead of dividing by zero");
    }

    [AvaloniaFact]
    public void MultipleLabels_KeepEnqueueOrder_EachWithHaloBeforeItsForeground()
    {
        var layer = new DeferredLabelLayer();
        var first = MakeText("first");
        var second = MakeText("second");
        layer.Enqueue(first, Brushes.White, new Point(0, 0));
        layer.Enqueue(second, Brushes.LightGray, new Point(10, 10));

        var draws = layer.BuildDrawOperations(zoom: 1.0);

        draws.Count.ShouldBe(4);
        draws[0].Text.ShouldBeSameAs(first);
        draws[1].Text.ShouldBeSameAs(first);
        draws[1].Brush.ShouldBeSameAs(Brushes.White);
        draws[2].Text.ShouldBeSameAs(second);
        draws[3].Text.ShouldBeSameAs(second);
        draws[3].Brush.ShouldBeSameAs(Brushes.LightGray);
        draws[3].Origin.ShouldBe(new Point(10, 10));
    }

    private static FormattedText MakeText(string text) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Arial"), 12, Brushes.White);
}
