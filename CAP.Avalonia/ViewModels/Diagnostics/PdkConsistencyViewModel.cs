using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CAP.Avalonia.ViewModels.Diagnostics;

/// <summary>
/// ViewModel for the PDK Consistency panel.
/// Validates JSON PDK component definitions for coordinate correctness and
/// compares them against built-in ComponentTemplates to detect mismatches.
/// Issue #334: Investigate PDK JSON vs Nazca Python coordinate mismatch.
/// </summary>
public partial class PdkConsistencyViewModel : ObservableObject
{
    private readonly PdkConsistencyChecker _checker;
    private readonly PdkLoader _loader;

    /// <summary>Status text shown below the Run button.</summary>
    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Diag.Pdk.Prompt");

    /// <summary>True when a check has been run and findings are available.</summary>
    [ObservableProperty]
    private bool _hasFindings;

    /// <summary>Summary line shown in the header (e.g., "3 warnings, 1 error").</summary>
    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>Collection of all consistency findings to display.</summary>
    public ObservableCollection<PdkFindingDisplayItem> Findings { get; } = new();

    /// <summary>Initializes a new <see cref="PdkConsistencyViewModel"/>.</summary>
    public PdkConsistencyViewModel()
    {
        _checker = new PdkConsistencyChecker();
        _loader = new PdkLoader();
    }

    /// <summary>
    /// Runs consistency checks on all bundled PDK JSON files and built-in templates.
    /// </summary>
    [RelayCommand]
    private void CheckPdks()
    {
        Findings.Clear();
        HasFindings = false;
        StatusText = LocalizationService.Instance.Translate("Diag.Pdk.Running");

        try
        {
            var allFindings = RunChecksOnBundledPdks();
            PopulateFindings(allFindings);
            UpdateSummary(allFindings);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance.Translate("Diag.ErrorFormat"), ex.Message);
        }
    }

    private List<PdkConsistencyFinding> RunChecksOnBundledPdks()
    {
        var allFindings = new List<PdkConsistencyFinding>();

        var pdkDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs");
        if (!Directory.Exists(pdkDir))
        {
            StatusText = string.Format(LocalizationService.Instance.Translate("Diag.Pdk.DirNotFound"), pdkDir);
            return allFindings;
        }

        foreach (var file in Directory.GetFiles(pdkDir, "*.json"))
        {
            try
            {
                var pdk = _loader.LoadFromFile(file);
                var internalFindings = _checker.Check(pdk);
                allFindings.AddRange(internalFindings);
            }
            catch (Exception ex)
            {
                allFindings.Add(new PdkConsistencyFinding
                {
                    ComponentName = Path.GetFileName(file),
                    FindingType = "LoadError",
                    Message = string.Format(LocalizationService.Instance.Translate("Diag.Pdk.LoadFailed"), ex.Message),
                    Severity = PdkFindingSeverity.Error
                });
            }
        }

        return allFindings;
    }

    private void PopulateFindings(List<PdkConsistencyFinding> findings)
    {
        foreach (var f in findings.OrderByDescending(f => f.Severity))
        {
            Findings.Add(new PdkFindingDisplayItem
            {
                ComponentName = f.ComponentName,
                FindingType = f.FindingType,
                Message = f.Message,
                SeverityLabel = f.Severity.ToString(),
                SeverityColor = f.Severity switch
                {
                    PdkFindingSeverity.Error => "Tomato",
                    PdkFindingSeverity.Warning => "Gold",
                    _ => "LightGray"
                },
                DeviationText = f.DeviationMicrometers.HasValue
                    ? string.Format(
                        LocalizationService.Instance.Translate("Diag.Pdk.DeviationFormat"),
                        f.DeviationMicrometers.Value.ToString("F3", CultureInfo.InvariantCulture))
                    : ""
            });
        }

        HasFindings = Findings.Count > 0;
    }

    private void UpdateSummary(List<PdkConsistencyFinding> findings)
    {
        var errors = findings.Count(f => f.Severity == PdkFindingSeverity.Error);
        var warnings = findings.Count(f => f.Severity == PdkFindingSeverity.Warning);
        var infos = findings.Count(f => f.Severity == PdkFindingSeverity.Info);

        if (findings.Count == 0)
        {
            SummaryText = LocalizationService.Instance.Translate("Diag.Pdk.AllConsistent");
            StatusText = LocalizationService.Instance.Translate("Diag.Pdk.NoIssues");
        }
        else
        {
            SummaryText = string.Format(
                LocalizationService.Instance.Translate("Diag.Pdk.SummaryFormat"), errors, warnings, infos);
            StatusText = errors > 0
                ? LocalizationService.Instance.Translate("Diag.Pdk.IssuesFound")
                : LocalizationService.Instance.Translate("Diag.Pdk.WarningsFound");
        }
    }
}

/// <summary>
/// Display model for a single PDK consistency finding in the UI.
/// </summary>
public class PdkFindingDisplayItem
{
    /// <summary>Name of the component the finding belongs to.</summary>
    public string ComponentName { get; set; } = "";

    /// <summary>Short type label (e.g., "PinOutOfBounds").</summary>
    public string FindingType { get; set; } = "";

    /// <summary>Full description of the issue.</summary>
    public string Message { get; set; } = "";

    /// <summary>Severity label (Info / Warning / Error).</summary>
    public string SeverityLabel { get; set; } = "";

    /// <summary>Avalonia color name for the severity badge.</summary>
    public string SeverityColor { get; set; } = "Gray";

    /// <summary>Formatted deviation value, or empty string if not applicable.</summary>
    public string DeviationText { get; set; } = "";
}
