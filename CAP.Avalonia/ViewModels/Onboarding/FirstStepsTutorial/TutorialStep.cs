namespace CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;

/// <summary>
/// A single guided-tour step: localized title/body keys plus a completion
/// predicate evaluated against live canvas state. The engine re-checks the
/// predicate whenever the canvas signals a relevant change; when it turns
/// true the tour advances to the next step.
/// </summary>
public sealed class TutorialStep
{
    /// <summary>Creates a step with its localization keys and completion condition.</summary>
    public TutorialStep(string titleKey, string bodyKey, Func<bool> isCompleted)
    {
        TitleKey = titleKey;
        BodyKey = bodyKey;
        IsCompleted = isCompleted;
    }

    /// <summary>Localization key for the step title.</summary>
    public string TitleKey { get; }

    /// <summary>Localization key for the task-focused body text.</summary>
    public string BodyKey { get; }

    /// <summary>Returns true when the step's task has been performed on the canvas.</summary>
    public Func<bool> IsCompleted { get; }
}
