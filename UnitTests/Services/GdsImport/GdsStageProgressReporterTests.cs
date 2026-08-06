using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for the throttled stage progress of huge GDS imports
/// (<see cref="GdsStageProgressReporter"/> + its use in
/// <see cref="GdsPlacementExecutor"/>): messages are forwarded at most once per
/// interval, counts increase, and every stage ends with its final count.
/// </summary>
public class GdsStageProgressReporterTests
{
    private sealed class ListProgress : IProgress<string>
    {
        public readonly List<string> Messages = new();
        public void Report(string value) => Messages.Add(value);
    }

    // ── Reporter unit behavior (fake clock) ──────────────────────────────────

    [Fact]
    public void Report_FirstAndFinal_AlwaysForward_IntermediateIsThrottled()
    {
        var progress = new ListProgress();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var reporter = new GdsStageProgressReporter(
            progress, "Placing components", TimeSpan.FromMilliseconds(300), () => now);

        reporter.Report(1, 4); // first call always forwards
        now += TimeSpan.FromMilliseconds(100);
        reporter.Report(2, 4); // inside the interval — suppressed
        now += TimeSpan.FromMilliseconds(250);
        reporter.Report(3, 4); // interval elapsed — forwarded
        now += TimeSpan.FromMilliseconds(50);
        reporter.Report(4, 4); // the final count ALWAYS forwards, interval or not

        progress.Messages.ShouldBe(new[]
        {
            "Placing components… 1/4",
            "Placing components… 3/4",
            "Placing components… 4/4",
        });
    }

    [Fact]
    public void Report_EmptyStage_ReportsNothing()
    {
        var progress = new ListProgress();
        var reporter = new GdsStageProgressReporter(progress, "Connecting pins", TimeSpan.Zero);

        reporter.Report(0, 0);

        progress.Messages.ShouldBeEmpty();
    }

    [Fact]
    public void Report_ZeroInterval_ForwardsEveryItem()
    {
        var progress = new ListProgress();
        var reporter = new GdsStageProgressReporter(progress, "Connecting pins", TimeSpan.Zero);

        reporter.Report(1, 3);
        reporter.Report(2, 3);
        reporter.Report(3, 3);

        progress.Messages.ShouldBe(new[]
        {
            "Connecting pins… 1/3",
            "Connecting pins… 2/3",
            "Connecting pins… 3/3",
        });
    }

    // ── Executor integration ─────────────────────────────────────────────────

    /// <summary>10×4 µm two-port waveguide template (mirrors GdsPlacementExecutorTests).</summary>
    private static ComponentTemplate WaveguideTemplate() => new()
    {
        Name = "wg",
        Category = "Test",
        PdkSource = "testpdk",
        WidthMicrometers = 10,
        HeightMicrometers = 4,
        PinDefinitions = new[]
        {
            new PinDefinition("in", 0, 2, 180),
            new PinDefinition("out", 10, 2, 0),
        },
        CreateSMatrix = pins => new SMatrix(
            pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(),
            new List<(Guid, double)>()),
    };

    [Fact]
    public async Task ExecuteAsync_StageProgress_IncreasingCounts_EndingWithFinalMessages()
    {
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => new[] { WaveguideTemplate() })
        {
            ProgressReportInterval = TimeSpan.Zero, // test seam: report every item
        };
        var placements = Enumerable.Range(0, 10)
            .Select(i => new GdsPlacementInstruction
            {
                InstanceName = $"wg#{i}",
                ComponentIdentifier = "wg",
                PdkSource = "testpdk",
                XUm = i * 10,
                YUm = 0,
            })
            .ToList();
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = placements,
            Connections = new[]
            {
                new GdsConnectionInstruction
                {
                    A = new GdsConnectionEndpoint { InstanceIndex = 0, PinName = "out" },
                    B = new GdsConnectionEndpoint { InstanceIndex = 1, PinName = "in" },
                },
            },
        };
        var progress = new ListProgress();

        await executor.ExecuteAsync(plan, progress);

        // Every stage's counter increases item by item and reaches its total.
        var placingCounts = progress.Messages
            .Where(m => m.StartsWith("Placing components…", StringComparison.Ordinal))
            .Select(m => int.Parse(m.Split('…')[1].Trim().Split('/')[0]))
            .ToList();
        placingCounts.ShouldBe(Enumerable.Range(1, 10).ToList());
        progress.Messages.ShouldContain("Placing components… 10/10");
        progress.Messages.ShouldContain("Connecting pins… 1/1");
        progress.Messages.ShouldContain(m => m.StartsWith("Grouping ", StringComparison.Ordinal),
            "the grouping stage keeps its one-shot summary message");
    }
}
