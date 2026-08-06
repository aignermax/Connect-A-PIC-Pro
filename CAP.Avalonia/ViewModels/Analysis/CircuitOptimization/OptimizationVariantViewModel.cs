using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.CircuitOptimization;

/// <summary>
/// One row in the ranked variant list: rank, metric versus the baseline, the
/// parameter values that produced it, and a one-click apply action.
/// </summary>
public partial class OptimizationVariantViewModel : ObservableObject
{
    private readonly Action<OptimizationVariantViewModel> _applyAction;

    /// <summary>1-based rank in the result list (1 = best).</summary>
    public int Rank { get; }

    /// <summary>Formatted objective score of this variant.</summary>
    public string ScoreText { get; }

    /// <summary>Formatted improvement over the baseline (e.g. "+0.1234").</summary>
    public string ImprovementText { get; }

    /// <summary>Parameter assignment, e.g. "DC1: 0.71 · PS1: 182°".</summary>
    public string ParameterSummary { get; }

    /// <summary>Raw parameter values, aligned with the run's parameter list.</summary>
    public IReadOnlyList<double> ParameterValues { get; }

    [ObservableProperty]
    private bool _isApplied;

    /// <summary>Creates a variant row.</summary>
    public OptimizationVariantViewModel(
        int rank,
        string scoreText,
        string improvementText,
        string parameterSummary,
        IReadOnlyList<double> parameterValues,
        Action<OptimizationVariantViewModel> applyAction)
    {
        Rank = rank;
        ScoreText = scoreText;
        ImprovementText = improvementText;
        ParameterSummary = parameterSummary;
        ParameterValues = parameterValues;
        _applyAction = applyAction ?? throw new ArgumentNullException(nameof(applyAction));
    }

    [RelayCommand]
    private void Apply() => _applyAction(this);
}
