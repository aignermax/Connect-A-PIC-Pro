using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core;
using CAP_Core.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// ViewModel for the gdsfactory export dialog (#581): mode selection (standalone stub
/// geometry vs. real ubcpdk cells), optional GDS generation through the configured
/// interpreter, and the list of components without a ubcpdk mapping.
/// </summary>
public partial class GdsFactoryExportViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly GdsExportService _exportService;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly GdsFactoryExporter _exporter = new();

    [ObservableProperty]
    private bool _useUbcPdkCells;

    [ObservableProperty]
    private bool _generateGdsEnabled = true;

    [ObservableProperty]
    private ObservableCollection<string> _unmappedComponents = new();

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    /// <summary>File dialog service; wired by the UI layer like the other exporters.</summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Supplies the current per-instance overrides at export time (issue #637); wired by
    /// the UI layer to the loaded project's stored overrides. gdsfactory-backend entries
    /// are embedded as self-contained factories in the exported script.
    /// </summary>
    public Func<IReadOnlyDictionary<string, CAP_DataAccess.Persistence.PIR.NazcaCodeOverride>>? OverridesProvider { get; set; }

    /// <summary>Initializes a new instance of <see cref="GdsFactoryExportViewModel"/>.</summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="exportService">Script runner used for the optional GDS generation.</param>
    /// <param name="errorConsole">Optional error logging.</param>
    public GdsFactoryExportViewModel(
        DesignCanvasViewModel canvas,
        GdsExportService exportService,
        ErrorConsoleService? errorConsole = null)
    {
        _canvas = canvas;
        _exportService = exportService;
        _errorConsole = errorConsole;
    }

    /// <summary>
    /// Recomputes the list of components that would fall back to stub geometry in
    /// ubcpdk mode. Called when the export dialog opens.
    /// </summary>
    public void RefreshUnmappedComponents()
    {
        UnmappedComponents.Clear();
        foreach (var name in GdsFactoryExporter.CollectUnmappedComponents(_canvas))
            UnmappedComponents.Add(name);
    }

    /// <summary>Runs the export: file dialog → shadowing guard → script → optional GDS.</summary>
    [RelayCommand]
    public async Task Export()
    {
        if (FileDialogService == null)
        {
            StatusText = "Export not available (no file dialog service).";
            return;
        }

        if (_canvas.Components.Count == 0)
        {
            StatusText = "Nothing to export — add some components first.";
            return;
        }

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export to gdsfactory Python", "py", "Python Files|*.py|All Files|*.*");
        if (filePath == null)
            return;

        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (PythonModuleShadowing.ShadowsPythonModule(stem))
        {
            StatusText = $"'{Path.GetFileName(filePath)}' shadows the Python module "
                + $"'{stem.ToLowerInvariant()}' — please choose a different file name (e.g. chip1.py).";
            return;
        }

        await RunExportAsync(filePath);
    }

    private async Task RunExportAsync(string filePath)
    {
        IsExporting = true;
        try
        {
            var options = new GdsFactoryExportOptions(UseUbcPdkCells
                ? GdsFactoryComponentMode.UbcPdkCells
                : GdsFactoryComponentMode.StandaloneStubs);
            await File.WriteAllTextAsync(
                filePath, _exporter.Export(_canvas, options, OverridesProvider?.Invoke()));

            if (!GenerateGdsEnabled)
            {
                StatusText = $"Exported {Path.GetFileName(filePath)} (GDS generation skipped).";
                return;
            }

            StatusText = "Running gdsfactory to generate the GDS...";
            var result = await _exportService.ExportToGdsAsync(filePath, generateGds: true);
            StatusText = DescribeResult(filePath, result);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"gdsfactory export failed: {ex.Message}", ex);
            StatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private string DescribeResult(string filePath, GdsExportService.ExportResult result)
    {
        var scriptName = Path.GetFileName(filePath);
        if (result.Success && result.GdsPath != null)
            return $"Exported {scriptName} and {Path.GetFileName(result.GdsPath)}.";
        if (result.Success)
            return $"Exported {scriptName}.";

        // Full traceback goes to the (copyable) Error Console only — the dialog shows a
        // short, actionable line so it doesn't duplicate an uncopyable wall of text.
        _errorConsole?.LogError($"gdsfactory GDS generation failed: {result.ErrorMessage}");
        return BuildFailureMessage(scriptName, result.ErrorMessage);
    }

    /// <summary>
    /// Builds the short, dialog-facing failure line. The full error is logged to the
    /// Error Console separately; this never embeds the raw traceback so the dialog stays
    /// concise and the copyable detail lives in one place.
    /// </summary>
    internal static string BuildFailureMessage(string scriptName, string? errorMessage)
    {
        var gdsFactoryMissing = errorMessage?.Contains("No module named 'gdsfactory'",
            StringComparison.OrdinalIgnoreCase) == true;
        if (gdsFactoryMissing)
            return $"Exported {scriptName}, but gdsfactory is not installed in the active "
                + "environment. Install it under Settings → Python Environments → Install gdsfactory, "
                + "then export again.";

        return $"Exported {scriptName}, but the GDS run failed — see the Error Console for details.";
    }
}
