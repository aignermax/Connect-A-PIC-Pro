using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;
using Shouldly;

namespace UnitTests.Onboarding.FirstStepsTutorial;

/// <summary>
/// Tests for the first-steps guided-tour engine (issue #1080, slice 1 of #769).
/// Drives the engine through all three steps by mutating a real canvas fixture
/// (place → connect → simulate) and asserts step advance, completion, and Skip
/// behaviour — headless, no UI automation.
/// </summary>
public class TutorialViewModelTests
{
    private static (DesignCanvasViewModel canvas, TutorialViewModel tutorial) CreateFixture()
    {
        var canvas = new DesignCanvasViewModel();
        var tutorial = new TutorialViewModel(canvas);
        return (canvas, tutorial);
    }

    [Fact]
    public void InitialState_IsInactive_AtFirstStep()
    {
        var (_, tutorial) = CreateFixture();

        tutorial.IsActive.ShouldBeFalse();
        tutorial.IsCompleted.ShouldBeFalse();
        tutorial.CurrentStepIndex.ShouldBe(0);
        tutorial.Steps.Count.ShouldBe(3);
    }

    [Fact]
    public void Start_ActivatesTour_AtFirstStep()
    {
        var (_, tutorial) = CreateFixture();

        tutorial.Start();

        tutorial.IsActive.ShouldBeTrue();
        tutorial.CurrentStepIndex.ShouldBe(0);
        tutorial.ProgressText.ShouldContain("1/3");
        tutorial.CurrentTitle.ShouldNotBeNullOrWhiteSpace();
        tutorial.CurrentTitle.ShouldNotBe(tutorial.CurrentStep.TitleKey, "title must be localized, not the raw key");
        tutorial.CurrentBody.ShouldNotBe(tutorial.CurrentStep.BodyKey, "body must be localized, not the raw key");
    }

    [Fact]
    public void PlacingComponent_AdvancesToConnectStep()
    {
        var (canvas, tutorial) = CreateFixture();
        tutorial.Start();

        canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide());

        tutorial.CurrentStepIndex.ShouldBe(1);
        tutorial.ProgressText.ShouldContain("2/3");
        tutorial.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task FullTour_PlaceConnectSimulate_Completes()
    {
        var (canvas, tutorial) = CreateFixture();
        tutorial.Start();

        // Step 1 — place any component (two needed for the connect step).
        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.WidthMicrometers = 250;
        startComp.HeightMicrometers = 250;
        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.WidthMicrometers = 250;
        endComp.HeightMicrometers = 250;
        endComp.PhysicalX = 400;
        endComp.PhysicalY = 300;
        canvas.AddComponent(startComp);
        tutorial.CurrentStepIndex.ShouldBe(1, "placing must advance to the connect step");
        canvas.AddComponent(endComp);
        tutorial.CurrentStepIndex.ShouldBe(1, "placing more components must not skip the connect step");

        // Step 2 — connect two pins.
        var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
        var endPin = endComp.PhysicalPins.First(p => p.Name == "in");
        var connection = await canvas.ConnectPinsAsync(startPin, endPin);
        connection.ShouldNotBeNull();
        tutorial.CurrentStepIndex.ShouldBe(2, "connecting must advance to the simulate step");

        // Step 3 — run the simulation (light-propagation overlay on).
        canvas.ShowPowerFlow = true;

        tutorial.IsCompleted.ShouldBeTrue();
        tutorial.IsActive.ShouldBeFalse("the overlay hides once the tour completes");
    }

    [Fact]
    public void Next_AdvancesManually_WithoutCompletionCondition()
    {
        var (_, tutorial) = CreateFixture();
        tutorial.Start();

        tutorial.NextCommand.Execute(null);
        tutorial.CurrentStepIndex.ShouldBe(1);

        tutorial.NextCommand.Execute(null);
        tutorial.CurrentStepIndex.ShouldBe(2);
        tutorial.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Next_OnLastStep_CompletesTour()
    {
        var (_, tutorial) = CreateFixture();
        tutorial.Start();

        tutorial.NextCommand.Execute(null);
        tutorial.NextCommand.Execute(null);
        tutorial.NextCommand.Execute(null);

        tutorial.IsCompleted.ShouldBeTrue();
        tutorial.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Skip_ExitsTour_WithoutCompleting()
    {
        var (_, tutorial) = CreateFixture();
        tutorial.Start();

        tutorial.SkipCommand.Execute(null);

        tutorial.IsActive.ShouldBeFalse();
        tutorial.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Skip_DetachesFromCanvas_LaterChangesDoNotAdvance()
    {
        var (canvas, tutorial) = CreateFixture();
        tutorial.Start();
        tutorial.SkipCommand.Execute(null);

        canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide());
        canvas.ShowPowerFlow = true;

        tutorial.CurrentStepIndex.ShouldBe(0);
        tutorial.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task CompletedTour_DetachesFromCanvas_LaterChangesDoNotThrow()
    {
        var (canvas, tutorial) = CreateFixture();
        tutorial.Start();
        tutorial.NextCommand.Execute(null);
        tutorial.NextCommand.Execute(null);
        tutorial.NextCommand.Execute(null);

        // Canvas teardown after completion must not touch the finished tour.
        canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide());
        canvas.ShowPowerFlow = true;
        await Task.CompletedTask;

        tutorial.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void Start_OnNonEmptyCanvas_SkipsAlreadySatisfiedSteps()
    {
        var (canvas, tutorial) = CreateFixture();
        canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide());

        tutorial.Start();

        tutorial.CurrentStepIndex.ShouldBe(1, "an already-placed component satisfies step 1 immediately");
    }

    [Fact]
    public void Restart_ResetsProgress_AndReevaluates()
    {
        var (canvas, tutorial) = CreateFixture();
        tutorial.Start();
        tutorial.NextCommand.Execute(null);
        tutorial.SkipCommand.Execute(null);

        tutorial.Start();

        tutorial.IsActive.ShouldBeTrue();
        tutorial.IsCompleted.ShouldBeFalse();
        tutorial.CurrentStepIndex.ShouldBe(0, "fresh canvas restart begins at step 1");
    }
}
