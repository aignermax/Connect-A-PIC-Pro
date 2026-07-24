using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
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

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    /// <summary>File dialog service; wired by the UI layer like the other exporters.</summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>Supplies the metal routing spec derived from the active process (trace width,
    /// metal/bridge GDS layers, crossing policy); wired by the UI layer (#682).</summary>
    public Func<CAP_Core.Routing.MetalRouting.MetalRoutingSpec>? MetalRoutingSpecProvider { get; set; }

    /// <summary>Supplies the loaded component library, used to resolve each placed
    /// component's inherent backend (raw-code backend lookup) for the mixed-backend
    /// export; wired by the DI layer. Null means an empty library.</summary>
    public Func<IEnumerable<ComponentTemplate>>? TemplateLibraryProvider { get; set; }

    /// <summary>Supplies the configured nazca exporter (carries the interconnect settings
    /// source) for the mixed-backend export; wired by the DI layer. Null falls
    /// back to a default <see cref="SimpleNazcaExporter"/>.</summary>
    public Func<SimpleNazcaExporter>? NazcaExporterProvider { get; set; }

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
    /// cell). Called when the export dialog opens.
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
            StatusText = LocalizationService.Instance.Translate("Export.GdsFactory.NotAvailable");
            return;
        }

        if (_canvas.Components.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Export.GdsFactory.NothingToExport");
            return;
        }

        // Mixed-backend design: gdsfactory-native and nazca-native components
        // coexist. Each group renders with its own backend into a separate GDS; the main
        // gdsfactory script merges both into ONE output GDS. The user is told what happens
        // (dialog + Error Console) — layer maps of the two processes are not reconciled,
        // so cross-process layer alignment must still be verified before fabrication.
        var library = (TemplateLibraryProvider?.Invoke() ?? Enumerable.Empty<ComponentTemplate>()).ToList();
        var isMixedBackend = MixedBackendGdsOrchestrator.IsMixedBackendDesign(_canvas, library);

        // A GDS is one fabrication process — but the Playground deliberately lets you place
        // components from different processes (e.g. CornerStone SiN + SiEPIC SOI) together.
        // Such a design still exports, so the user can look at the result, with an
        // unmissable warning (dialog + Error Console) that the GDS is inspection-only and
        // NOT manufacturable. The generated script activates each cell's own PDK right
        // before instantiating it (see GdsFactoryPdkContext), so no cell is silently drawn
        // with a foreign process' layers. The process check runs for mixed-backend designs
        // too: the canonical SiN + SOI mix keeps the strong not-manufacturable warning;
        // the soft merge notice is for same-process mixes only.
        var backendConflicts = GdsFactoryExporter.CollectBackendConflicts(
            _canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells));
        string? mixedProcessWarning = null;
        if (backendConflicts.Count > 0)
        {
            mixedProcessWarning = string.Format(
                LocalizationService.Instance.Translate("Export.GdsFactory.MixedProcessWarning"),
                string.Join(" + ", backendConflicts));
        }
        else if (isMixedBackend)
        {
            mixedProcessWarning = LocalizationService.Instance.Translate(
                "Export.GdsFactory.MixedBackendWarning");
        }
        if (mixedProcessWarning != null)
        {
            StatusText = mixedProcessWarning;
            _errorConsole?.LogWarning(mixedProcessWarning);
        }

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export to gdsfactory Python", "py", "Python Files|*.py|All Files|*.*");
        if (filePath == null)
            return;

        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (PythonModuleShadowing.ShadowsPythonModule(stem))
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Export.GdsFactory.Shadows"),
                Path.GetFileName(filePath), stem.ToLowerInvariant());
            return;
        }

        await RunExportAsync(filePath, mixedProcessWarning, isMixedBackend ? library : null);
    }

    /// <summary>
    /// Writes the export script(s) and runs them. For a mixed-backend design
    /// (<paramref name="mixedBackendLibrary"/> non-null) the nazca partial
    /// script is written and run FIRST — it produces the partial GDS the main gdsfactory
    /// script merges into the final output.
    /// </summary>
    private async Task RunExportAsync(
        string filePath, string? mixedProcessWarning = null,
        IReadOnlyList<ComponentTemplate>? mixedBackendLibrary = null)
    {
        IsExporting = true;
        try
        {
            if (mixedBackendLibrary != null)
            {
                if (!await WriteAndRunMixedBackendPartAsync(filePath, mixedBackendLibrary))
                    return;
            }
            else
            {
                // Always ubcpdk-where-available with stub fallback — no geometry question.
                await File.WriteAllTextAsync(filePath,
                    _exporter.Export(_canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
                        MetalRoutingSpecProvider?.Invoke()));
            }

            StatusText = LocalizationService.Instance.Translate("Export.GdsFactory.Running");
            var result = await _exportService.ExportToGdsAsync(filePath, generateGds: true);

            // gdsfactory missing → auto-install into a managed environment and retry once.
            if (!result.Success && IsGdsFactoryMissing(result.ErrorMessage) && EnsureGdsFactoryAsync != null)
            {
                var progress = new Progress<string>(m => StatusText = m);
                StatusText = LocalizationService.Instance.Translate("Export.GdsFactory.Installing");
                var installed = await EnsureGdsFactoryAsync(progress, CancellationToken.None);
                if (installed)
                {
                    StatusText = LocalizationService.Instance.Translate("Export.GdsFactory.Retrying");
                    result = await _exportService.ExportToGdsAsync(filePath, generateGds: true);
                }
            }

            // Keep the mixed-process warning visible next to the final result — it must not
            // be scrolled away by the success line (field round 4).
            var status = DescribeResult(filePath, result);
            StatusText = mixedProcessWarning == null
                ? status
                : mixedProcessWarning + Environment.NewLine + status;
            if (result.Success && result.GdsPath != null)
                TryOpenGds(result.GdsPath);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"gdsfactory export failed: {ex.Message}", ex);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Export.GdsFactory.ExportFailed"), ex.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Mixed-backend export: writes both scripts (nazca partial next to the main
    /// script) and runs the nazca partial so its GDS exists before the main script merges it.
    /// Returns false — with a dialog status and an Error Console entry — when the nazca
    /// render fails; the main script is not run against a stale/missing partial.
    /// </summary>
    private async Task<bool> WriteAndRunMixedBackendPartAsync(
        string filePath, IReadOnlyList<ComponentTemplate> library)
    {
        var orchestrator = new MixedBackendGdsOrchestrator(NazcaExporterProvider?.Invoke());
        var scripts = orchestrator.BuildScripts(
            _canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
            MetalRoutingSpecProvider?.Invoke(), library, filePath);

        var partialPath = MixedBackendGdsOrchestrator.PartialScriptPathFor(filePath);
        await File.WriteAllTextAsync(partialPath, scripts.NazcaPartialScript);
        await File.WriteAllTextAsync(filePath, scripts.GdsFactoryScript);

        StatusText = LocalizationService.Instance.Translate("Export.GdsFactory.MixedBackendRunningNazca");
        var partialResult = await _exportService.ExportToGdsAsync(partialPath, generateGds: true);
        if (partialResult.Success)
            return true;

        _errorConsole?.LogError($"Mixed-backend nazca partial failed: {partialResult.ErrorMessage}");
        StatusText = string.Format(
            LocalizationService.Instance.Translate("Export.GdsFactory.MixedBackendNazcaFailed"),
            Path.GetFileName(partialPath));
        return false;
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
            return string.Format(
                LocalizationService.Instance.Translate("Export.GdsFactory.ExportedOpened"),
                scriptName, Path.GetFileName(result.GdsPath));
        if (result.Success)
            return string.Format(
                LocalizationService.Instance.Translate("Export.GdsFactory.Exported"), scriptName);

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
            return string.Format(
                LocalizationService.Instance.Translate("Export.GdsFactory.MissingGdsFactory"), scriptName);

        return string.Format(
            LocalizationService.Instance.Translate("Export.GdsFactory.RunFailed"), scriptName);
    }
}
