using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;
using Shouldly;
using static UnitTests.Helpers.LogicRingTestFixture;

namespace UnitTests.Onboarding.FirstStepsTutorial;

/// <summary>
/// Tests for the "Watch it compute" guided-tour engine (issue #1143, slice 2 of
/// #769): the tour observes the real Logic panel signals — network built, clock
/// stepped, Run started and stopped — and advances exactly on those, never on
/// arbitrary clicks. Drives the engine against the two-register ring fixture
/// (same sequential circuit as the Logic panel's step/run tests), headless.
/// </summary>
public class WatchComputeTourViewModelTests
{
    private static async Task<(LogicPanelViewModel logic, WatchComputeTourViewModel tour)> CreateBuiltFixture()
    {
        var canvas = RingCanvas();
        var logic = new LogicPanelViewModel(new FakeLogicRunClock());
        logic.Configure(canvas);
        await logic.BuildNetworkCommand.ExecuteAsync(null);
        logic.HasNetwork.ShouldBeTrue(logic.StatusText);
        logic.HasRegisters.ShouldBeTrue("the ring has two registers, so Step/Run are enabled");
        return (logic, new WatchComputeTourViewModel(logic));
    }

    private static WatchComputeTourViewModel CreateTour(out LogicPanelViewModel logic)
    {
        logic = new LogicPanelViewModel(new FakeLogicRunClock());
        return new WatchComputeTourViewModel(logic);
    }

    [Fact]
    public void InitialState_IsInactive_AtFirstStep()
    {
        var tour = CreateTour(out _);

        tour.IsActive.ShouldBeFalse();
        tour.IsCompleted.ShouldBeFalse();
        tour.CurrentStepIndex.ShouldBe(0);
        tour.Steps.Count.ShouldBe(5);
    }

    [Fact]
    public void Start_ActivatesTour_AtBuildStep()
    {
        var tour = CreateTour(out _);

        tour.Start();

        tour.IsActive.ShouldBeTrue();
        tour.CurrentStepIndex.ShouldBe(0);
        tour.ProgressText.ShouldContain("1/5");
        tour.CurrentTitle.ShouldNotBeNullOrWhiteSpace();
        tour.CurrentTitle.ShouldNotBe(tour.CurrentStep.TitleKey, "title must be localized, not the raw key");
        tour.CurrentBody.ShouldNotBe(tour.CurrentStep.BodyKey, "body must be localized, not the raw key");
    }

    [Fact]
    public async Task FullTour_BuildStepRunStop_Completes()
    {
        var (logic, tour) = await CreateBuiltFixture();
        tour.Start();

        tour.CurrentStepIndex.ShouldBe(1,
            "the Counter example is already built when the tour starts, so the build step is satisfied immediately");

        // Step 2 — press Step clock once (the seeded ring flips a register, so the step lands).
        logic.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        logic.StepClockCommand.Execute(null);

        tour.CurrentStepIndex.ShouldBe(2, "one clock step must advance to the run step");
        logic.ClockStepCount.ShouldBe(1);

        // Step 3 — press Run; the step completes on the running transition.
        logic.ToggleRunCommand.Execute(null);
        logic.IsRunning.ShouldBeTrue();

        tour.CurrentStepIndex.ShouldBe(3, "starting Run must advance to the stop step");

        // Step 4 — press Stop (second ToggleRun press).
        logic.ToggleRunCommand.Execute(null);
        logic.IsRunning.ShouldBeFalse();

        tour.CurrentStepIndex.ShouldBe(4, "stopping Run must advance to the closing step");
        tour.IsCompleted.ShouldBeFalse("the closing words step completes via Next");

        // Step 5 — closing words; Next finishes the tour.
        tour.NextCommand.Execute(null);

        tour.IsCompleted.ShouldBeTrue();
        tour.IsActive.ShouldBeFalse("the overlay hides once the tour completes");
    }

    [Fact]
    public async Task StopWithoutPriorRun_DoesNotAdvance()
    {
        var (logic, tour) = await CreateBuiltFixture();
        tour.Start();
        logic.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        logic.StepClockCommand.Execute(null);
        tour.CurrentStepIndex.ShouldBe(2);

        // IsRunning drops without a preceding Run (e.g. a design edit cleared the
        // network while stopped): no false advance to the closing step.
        logic.HasNetwork = false;

        tour.CurrentStepIndex.ShouldBe(2, "the stop step must only complete after Run was actually started");
    }

    [Fact]
    public async Task QuietClockStep_DoesNotAdvance()
    {
        var (logic, tour) = await CreateBuiltFixture();
        tour.Start();

        // A clock that changes nothing appends no timeline block — the step
        // counter stays at 0 and the tour honestly waits for a visible step.
        logic.StepClockCommand.Execute(null);

        logic.ClockStepCount.ShouldBe(0);
        tour.CurrentStepIndex.ShouldBe(1, "a quiet clock must not count as the tour's step");
    }

    [Fact]
    public async Task SecondStep_DoesNotSkipRunStep()
    {
        var (logic, tour) = await CreateBuiltFixture();
        tour.Start();
        logic.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        logic.StepClockCommand.Execute(null);
        tour.CurrentStepIndex.ShouldBe(2);

        logic.StepClockCommand.Execute(null);

        tour.CurrentStepIndex.ShouldBe(2, "further clock steps must not skip the Run step");
    }

    [Fact]
    public void Next_AdvancesManually_WithoutCompletionCondition()
    {
        var tour = CreateTour(out _);
        tour.Start();

        tour.NextCommand.Execute(null);
        tour.CurrentStepIndex.ShouldBe(1);

        tour.NextCommand.Execute(null);
        tour.CurrentStepIndex.ShouldBe(2);
        tour.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Next_OnLastStep_CompletesTour()
    {
        var tour = CreateTour(out _);
        tour.Start();

        for (var i = 0; i < 5; i++)
            tour.NextCommand.Execute(null);

        tour.IsCompleted.ShouldBeTrue();
        tour.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Skip_ExitsTour_LaterPanelChangesDoNotAdvance()
    {
        var (logic, tour) = await CreateBuiltFixture();
        tour.Start();
        tour.SkipCommand.Execute(null);

        tour.IsActive.ShouldBeFalse();
        tour.IsCompleted.ShouldBeFalse();

        logic.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        logic.StepClockCommand.Execute(null);
        logic.ToggleRunCommand.Execute(null);

        tour.CurrentStepIndex.ShouldBe(1, "a skipped tour must not react to the panel anymore");
        tour.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task CompletedTour_DetachesFromPanel_LaterChangesDoNotThrow()
    {
        var (logic, tour) = await CreateBuiltFixture();
        tour.Start();
        for (var i = 0; i < 5; i++)
            tour.NextCommand.Execute(null);

        logic.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        logic.StepClockCommand.Execute(null);
        logic.ToggleRunCommand.Execute(null);
        logic.ToggleRunCommand.Execute(null);

        tour.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Restart_ResetsProgress_AndReevaluates()
    {
        var (logic, tour) = await CreateBuiltFixture();
        tour.Start();
        logic.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        logic.StepClockCommand.Execute(null);
        tour.SkipCommand.Execute(null);

        tour.Start();

        tour.IsActive.ShouldBeTrue();
        tour.IsCompleted.ShouldBeFalse();
        tour.CurrentStepIndex.ShouldBe(2,
            "the built network and the elapsed clock step still satisfy the first two steps");
    }

    /// <summary>
    /// Manually fired <see cref="ILogicRunClock"/> — mirrors the fake in the Logic
    /// panel's run-mode tests; the tour never fires ticks itself, but the panel
    /// requires a clock instance.
    /// </summary>
    private sealed class FakeLogicRunClock : ILogicRunClock
    {
        public event EventHandler? Tick;

        public void Start(TimeSpan interval)
        {
        }

        public void Stop()
        {
        }

        public void FireTick() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
