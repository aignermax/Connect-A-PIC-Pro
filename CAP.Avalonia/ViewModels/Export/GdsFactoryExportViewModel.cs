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
/// ViewModel for the gdsfactory export dialog (#581/#643). The export "just works":
/// it always uses real ubcpdk (SiEPIC) cells where a mapping exists and falls back to
/// stub geometry otherwise (no geometry question), always generates the GDS and opens it,
/// and — when gdsfactory is missing from the active interpreter — auto-installs it into a
/// managed environment (creating one if needed) and retries.
/// </summary>
public partial class GdsFactoryExportViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly GdsExportService _exportService;
    private readonly IUrlLauncher _urlLauncher;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly GdsFactoryExporter _exporter = new();

    [ObservableProperty]
    private ObservableCollection<string> _unmappedComponents = new();

    /// <summary>Instances whose override is written for Nazca — not honoured in the gdsfactory
    /// export (they use ubcpdk/stub geometry instead). Surfaced as a pre-export warning.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _backendMismatches = new();

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    /// <summary>File dialog service; wired by the UI layer like the other exporters.</summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>Supplies the per-instance overrides (wired by the UI layer to the design's
    /// stored overrides); gdsfactory-backend ones are emitted as factories.</summary>
    public Func<IReadOnlyDictionary<string, CAP_DataAccess.Persistence.PIR.NazcaCodeOverride>>? OverridesProvider { get; set; }

    /// <summary>
    /// Ensures gdsfactory is installed into a managed environment (creating one if needed)
    /// and returns true when it is available afterwards. Wired by the DI layer to the
    /// environment manager so the export slice does not import it directly.
    /// </summary>
    public Func<IProgress<string>, CancellationToken, Task<bool>>? EnsureGdsFactoryAsync { get; set; }

    /// <summary>Initializes a new instance of <see cref="GdsFactoryExportViewModel"/>.</summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="exportService">Script runner used for GDS generation.</param>
    /// <param name="urlLauncher">Launcher used to open the generated GDS.</param>
    /// <param name="errorConsole">Optional error logging.</param>
    public GdsFactoryExportViewModel(
        DesignCanvasViewModel canvas,
        GdsExportService exportService,
        IUrlLauncher? urlLauncher = null,
        ErrorConsoleService? errorConsole = null)
    {
        _canvas = canvas;
        _exportService = exportService;
        _urlLauncher = urlLauncher ?? PlatformShellLauncher.CreateDefault();
        _errorConsole = errorConsole;
    }

    /// <summary>
    /// Recomputes the pre-export info: components that fall back to stub geometry (no ubcpdk
    /// cell) and instances whose override targets Nazca (not honoured here). Called when the
    /// export dialog opens.
    /// </summary>
    public void RefreshUnmappedComponents()
    {
        UnmappedComponents.Clear();
        foreach (var name in GdsFactoryExporter.CollectUnmappedComponents(_canvas))
            UnmappedComponents.Add(name);

        BackendMismatches.Clear();
        foreach (var id in GdsFactoryExporter.CollectBackendMismatches(_canvas, OverridesProvider?.Invoke()))
            BackendMismatches.Add(id);
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

        // A GDS is one fabrication process. Playground lets you place components from different
        // processes (e.g. CornerStone SiN + SiEPIC SOI) together, but they cannot be exported into
        // one gdsfactory GDS — the script activates a single PDK and the foreign cells fail to
        // resolve. Detect this up front and refuse with a clear message instead of a raw Python
        // crash at runtime (#570).
        var backendConflicts = GdsFactoryExporter.CollectBackendConflicts(
            _canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells));
        if (backendConflicts.Count > 0)
        {
            var message =
                "Cannot export: this design mixes incompatible fabrication processes ("
                + string.Join(" + ", backendConflicts)
                + "). A single GDS is one process — keep one PDK's components per design and export "
                + "each separately. (Playground lets you place mixed components for experimentation, "
                + "but they can't be exported together.)";
            StatusText = message;
            _errorConsole?.LogError(message);
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
            // Always ubcpdk-where-available with stub fallback — no geometry question.
            // gdsfactory-backend overrides are emitted as factories; Nazca-backend ones fall
            // back to ubcpdk/stub (surfaced as a mismatch warning).
            await File.WriteAllTextAsync(filePath,
                _exporter.Export(_canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
                    OverridesProvider?.Invoke()));

            StatusText = "Running gdsfactory to generate the GDS...";
            var result = await _exportService.ExportToGdsAsync(filePath, generateGds: true);

            // gdsfactory missing → auto-install into a managed environment and retry once.
            if (!result.Success && IsGdsFactoryMissing(result.ErrorMessage) && EnsureGdsFactoryAsync != null)
            {
                var progress = new Progress<string>(m => StatusText = m);
                StatusText = "gdsfactory not found — installing it into a managed environment...";
                var installed = await EnsureGdsFactoryAsync(progress, CancellationToken.None);
                if (installed)
                {
                    StatusText = "Retrying GDS generation...";
                    result = await _exportService.ExportToGdsAsync(filePath, generateGds: true);
                }
            }

            StatusText = DescribeResult(filePath, result);
            if (result.Success && result.GdsPath != null)
                TryOpenGds(result.GdsPath);
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

    private static bool IsGdsFactoryMissing(string? errorMessage) =>
        // Any package the gdsfactory export env is expected to provide — gdsfactory itself,
        // ubcpdk, or cspdk (CornerStone SiN, #661). Environments provisioned before a package
        // was added report "No module named '<pkg>'"; the same reinstall fixes all of them.
        errorMessage != null &&
        (errorMessage.Contains("No module named 'gdsfactory'", StringComparison.OrdinalIgnoreCase) ||
         errorMessage.Contains("No module named 'ubcpdk'", StringComparison.OrdinalIgnoreCase) ||
         errorMessage.Contains("No module named 'cspdk'", StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens the generated GDS in the default viewer (KLayout etc.), best-effort.</summary>
    private void TryOpenGds(string gdsPath)
    {
        try
        {
            if (File.Exists(gdsPath))
                _urlLauncher.OpenFileOrDirectory(gdsPath);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogWarning($"Could not open {Path.GetFileName(gdsPath)}: {ex.Message}");
        }
    }

    private string DescribeResult(string filePath, GdsExportService.ExportResult result)
    {
        var scriptName = Path.GetFileName(filePath);
        if (result.Success && result.GdsPath != null)
            return $"Exported {scriptName} and opened {Path.GetFileName(result.GdsPath)}.";
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
