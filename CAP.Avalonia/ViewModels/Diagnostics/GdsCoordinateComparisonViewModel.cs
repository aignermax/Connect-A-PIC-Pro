using System.Globalization;
using CAP_Core.Export;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Diagnostics;

/// <summary>
/// ViewModel for the GDS Coordinate Comparison panel (Issue #333).
/// Runs scripts/compare_gds_coords.py on two JSON coordinate files and
/// displays exact per-element deviations in micrometres, enabling engineers
/// to confirm or refute fabrication-blocking alignment bugs (Issue #329).
/// </summary>
public partial class GdsCoordinateComparisonViewModel : ObservableObject
{
    private readonly GdsCoordinateComparisonService _service;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunComparisonCommand))]
    private string _referenceJsonPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunComparisonCommand))]
    private string _systemJsonPath = string.Empty;

    [ObservableProperty]
    private string _comparisonStatus = LocalizationService.Instance.Translate("Diag.Gds.EnterPaths");

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _maxDeviationText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunComparisonCommand))]
    private bool _isComparing;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private bool _passed;

    /// <summary>
    /// Callback invoked when the user requests a file picker for the Reference JSON.
    /// The callback should return the selected path, or null if cancelled.
    /// Assigned by the View code-behind.
    /// </summary>
    public Func<Task<string?>>? BrowseForReferenceJson { get; set; }

    /// <summary>
    /// Callback invoked when the user requests a file picker for the System JSON.
    /// The callback should return the selected path, or null if cancelled.
    /// Assigned by the View code-behind.
    /// </summary>
    public Func<Task<string?>>? BrowseForSystemJson { get; set; }

    /// <summary>Initializes a new instance of <see cref="GdsCoordinateComparisonViewModel"/>.</summary>
    /// <param name="service">Comparison service.  When null a default instance is created.</param>
    public GdsCoordinateComparisonViewModel(GdsCoordinateComparisonService? service = null)
    {
        _service = service ?? new GdsCoordinateComparisonService();
    }

    /// <summary>
    /// Runs the Python comparison script on the two configured JSON paths.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunComparison))]
    public async Task RunComparisonAsync()
    {
        IsComparing = true;
        HasResults = false;
        ComparisonStatus = LocalizationService.Instance.Translate("Diag.Gds.Running");
        ResultText = string.Empty;
        MaxDeviationText = string.Empty;

        try
        {
            var result = await _service.CompareAsync(ReferenceJsonPath.Trim(), SystemJsonPath.Trim());

            Passed = result.Passed;
            MaxDeviationText = string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Instance.Translate("Diag.Gds.DeviationSummary"),
                result.MaxDeviationUm,
                result.RmsDeviationUm);
            ComparisonStatus = result.StatusText;
            ResultText = string.IsNullOrWhiteSpace(result.ErrorOutput)
                ? result.RawOutput
                : $"STDERR:\n{result.ErrorOutput}\n\nSTDOUT:\n{result.RawOutput}";
        }
        catch (Exception ex)
        {
            Passed = false;
            ComparisonStatus = string.Format(LocalizationService.Instance.Translate("Diag.ErrorFormat"), ex.Message);
            ResultText = ex.ToString();
            MaxDeviationText = string.Empty;
        }
        finally
        {
            HasResults = true;
            IsComparing = false;
        }
    }

    private bool CanRunComparison() =>
        !IsComparing
        && !string.IsNullOrWhiteSpace(ReferenceJsonPath)
        && !string.IsNullOrWhiteSpace(SystemJsonPath);

    /// <summary>Opens a file picker to select the reference JSON path.</summary>
    [RelayCommand]
    public async Task BrowseReferenceAsync()
    {
        if (BrowseForReferenceJson == null)
            return;
        var path = await BrowseForReferenceJson();
        if (path != null)
            ReferenceJsonPath = path;
    }

    /// <summary>Opens a file picker to select the system JSON path.</summary>
    [RelayCommand]
    public async Task BrowseSystemAsync()
    {
        if (BrowseForSystemJson == null)
            return;
        var path = await BrowseForSystemJson();
        if (path != null)
            SystemJsonPath = path;
    }

    /// <summary>Clears all results and resets the panel to its initial state.</summary>
    [RelayCommand]
    public void ClearResults()
    {
        HasResults = false;
        Passed = false;
        ResultText = string.Empty;
        MaxDeviationText = string.Empty;
        ComparisonStatus = LocalizationService.Instance.Translate("Diag.Gds.EnterPaths");
    }

}
