using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;

/// <summary>
/// Step engine for the "Learn Lunima" first-steps tour (issue #1080, slice 1 of #769).
/// An ordered list of steps observes live canvas state — component count, connection
/// count, and the light-propagation overlay — and advances automatically as each task
/// is completed. Plain observable state with no view dependency, so the whole tour can
/// be driven headlessly from unit tests against a real <see cref="DesignCanvasViewModel"/>.
/// </summary>
public partial class TutorialViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;

    /// <summary>True while the tour overlay is shown and the engine is observing the canvas.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Zero-based index of the step the user is currently asked to perform.</summary>
    [ObservableProperty]
    private int _currentStepIndex;

    /// <summary>True once the final step was completed (or Next was pressed on it).</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>
    /// Builds the three first-steps tasks against the given canvas:
    /// place any component, connect two pins, run the light simulation.
    /// </summary>
    public TutorialViewModel(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
        Steps = new List<TutorialStep>
        {
            new("Tutorial.Step1Title", "Tutorial.Step1Body", () => _canvas.Components.Count > 0),
            new("Tutorial.Step2Title", "Tutorial.Step2Body", () => _canvas.Connections.Count > 0),
            new("Tutorial.Step3Title", "Tutorial.Step3Body", () => _canvas.ShowPowerFlow),
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

    /// <summary>Localized position label, e.g. "Step 2/3".</summary>
    public string ProgressText => string.Format(
        CultureInfo.InvariantCulture, "{0} {1}/{2}",
        LocalizationService.Instance.Translate("Tutorial.Step"),
        CurrentStepIndex + 1, Steps.Count);

    /// <summary>Starts the tour at the first step and begins observing the canvas.</summary>
    public void Start()
    {
        Detach();
        CurrentStepIndex = 0;
        IsCompleted = false;
        IsActive = true;
        _canvas.Components.CollectionChanged += OnCanvasChanged;
        _canvas.Connections.CollectionChanged += OnCanvasChanged;
        _canvas.PropertyChanged += OnCanvasPropertyChanged;
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

    private void OnCanvasChanged(object? sender, NotifyCollectionChangedEventArgs e) => EvaluateCurrentStep();

    private void OnCanvasPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignCanvasViewModel.ShowPowerFlow))
            EvaluateCurrentStep();
    }

    /// <summary>
    /// Advances past every already-satisfied step, so a state change that fulfils
    /// several conditions at once (e.g. loading a design) cannot stall the tour.
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
        _canvas.Components.CollectionChanged -= OnCanvasChanged;
        _canvas.Connections.CollectionChanged -= OnCanvasChanged;
        _canvas.PropertyChanged -= OnCanvasPropertyChanged;
    }

    partial void OnCurrentStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentBody));
        OnPropertyChanged(nameof(ProgressText));
    }
}
