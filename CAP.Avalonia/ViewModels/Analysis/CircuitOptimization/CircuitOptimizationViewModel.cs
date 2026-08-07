using System.Collections.ObjectModel;
using CAP_Core;
using CAP_Core.Analysis.CircuitOptimization;
using CAP_Core.Components.ComponentHelpers;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.CircuitOptimization;

/// <summary>
/// ViewModel for the circuit optimization panel (issue #820): runs a seeded,
/// budget-limited hill-climb over all slider parameters on the canvas and shows
/// the top-N improved variants with one-click, undo-safe apply.
/// </summary>
public partial class CircuitOptimizationViewModel : ObservableObject
{
    private const int MaxSummaryDecimals = 3;

    private readonly CommandManager _commandManager;
    private readonly ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;
    private CancellationTokenSource? _runCts;
    private IReadOnlyList<OptimizationParameter> _lastParameters = Array.Empty<OptimizationParameter>();
    private IReadOnlyList<ComponentViewModel> _lastParameterComponents = Array.Empty<ComponentViewModel>();

    [ObservableProperty]
    private int _evaluationBudget = 50;

    [ObservableProperty]
    private int _seed = 42;

    [ObservableProperty]
    private bool _maximize = true;

    [ObservableProperty]
    private bool _isOptimizing;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _baselineText = "";

    [ObservableProperty]
    private bool _hasParameters;

    [ObservableProperty]
    private OptimizationTargetOption? _selectedTarget;

    /// <summary>Selectable optimization targets, rebuilt from the canvas.</summary>
    public ObservableCollection<OptimizationTargetOption> Targets { get; } = new();

    /// <summary>Ranked list of improved variants from the last run.</summary>
    public ObservableCollection<OptimizationVariantViewModel> Variants { get; } = new();

    /// <summary>Initializes a new instance of <see cref="CircuitOptimizationViewModel"/>.</summary>
    /// <param name="commandManager">Undo-aware command manager used for variant apply.</param>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public CircuitOptimizationViewModel(CommandManager commandManager, ErrorConsoleService? errorConsole = null)
    {
        _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        _errorConsole = errorConsole;
    }

    /// <summary>Attaches the panel to the canvas and builds targets/parameters.</summary>
    public void ConfigureForCanvas(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
        canvas.Components.CollectionChanged += (_, _) => RefreshFromCanvas();
        RefreshFromCanvas();
    }

    /// <summary>Re-reads tunable parameters and target options from the canvas.</summary>
    public void RefreshFromCanvas()
    {
        if (_canvas == null || IsOptimizing) return;

        HasParameters = _canvas.Components.Any(c => c.HasSliders);
        RebuildTargets();
    }

    private void RebuildTargets()
    {
        string? previousSelection = SelectedTarget?.DisplayName;
        Targets.Clear();
        foreach (var option in OptimizationTargetFactory.Build(_canvas!.Components))
            Targets.Add(option);

        SelectedTarget = Targets.FirstOrDefault(t => t.DisplayName == previousSelection)
            ?? Targets.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RunOptimization()
    {
        if (_canvas == null || IsOptimizing) return;
        if (!TryPrepareRun(out var calculator)) return;

        IsOptimizing = true;
        Variants.Clear();
        BaselineText = "";
        _runCts = new CancellationTokenSource();

        try
        {
            var objective = new PinPowerObjective(
                SelectedTarget!.PinIds.ToList(), SelectedTarget.DisplayName, Maximize);
            var settings = new OptimizationSettings(
                _lastParameters, objective, StandardWaveLengths.RedNM, EvaluationBudget, Seed);
            var progress = new Progress<OptimizationProgress>(p => StatusText = string.Format(
                LocalizationService.Instance.Translate("Optimize.Progress"),
                p.EvaluationsDone, p.Budget, p.BestScore));

            var result = await new CircuitOptimizer(calculator!).RunAsync(
                settings, _runCts.Token, progress);
            PopulateVariants(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _errorConsole?.LogError($"Circuit optimization failed: {ex.Message}", ex);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Optimize.Failed"), ex.Message);
        }
        finally
        {
            IsOptimizing = false;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private void CancelOptimization() => _runCts?.Cancel();

    private bool TryPrepareRun(out CAP_Core.LightCalculation.ILightCalculator? calculator)
    {
        calculator = null;
        BuildParameters();
        if (_lastParameters.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Optimize.NoParameters");
            return false;
        }
        if (SelectedTarget == null || SelectedTarget.PinIds.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Optimize.NoTarget");
            return false;
        }

        calculator = CanvasSimulationBuilder.TryBuild(_canvas!);
        if (calculator == null)
        {
            StatusText = LocalizationService.Instance.Translate("Optimize.NoCircuit");
            return false;
        }
        return true;
    }

    private void BuildParameters()
    {
        var parameters = new List<OptimizationParameter>();
        var components = new List<ComponentViewModel>();
        foreach (var componentVm in _canvas!.Components.Where(c => c.HasSliders))
        {
            parameters.Add(new OptimizationParameter(
                componentVm.Component, 0, $"{componentVm.Name} · {componentVm.SliderLabel}"));
            components.Add(componentVm);
        }
        _lastParameters = parameters;
        _lastParameterComponents = components;
    }

    private void PopulateVariants(OptimizationResult result)
    {
        BaselineText = string.Format(
            LocalizationService.Instance.Translate("Optimize.Baseline"), result.BaselineScore);

        int rank = 1;
        foreach (var candidate in result.TopVariants)
        {
            Variants.Add(new OptimizationVariantViewModel(
                rank++,
                candidate.Score.ToString("F4"),
                $"+{candidate.ImprovementOver(result.BaselineScore):F4}",
                BuildParameterSummary(candidate),
                candidate.ParameterValues,
                ApplyVariant));
        }

        string key = result.WasCancelled ? "Optimize.Cancelled"
            : result.TopVariants.Count == 0 ? "Optimize.NoImprovement"
            : "Optimize.Complete";
        StatusText = string.Format(LocalizationService.Instance.Translate(key),
            result.EvaluationsUsed, result.TopVariants.Count);
    }

    private string BuildParameterSummary(OptimizationCandidate candidate)
    {
        var parts = new List<string>(candidate.ParameterValues.Count);
        for (int i = 0; i < candidate.ParameterValues.Count; i++)
        {
            double rounded = Math.Round(candidate.ParameterValues[i], MaxSummaryDecimals);
            parts.Add($"{_lastParameters[i].DisplayName} = {rounded}");
        }
        return string.Join("   ", parts);
    }

    private void ApplyVariant(OptimizationVariantViewModel variant)
    {
        var assignments = new List<(ComponentViewModel, double)>(_lastParameterComponents.Count);
        for (int i = 0; i < _lastParameterComponents.Count; i++)
            assignments.Add((_lastParameterComponents[i], variant.ParameterValues[i]));

        _commandManager.ExecuteCommand(
            new ApplyOptimizationVariantCommand(assignments, $"variant #{variant.Rank}"));

        foreach (var other in Variants)
            other.IsApplied = false;
        variant.IsApplied = true;
        StatusText = string.Format(
            LocalizationService.Instance.Translate("Optimize.Applied"), variant.Rank);
    }
}
