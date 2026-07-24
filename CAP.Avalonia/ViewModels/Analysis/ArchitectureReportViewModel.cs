using CAP_Core.Analysis;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>
/// ViewModel for the Architecture Report panel.
/// Loads and displays architectural health metrics, SOLID compliance findings,
/// and prioritized refactoring recommendations based on the Issue #320 analysis.
/// </summary>
public partial class ArchitectureReportViewModel : ObservableObject
{
    /// <summary>Gets or sets the total number of feature ViewModels.</summary>
    [ObservableProperty]
    private int _viewModelCount;

    /// <summary>Gets or sets the number of DI registrations.</summary>
    [ObservableProperty]
    private int _diRegistrationCount;

    /// <summary>Gets or sets the number of test files.</summary>
    [ObservableProperty]
    private int _testFileCount;

    /// <summary>Gets or sets the MainViewModel line count.</summary>
    [ObservableProperty]
    private int _mainViewModelLines;

    /// <summary>Gets or sets the MainWindow.axaml line count.</summary>
    [ObservableProperty]
    private int _mainWindowLines;

    /// <summary>Gets or sets the architectural maturity score (1–5).</summary>
    [ObservableProperty]
    private int _maturityScore;

    /// <summary>Gets or sets the PRISM migration recommendation text.</summary>
    [ObservableProperty]
    private string _prismRecommendationText = "";

    /// <summary>Gets or sets the status text shown below the Run button.</summary>
    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Analysis.Architecture.InitialStatus");

    /// <summary>Gets whether metrics have been loaded.</summary>
    [ObservableProperty]
    private bool _hasMetrics;

    /// <summary>Gets the list of prioritized architectural recommendations.</summary>
    public ObservableCollection<string> Recommendations { get; } = new();

    /// <summary>
    /// Loads the current architecture metrics and populates all observable properties.
    /// </summary>
    [RelayCommand]
    private void LoadMetrics()
    {
        var metrics = ArchitectureMetrics.Current();
        ApplyMetrics(metrics);
    }

    private void ApplyMetrics(ArchitectureMetrics metrics)
    {
        ViewModelCount = metrics.ViewModelCount;
        DiRegistrationCount = metrics.DiRegistrationCount;
        TestFileCount = metrics.TestFileCount;
        MainViewModelLines = metrics.MainViewModelLines;
        MainWindowLines = metrics.MainWindowLines;
        MaturityScore = metrics.MaturityScore;

        PrismRecommendationText = metrics.PrismMigrationRecommended
            ? "PRISM migration RECOMMENDED — project has outgrown manual MVVM"
            : "PRISM NOT recommended — hybrid modularization (Option C) is sufficient";

        Recommendations.Clear();
        foreach (var r in metrics.Recommendations)
            Recommendations.Add(r);

        HasMetrics = true;
        StatusText = string.Format(
            LocalizationService.Instance.Translate("Analysis.Architecture.Summary"),
            MaturityScore, TestFileCount, Recommendations.Count);
    }
}
