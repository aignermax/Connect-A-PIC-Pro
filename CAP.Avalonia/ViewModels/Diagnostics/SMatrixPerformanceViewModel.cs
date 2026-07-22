using System.Globalization;
using CAP_Core;
using CAP_Core.LightCalculation;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Diagnostics;

/// <summary>
/// ViewModel for S-Matrix performance diagnostics panel.
/// Displays sparsity statistics and memory usage of the system S-Matrix.
/// </summary>
public partial class SMatrixPerformanceViewModel : ObservableObject
{
    private readonly SMatrixStatisticsAnalyzer _analyzer = new();
    private readonly ErrorConsoleService? _errorConsole;

    /// <summary>Initializes a new instance of <see cref="SMatrixPerformanceViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public SMatrixPerformanceViewModel(ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    [ObservableProperty]
    private string _matrixSizeText = "-";

    [ObservableProperty]
    private string _totalElementsText = "-";

    [ObservableProperty]
    private string _nonZeroElementsText = "-";

    [ObservableProperty]
    private string _sparsityText = "-";

    [ObservableProperty]
    private string _memoryUsageText = "-";

    [ObservableProperty]
    private string _memorySavingsText = "-";

    [ObservableProperty]
    private string _storageTypeText = "-";

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Diag.SMatrix.NoAnalysisYet");

    [ObservableProperty]
    private bool _hasAnalysis;

    [ObservableProperty]
    private bool _isAnalyzing;

    private SMatrixStatistics? _lastStats;

    /// <summary>
    /// Analyzes the given S-Matrix and updates the displayed statistics.
    /// </summary>
    /// <param name="matrix">The S-Matrix to analyze</param>
    [RelayCommand]
    public void AnalyzeMatrix(SMatrix? matrix)
    {
        if (matrix == null)
        {
            ResetStatistics();
            StatusText = LocalizationService.Instance.Translate("Diag.SMatrix.NoMatrix");
            return;
        }

        IsAnalyzing = true;
        StatusText = LocalizationService.Instance.Translate("Diag.SMatrix.Analyzing");

        try
        {
            _lastStats = _analyzer.AnalyzeMatrix(matrix);
            UpdateDisplayedStatistics(_lastStats);
            HasAnalysis = true;
            StatusText = LocalizationService.Instance.Translate("Diag.SMatrix.Complete");
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"S-Matrix analysis failed: {ex.Message}", ex);
            StatusText = string.Format(LocalizationService.Instance.Translate("Diag.AnalysisFailedFormat"), ex.Message);
            ResetStatistics();
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    /// <summary>
    /// Updates all displayed statistics from the analysis result.
    /// </summary>
    private void UpdateDisplayedStatistics(SMatrixStatistics stats)
    {
        MatrixSizeText = $"{stats.MatrixSize} × {stats.MatrixSize}";
        TotalElementsText = FormatNumber(stats.TotalElements);
        NonZeroElementsText = FormatNumber(stats.NonZeroElements);
        SparsityText = $"{stats.SparsityPercentage:F2}%";
        MemoryUsageText = stats.FormattedMemorySize;
        StorageTypeText = stats.IsSparse
            ? LocalizationService.Instance.Translate("Diag.SMatrix.Sparse")
            : LocalizationService.Instance.Translate("Diag.SMatrix.Dense");

        double savings = _analyzer.CalculateMemorySavings(stats);
        if (stats.IsSparse && savings > 1.0)
        {
            MemorySavingsText = string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Instance.Translate("Diag.SMatrix.SavingsFormat"),
                savings);
        }
        else
        {
            MemorySavingsText = LocalizationService.Instance.Translate("Diag.NotAvailable");
        }
    }

    /// <summary>
    /// Resets all statistics to default values.
    /// </summary>
    private void ResetStatistics()
    {
        MatrixSizeText = "-";
        TotalElementsText = "-";
        NonZeroElementsText = "-";
        SparsityText = "-";
        MemoryUsageText = "-";
        MemorySavingsText = "-";
        StorageTypeText = "-";
        HasAnalysis = false;
        _lastStats = null;
    }

    /// <summary>
    /// Formats large numbers with thousands separators.
    /// </summary>
    private string FormatNumber(int number)
    {
        return number.ToString("N0");
    }

    /// <summary>
    /// Clears the current analysis.
    /// </summary>
    [RelayCommand]
    private void ClearAnalysis()
    {
        ResetStatistics();
        StatusText = LocalizationService.Instance.Translate("Diag.SMatrix.Cleared");
    }
}
