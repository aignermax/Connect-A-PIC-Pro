using System.Text;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Diagnostics;

/// <summary>
/// ViewModel for the routing diagnostics UI panel.
/// Validates all routed connections and reports issues like bend radius violations,
/// segment gaps, and blocked fallback paths.
/// </summary>
public partial class RoutingDiagnosticsViewModel : ObservableObject
{
    private readonly ErrorConsoleService? _errorConsole;

    /// <summary>Initializes a new instance of <see cref="RoutingDiagnosticsViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public RoutingDiagnosticsViewModel(ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }
    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _totalConnections;

    [ObservableProperty]
    private int _validConnections;

    [ObservableProperty]
    private int _issueCount;

    [ObservableProperty]
    private string _exportedJsonPath = "";

    private DesignCanvasViewModel? _canvas;

    /// <summary>
    /// File dialog service for JSON export.
    /// </summary>
    public Services.IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Configures the diagnostics panel with the current canvas.
    /// </summary>
    public void Configure(DesignCanvasViewModel? canvas)
    {
        _canvas = canvas;
    }

    /// <summary>
    /// Runs diagnostics on all routed connections, including paths inside ComponentGroups.
    /// </summary>
    [RelayCommand]
    private void RunDiagnostics()
    {
        if (_canvas == null || IsAnalyzing) return;

        IsAnalyzing = true;
        StatusText = LocalizationService.Instance.Translate("Diag.Routing.Analyzing");
        ResultText = "";
        ExportedJsonPath = "";

        try
        {
            var connections = _canvas.Connections;
            var frozenPaths = CollectFrozenPaths();
            TotalConnections = connections.Count + frozenPaths.Count;

            if (TotalConnections == 0)
            {
                StatusText = LocalizationService.Instance.Translate("Diag.Routing.NoConnectionsAnalyze");
                ValidConnections = 0;
                IssueCount = 0;
                return;
            }

            double minBendRadius = _canvas.Router?.MinBendRadiusMicrometers ?? 10.0;
            var diagnostics = new RoutingDiagnostics(minBendRadius);
            var sb = new StringBuilder();
            int valid = 0;
            int totalIssues = 0;

            foreach (var conn in connections)
            {
                var path = conn.Connection.RoutedPath;
                if (path == null)
                {
                    sb.AppendLine($"  {GetConnectionLabel(conn)}: No path");
                    totalIssues++;
                    continue;
                }

                AppendPathDiagnostics(sb, diagnostics, path, GetConnectionLabel(conn), ref valid, ref totalIssues);
            }

            foreach (var frozen in frozenPaths)
            {
                AppendPathDiagnostics(sb, diagnostics, frozen.Path, GetFrozenPathLabel(frozen), ref valid, ref totalIssues);
            }

            ValidConnections = valid;
            IssueCount = totalIssues;
            ResultText = sb.ToString();
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Diag.Routing.DoneFormat"), valid, TotalConnections, totalIssues);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Routing diagnostics failed: {ex.Message}", ex);
            StatusText = string.Format(LocalizationService.Instance.Translate("Diag.AnalysisFailedFormat"), ex.Message);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private static void AppendPathDiagnostics(StringBuilder sb, RoutingDiagnostics diagnostics,
        RoutedPath path, string label, ref int valid, ref int totalIssues)
    {
        var report = diagnostics.Validate(path);
        bool isOk = report.IsValid && !path.IsBlockedFallback;

        if (isOk) valid++;

        string status = isOk ? "OK" : "ISSUES";
        sb.AppendLine($"  {label}: {status}");
        sb.AppendLine($"    Length: {path.TotalLengthMicrometers:F1}µm");
        sb.AppendLine($"    Segments: {path.Segments.Count}");
        sb.AppendLine($"    Bends: {path.TotalEquivalent90DegreeBends:F1}×90°");

        if (path.IsBlockedFallback)
            sb.AppendLine("    ⚠ Blocked fallback");
        if (path.IsInvalidGeometry)
            sb.AppendLine("    ⚠ Invalid geometry");

        foreach (var issue in report.Issues)
        {
            sb.AppendLine($"    [{issue.Severity}] {issue.Message}");
            totalIssues++;
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Exports all routed paths to a JSON file for external visualization,
    /// including paths inside ComponentGroups.
    /// </summary>
    [RelayCommand]
    private async Task ExportPathsJson()
    {
        if (_canvas == null)
        {
            StatusText = LocalizationService.Instance.Translate("Diag.Routing.NoConnectionsExport");
            return;
        }

        try
        {
            var paths = CollectAllPaths();

            if (paths.Count == 0)
            {
                StatusText = LocalizationService.Instance.Translate("Diag.Routing.NoConnectionsExport");
                return;
            }

            var json = RoutedPathSerializer.ToJson(paths);

            string? filePath = null;
            if (FileDialogService != null)
            {
                filePath = await FileDialogService.ShowSaveFileDialogAsync(
                    "Export Routed Paths",
                    "json",
                    "JSON Files|*.json|All Files|*.*");
            }

            if (filePath == null)
            {
                StatusText = LocalizationService.Instance.Translate("Diag.Routing.ExportCancelled");
                return;
            }

            await File.WriteAllTextAsync(filePath, json);
            ExportedJsonPath = filePath;
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Diag.Routing.ExportedFormat"),
                paths.Count, Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to export routing paths: {ex.Message}", ex);
            StatusText = string.Format(LocalizationService.Instance.Translate("Diag.Routing.ExportFailedFormat"), ex.Message);
        }
    }

    /// <summary>
    /// Callback to copy text to the system clipboard.
    /// Set by the View code-behind since clipboard access requires a TopLevel reference.
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>
    /// Copies all routed paths as JSON to the clipboard,
    /// including paths inside ComponentGroups.
    /// </summary>
    [RelayCommand]
    private async Task CopyPathsJsonToClipboard()
    {
        if (_canvas == null)
        {
            StatusText = LocalizationService.Instance.Translate("Diag.Routing.NoConnectionsCopy");
            return;
        }

        try
        {
            var paths = CollectAllPaths();

            if (paths.Count == 0)
            {
                StatusText = LocalizationService.Instance.Translate("Diag.Routing.NoConnectionsCopy");
                return;
            }

            var json = RoutedPathSerializer.ToJson(paths);

            // Copy to clipboard via callback
            if (CopyToClipboard != null)
            {
                await CopyToClipboard(json);
                StatusText = string.Format(
                    LocalizationService.Instance.Translate("Diag.Routing.CopiedFormat"), paths.Count);
            }
            else
            {
                StatusText = LocalizationService.Instance.Translate("Diag.ClipboardUnavailable");
            }
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to copy routing paths to clipboard: {ex.Message}", ex);
            StatusText = string.Format(LocalizationService.Instance.Translate("Diag.Routing.CopyFailedFormat"), ex.Message);
        }
    }

    private static string GetConnectionLabel(WaveguideConnectionViewModel conn)
    {
        var start = conn.Connection.StartPin;
        var end = conn.Connection.EndPin;
        var startId = start.ParentComponent?.Identifier ?? "?";
        var endId = end.ParentComponent?.Identifier ?? "?";
        return $"{startId}.{start.Name} -> {endId}.{end.Name}";
    }

    private static string GetFrozenPathLabel(FrozenWaveguidePath frozen)
    {
        var startId = frozen.StartPin?.ParentComponent?.Identifier ?? "?";
        var startName = frozen.StartPin?.Name ?? "?";
        var endId = frozen.EndPin?.ParentComponent?.Identifier ?? "?";
        var endName = frozen.EndPin?.Name ?? "?";
        return $"{startId}.{startName} -> {endId}.{endName} [group]";
    }

    /// <summary>
    /// Collects all routed paths: canvas connections + frozen paths from ComponentGroups.
    /// </summary>
    private Dictionary<string, RoutedPath> CollectAllPaths()
    {
        var paths = new Dictionary<string, RoutedPath>();

        foreach (var conn in _canvas!.Connections)
        {
            if (conn.Connection.RoutedPath != null)
                paths[GetConnectionLabel(conn)] = conn.Connection.RoutedPath;
        }

        foreach (var frozen in CollectFrozenPaths())
        {
            if (frozen.Path != null)
                paths[GetFrozenPathLabel(frozen)] = frozen.Path;
        }

        return paths;
    }

    /// <summary>
    /// Recursively collects all frozen waveguide paths from ComponentGroups on the canvas.
    /// </summary>
    private List<FrozenWaveguidePath> CollectFrozenPaths()
    {
        var result = new List<FrozenWaveguidePath>();
        foreach (var compVm in _canvas!.Components)
        {
            if (compVm.Component is ComponentGroup group)
                CollectFrozenPathsRecursive(group, result);
        }
        return result;
    }

    private static void CollectFrozenPathsRecursive(ComponentGroup group, List<FrozenWaveguidePath> result)
    {
        result.AddRange(group.InternalPaths);
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
                CollectFrozenPathsRecursive(nestedGroup, result);
        }
    }
}
