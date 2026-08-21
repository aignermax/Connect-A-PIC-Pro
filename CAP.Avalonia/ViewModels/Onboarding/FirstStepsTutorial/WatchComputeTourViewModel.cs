using System.ComponentModel;
using System.Globalization;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;

/// <summary>
/// Step engine for the "Watch it compute" guided tour (issue #1143, slice 2 of
/// #769): the Counter example is loaded by the Home entry point, then an ordered
/// list of steps observes the live Logic panel — network built, clock stepped,
/// Run started and stopped — and advances automatically as each task is
/// completed. Plain observable state with no view dependency, so the whole tour
/// can be driven headlessly from unit tests against a real
/// <see cref="LogicPanelViewModel"/>.
/// </summary>
public partial class WatchComputeTourViewModel : ObservableObject
{
    /// <summary>File name of the shipped example the tour opens.</summary>
    public const string CounterExampleFileName = "Logic Gate Counter 2-bit.lun";

    private readonly LogicPanelViewModel _logic;

    /// <summary>True once Run was pressed — the stop step completes on the falling edge of IsRunning, not its level.</summary>
    private bool _sawRunStarted;

    /// <summary>True while the tour overlay is shown and the engine is observing the Logic panel.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Zero-based index of the step the user is currently asked to perform.</summary>
    [ObservableProperty]
    private int _currentStepIndex;

    /// <summary>True once the final step was completed (or Next was pressed on it).</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>
    /// Builds the tour steps against the given Logic panel: build the network,
    /// step the clock once, run the auto-clock, stop it again, closing words.
    /// </summary>
    public WatchComputeTourViewModel(LogicPanelViewModel logic)
    {
        _logic = logic;
        Steps = new List<TutorialStep>
        {
            new("WatchTour.Step1Title", "WatchTour.Step1Body", () => _logic.HasNetwork),
            new("WatchTour.Step2Title", "WatchTour.Step2Body", () => _logic.ClockStepCount > 0),
            new("WatchTour.Step3Title", "WatchTour.Step3Body", () => _logic.IsRunning),
            new("WatchTour.Step4Title", "WatchTour.Step4Body", () => _sawRunStarted && !_logic.IsRunning),
            new("WatchTour.Step5Title", "WatchTour.Step5Body", () => false),
        };
    }

    /// <summary>The ordered steps of this tour chapter.</summary>
    public IReadOnlyList<TutorialStep> Steps { get; }

    /// <summary>The step the user is currently asked to perform.</summary>
    public TutorialStep CurrentStep => Steps[CurrentStepIndex];

    /// <summary>Localized title of the current step.</summary>
    public string CurrentTitle => LocalizationService.Instance.Translate(CurrentStep.TitleKey);

    /// <summary>Localized body text of the current step.</summary>
    public string CurrentBody => LocalizationService.Instance.Translate(CurrentStep.BodyKey);

    /// <summary>Localized position label, e.g. "Step 2/5".</summary>
    public string ProgressText => string.Format(
        CultureInfo.InvariantCulture, "{0} {1}/{2}",
        LocalizationService.Instance.Translate("Tutorial.Step"),
        CurrentStepIndex + 1, Steps.Count);

    /// <summary>Starts the tour at the first step and begins observing the Logic panel.</summary>
    public void Start()
    {
        Detach();
        CurrentStepIndex = 0;
        IsCompleted = false;
        _sawRunStarted = false;
        IsActive = true;
        _logic.PropertyChanged += OnLogicPropertyChanged;
        EvaluateCurrentStep();
    }

    /// <summary>Advances to the next step manually; on the last step the tour completes.</summary>
    [RelayCommand]
    public void Next()
    {
        if (!IsActive)
            return;
        Advance();
    }

    /// <summary>Exits the tour without completing it.</summary>
    [RelayCommand]
    public void Skip()
    {
        IsActive = false;
        Detach();
    }

    private void OnLogicPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogicPanelViewModel.IsRunning) && _logic.IsRunning)
            _sawRunStarted = true;
        if (e.PropertyName is nameof(LogicPanelViewModel.HasNetwork)
            or nameof(LogicPanelViewModel.ClockStepCount)
            or nameof(LogicPanelViewModel.IsRunning))
        {
            EvaluateCurrentStep();
        }
    }

    /// <summary>
    /// Advances past every already-satisfied step, so a panel state that already
    /// fulfils a condition when the tour starts cannot stall it.
    /// </summary>
    private void EvaluateCurrentStep()
    {
        while (IsActive && CurrentStep.IsCompleted())
            Advance();
    }

    private void Advance()
    {
        if (CurrentStepIndex < Steps.Count - 1)
        {
            CurrentStepIndex++;
            return;
        }

        IsCompleted = true;
        IsActive = false;
        Detach();
    }

    private void Detach()
    {
        _logic.PropertyChanged -= OnLogicPropertyChanged;
    }

    partial void OnCurrentStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentBody));
        OnPropertyChanged(nameof(ProgressText));
    }
}
