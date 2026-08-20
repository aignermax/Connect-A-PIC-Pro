using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Unit tests for the waveform mapping (issue #1129, rung 5 visualizer) — the
/// signal → lane-points projection pinned separately from any pixel: lane levels,
/// edge order, divider and cursor fractions, and the degenerate time ranges. All
/// cases hand the mapper a synthetic timeline, so no network or canvas is needed.
/// </summary>
public class LogicWaveformMapperTests
{
    private static readonly LogicPinRef QPin = new("G1", "Y");
    private static readonly LogicPinRef QBarPin = new("G2", "Y");

    [Fact]
    public void Build_TwoPhaseTimeline_MapsEdgesLevelsDividersAndCursor()
    {
        var timeline = new[]
        {
            new LogicTimelineEventViewModel(new LogicSwitchEvent(1.0, "G1", "Y", true)),
            new LogicTimelineEventViewModel(new LogicSwitchEvent(2.0, "G2", "Y", false))
            {
                ClockBoundaryText = "── clock #1 ──",
            },
            new LogicTimelineEventViewModel(new LogicSwitchEvent(4.0, "G1", "Y", false)),
        };
        var sources = new[]
        {
            new LogicWaveformLaneSource("EN", null, LiveLevel: true),
            new LogicWaveformLaneSource("Q", QPin, LiveLevel: false),
            new LogicWaveformLaneSource("QBAR", QBarPin, LiveLevel: false),
        };

        var model = LogicWaveformMapper.Build(sources, timeline, cursorTimePicoseconds: 2.0);

        model.StartTimePicoseconds.ShouldBe(0.0);
        model.EndTimePicoseconds.ShouldBe(4.0);

        var input = model.Lanes[0];
        input.SignalName.ShouldBe("EN");
        input.Edges.ShouldBeEmpty("an input lane holds its toggled level — no events target it");
        input.InitialLevel.ShouldBeTrue();
        input.LevelAt(0.0).ShouldBeTrue();
        input.LevelAt(1.0).ShouldBeTrue();

        var q = model.Lanes[1];
        q.InitialLevel.ShouldBeFalse("the lane rests at the opposite of its first edge's new level");
        q.Edges.Select(e => e.NewLevel).ShouldBe(new[] { true, false });
        q.Edges.Select(e => e.XFraction).ShouldBe(new[] { 0.25, 1.0 });
        q.LevelAt(0.2).ShouldBeFalse();
        q.LevelAt(0.25).ShouldBeTrue("an edge applies at its own x — the step is closed to the right");
        q.LevelAt(0.9).ShouldBeTrue();
        q.LevelAt(1.0).ShouldBeFalse();

        var qBar = model.Lanes[2];
        qBar.InitialLevel.ShouldBeTrue();
        qBar.Edges.ShouldHaveSingleItem().NewLevel.ShouldBeFalse();
        qBar.LevelAt(0.4).ShouldBeTrue();
        qBar.LevelAt(0.5).ShouldBeFalse();

        var divider = model.Dividers.ShouldHaveSingleItem();
        divider.Label.ShouldBe("── clock #1 ──", "the divider reuses the timeline row's localized label");
        divider.TimePicoseconds.ShouldBe(2.0);
        divider.XFraction.ShouldBe(0.5);

        model.CursorXFraction.ShouldBe(0.5);
    }

    [Fact]
    public void Build_EdgeFractionsAreNonDecreasingAlongEveryLane()
    {
        var timeline = new[]
        {
            new LogicTimelineEventViewModel(new LogicSwitchEvent(0.0, "G1", "Y", true)),
            new LogicTimelineEventViewModel(new LogicSwitchEvent(0.0, "G2", "Y", true)),
            new LogicTimelineEventViewModel(new LogicSwitchEvent(3.0, "G1", "Y", false)),
            new LogicTimelineEventViewModel(new LogicSwitchEvent(6.0, "G1", "Y", true)),
        };
        var sources = new[]
        {
            new LogicWaveformLaneSource("Q", QPin, LiveLevel: true),
            new LogicWaveformLaneSource("QBAR", QBarPin, LiveLevel: true),
        };

        var model = LogicWaveformMapper.Build(sources, timeline, cursorTimePicoseconds: null);

        foreach (var lane in model.Lanes)
        {
            var fractions = lane.Edges.Select(e => e.XFraction).ToList();
            fractions.ShouldBe(fractions.OrderBy(f => f).ToList(),
                $"lane '{lane.SignalName}' must keep monotone x — the timeline arrives in time order");
        }
    }

    [Fact]
    public void Build_EmptyTimeline_EveryLaneRestsAtItsLiveLevel()
    {
        var sources = new[]
        {
            new LogicWaveformLaneSource("IN", null, LiveLevel: false),
            new LogicWaveformLaneSource("Q", QPin, LiveLevel: true),
        };

        var model = LogicWaveformMapper.Build(sources, Array.Empty<LogicTimelineEventViewModel>(), null);

        model.Lanes.Count.ShouldBe(2);
        foreach (var lane in model.Lanes)
        {
            lane.Edges.ShouldBeEmpty();
            lane.InitialLevel.ShouldBe(lane.LiveLevel);
            lane.LevelAt(0.5).ShouldBe(lane.LiveLevel);
        }
        model.Dividers.ShouldBeEmpty();
        model.CursorXFraction.ShouldBeNull();
        model.EndTimePicoseconds.ShouldBeGreaterThan(model.StartTimePicoseconds,
            "even an empty timeline keeps a usable x range");
    }

    [Fact]
    public void Build_SingleInstantTimeline_NormalizesWithoutDividingByZero()
    {
        var timeline = new[]
        {
            new LogicTimelineEventViewModel(new LogicSwitchEvent(0.0, "G1", "Y", true))
            {
                ClockBoundaryText = "── clock #1 ──",
            },
        };
        var sources = new[] { new LogicWaveformLaneSource("Q", QPin, LiveLevel: true) };

        var model = LogicWaveformMapper.Build(sources, timeline, null);

        model.EndTimePicoseconds.ShouldBe(1.0, "a degenerate range widens to start + 1");
        model.Lanes[0].Edges.ShouldHaveSingleItem().XFraction.ShouldBe(0.0);
        model.Dividers.ShouldHaveSingleItem().XFraction.ShouldBe(0.0);
    }

    [Fact]
    public void Build_NoCursor_KeepsTheCursorUnset()
    {
        var timeline = new[]
        {
            new LogicTimelineEventViewModel(new LogicSwitchEvent(2.0, "G1", "Y", true)),
        };
        var sources = new[] { new LogicWaveformLaneSource("Q", QPin, LiveLevel: true) };

        var model = LogicWaveformMapper.Build(sources, timeline, cursorTimePicoseconds: null);

        model.CursorXFraction.ShouldBeNull("at the live end state no cursor is drawn");
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        var sources = Array.Empty<LogicWaveformLaneSource>();
        var timeline = Array.Empty<LogicTimelineEventViewModel>();

        Should.Throw<ArgumentNullException>(() => LogicWaveformMapper.Build(null!, timeline, null));
        Should.Throw<ArgumentNullException>(() => LogicWaveformMapper.Build(sources, null!, null));
    }
}
