using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Honesty check for the "Watch it compute" tour texts (issue #1143) against the
/// shipped <c>examples/Logic Gate Counter 2-bit.lun</c>: the tour tells the user
/// that after one "Step clock" press the C bus row reads 1 — so the real example,
/// built through the real panel path, must show exactly that. Runs the whole tour
/// flow on the real design as an end-to-end guard.
/// </summary>
public class WatchTourCounterExampleTests : IClassFixture<LogicGateCounter2BitExampleTests.CounterFixture>
{
    private readonly LogicGateCounter2BitExampleTests.CounterFixture _fixture;

    /// <summary>Attaches the shared loaded Counter example (already network-assembled once).</summary>
    public WatchTourCounterExampleTests(LogicGateCounter2BitExampleTests.CounterFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task FirstClockStep_CBusReadsOne_TourAdvancesThroughWholeFlow()
    {
        var logic = new LogicPanelViewModel();
        logic.Configure(_fixture.Canvas);
        await logic.BuildNetworkCommand.ExecuteAsync(null);
        logic.HasNetwork.ShouldBeTrue(logic.StatusText);
        logic.HasRegisters.ShouldBeTrue("the Counter has two register gates");

        var tour = new WatchComputeTourViewModel(logic);
        tour.Start();
        tour.CurrentStepIndex.ShouldBe(1, "the pre-built network satisfies the build step immediately");

        var cBus = logic.OutputRows.OfType<LogicSignalBusOutputViewModel>().Single(b => b.Prefix == "C");
        cBus.DecimalValue.ShouldBe(0, "registers power up cleared — the counter starts at 0");

        // The tour's step-2 promise: one Step press and the C bus reads 1.
        logic.StepClockCommand.Execute(null);

        tour.CurrentStepIndex.ShouldBe(2, "the step advanced on the real clock-step signal");
        logic.ClockStepCount.ShouldBe(1);
        logic.OutputRows.OfType<LogicSignalBusOutputViewModel>().Single(b => b.Prefix == "C")
            .DecimalValue.ShouldBe(1, "the tour text says the C bus reads 1 after the first step");

        // Steps 3–4 on the real signals: Run starts, Stop halts.
        logic.ToggleRunCommand.Execute(null);
        tour.CurrentStepIndex.ShouldBe(3, "Run started");
        logic.ToggleRunCommand.Execute(null);
        tour.CurrentStepIndex.ShouldBe(4, "Run stopped");
    }
}
