using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Analysis;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Diagnostics;

/// <summary>
/// ViewModel for component dimension validation panel.
/// Shows components with dimensional mismatches between stated size and pin positions.
/// </summary>
public partial class ComponentDimensionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Diag.Ready");

    [ObservableProperty]
    private bool _hasIssues;

    [ObservableProperty]
    private int _issueCount;

    /// <summary>
    /// List of components with dimension validation issues.
    /// </summary>
    public ObservableCollection<DimensionIssue> Issues { get; } = new();

    private readonly ComponentDimensionValidator _validator = new();
    private DesignCanvasViewModel? _canvas;

    /// <summary>
    /// Clipboard copy function injected by the View.
    /// Set by the View code-behind since clipboard access requires a TopLevel reference.
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>
    /// Configures the validator to monitor a specific canvas.
    /// </summary>
    public void Configure(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
        canvas.Components.CollectionChanged += (s, e) => RunValidation();
    }

    /// <summary>
    /// Runs validation on all components in the design.
    /// </summary>
    [RelayCommand]
    private void RunValidation()
    {
        if (_canvas == null)
        {
            StatusText = LocalizationService.Instance.Translate("Diag.Dimension.NoDesign");
            return;
        }

        Issues.Clear();
        var components = _canvas.Components.Select(vm => vm.Component).ToList();
        var results = _validator.ValidateAll(components);

        foreach (var result in results)
        {
            Issues.Add(new DimensionIssue
            {
                ComponentName = result.ComponentName,
                Issue = result.Issue ?? LocalizationService.Instance.Translate("Diag.Dimension.UnknownIssue"),
                CurrentDimensions = $"{result.CurrentWidth:F1} × {result.CurrentHeight:F1} µm",
                RecommendedDimensions = $"{result.RecommendedWidth:F1} × {result.RecommendedHeight:F1} µm"
            });
        }

        IssueCount = Issues.Count;
        HasIssues = IssueCount > 0;
        StatusText = HasIssues
            ? string.Format(LocalizationService.Instance.Translate("Diag.Dimension.FoundIssues"), IssueCount)
            : LocalizationService.Instance.Translate("Diag.Dimension.AllValid");
    }

    /// <summary>
    /// Copies all dimension issues to clipboard.
    /// </summary>
    [RelayCommand]
    private async Task CopyIssuesToClipboard()
    {
        if (!HasIssues || Issues.Count == 0 || CopyToClipboard == null)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("Component Dimension Issues:");
        sb.AppendLine();

        foreach (var issue in Issues)
        {
            sb.AppendLine($"Component: {issue.ComponentName}");
            sb.AppendLine($"  Issue: {issue.Issue}");
            sb.AppendLine($"  Current: {issue.CurrentDimensions}");
            sb.AppendLine($"  Recommended: {issue.RecommendedDimensions}");
            sb.AppendLine();
        }

        await CopyToClipboard(sb.ToString());
        StatusText = string.Format(LocalizationService.Instance.Translate("Diag.Dimension.CopiedToClipboard"), IssueCount);
    }
}

/// <summary>
/// Represents a single dimension validation issue for display in the UI.
/// </summary>
public class DimensionIssue
{
    public string ComponentName { get; init; } = "";
    public string Issue { get; init; } = "";
    public string CurrentDimensions { get; init; } = "";
    public string RecommendedDimensions { get; init; } = "";
}
