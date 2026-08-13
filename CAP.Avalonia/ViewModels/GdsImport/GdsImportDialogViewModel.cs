using System.Collections.ObjectModel;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.Localization;
using CAP_Core;
using CAP_DataAccess.Import.Gds;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// ViewModel for the GDS import dialog. The .gds file is chosen
/// before the dialog opens; the dialog analyzes it on open (top-cell candidates),
/// lets the user pick the top cell, hierarchy mode and pin-detection layers, then
/// runs the import and places the result on the canvas via
/// <see cref="GdsPlacementExecutor"/>. The outcome (placed/connected counts plus
/// all warnings and info notes) stays visible until the user closes the dialog;
/// every warning is mirrored to the error console via <c>LogWarning</c>, every
/// info note via <c>LogInfo</c>, and failures via <c>LogError</c> — the console
/// lines can be selected and copied (the dialog's own texts are not copyable).
/// </summary>
public partial class GdsImportDialogViewModel : ObservableObject
{
    private readonly GdsImportService _importService;
    private readonly GdsPlacementExecutor _placementExecutor;
    private readonly ErrorConsoleService? _errorConsole;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// The library parsed by the analysis pass, handed back to the import so a
    /// large file is read once, not twice. Held only for the dialog's lifetime.
    /// </summary>
    private GdsLibrary? _analyzedLibrary;

    /// <summary>Absolute path of the .gds file being imported (chosen before the dialog opens).</summary>
    public string GdsFilePath { get; }

    /// <summary>True while analysis or import is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _isBusy;

    /// <summary>Progress/status line shown under the busy bar.</summary>
    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("GdsImport.StatusAnalyzing");

    /// <summary>User-readable failure message (analysis or import).</summary>
    [ObservableProperty]
    private string _errorText = "";

    /// <summary>True when the last operation failed; shows the error panel and Retry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOptions))]
    private bool _hasError;

    /// <summary>True once analysis succeeded and the options section is usable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(ShowOptions))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _analysisReady;

    /// <summary>Top-cell candidates with their direct instance counts.</summary>
    public ObservableCollection<GdsTopCellSummary> TopCells { get; } = new();

    /// <summary>The top cell to import.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private GdsTopCellSummary? _selectedTopCell;

    /// <summary>True for "explode hierarchy into components" (default), false for black-box.</summary>
    [ObservableProperty]
    private bool _isExplodeMode = true;

    /// <summary>Factory default for the port-layers field (Lunima/gdsfactory/demofab conventions).</summary>
    private const string DefaultPortLayersText = "1,10;501,1";

    /// <summary>Factory default for the waveguide-layers field.</summary>
    private const string DefaultWaveguideLayersText = "1,0; 1111,0";

    /// <summary>Factory default for the metal-layers field (Lunima's own export layers + SiEPIC).</summary>
    private const string DefaultMetalLayersText = "11,0; 12,0; 13,0";

    /// <summary>
    /// Port-label layers as "layer,datatype" pairs, ';'-separated. The default mirrors
    /// <see cref="GdsPinDetectionOptions.PortLayers"/>: 1,10 (gdsfactory port labels)
    /// plus 501,1 (nazca demofab's bb_pin_text layer) — shown explicitly because the
    /// import service would union demofab's layer in invisibly otherwise.
    /// </summary>
    [ObservableProperty]
    private string _portLayersText = DefaultPortLayersText;

    /// <summary>
    /// Waveguide-core layers as "layer,datatype" pairs, ';'-separated. One field,
    /// two roles: pin detection (edge heuristic, direction rule) AND optical
    /// route reconstruction of the top cell's drawn routes — a foundry whose
    /// optical routing lives on its own layer needs it entered here to get
    /// optical connections back. Default: gdsfactory's core (1,0) plus nazca's
    /// interconnect layer (1111,0), mirroring the importer defaults.
    /// </summary>
    [ObservableProperty]
    private string _waveguideLayersText = DefaultWaveguideLayersText;

    /// <summary>
    /// METAL layers as "layer,datatype" pairs, ';'-separated. Polygons on these
    /// layers count as electrical: top-cell routes on them reconstruct as metal
    /// connections, and label pins touching them classify electrical. Defaults
    /// to our own exporter's trace/bridge layers plus SiEPIC's pad opening —
    /// foundry files that assign these numbers differently import their optical
    /// routing as electrical until corrected here.
    /// </summary>
    [ObservableProperty]
    private string _metalLayersText = DefaultMetalLayersText;

    /// <summary>
    /// Recreate the detected connections with Lunima's own routing (default: on).
    /// The flag flows into <see cref="GdsPlacementExecutor.ExecuteAsync"/>: the
    /// route-derived connections become real router-generated waveguides/metal
    /// traces instead of keeping the imported route geometry as frozen paths.
    /// Real connectivity always comes from the GDS route structure — this option
    /// only decides HOW the detected connections get their geometry. Very large
    /// imports fall back to frozen geometry automatically
    /// (<see cref="GdsPlacementExecutor.MaxReroutedConnections"/>).
    /// </summary>
    [ObservableProperty]
    private bool _rerouteConnectionsRequested = true;

    /// <summary>True once an import finished successfully; switches the dialog to the result view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOptions))]
    [NotifyPropertyChangedFor(nameof(CloseButtonText))]
    private bool _importCompleted;

    /// <summary>One-line outcome summary ("Placed N components, connected M pins…").</summary>
    [ObservableProperty]
    private string _resultSummaryText = "";

    /// <summary>All import + placement warnings and skip reasons, shown in the result view.</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>True when <see cref="Warnings"/> has entries (drives the warnings list visibility).</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// Informational import notes (known-component resolutions, skipped
    /// zero-geometry/export-artifact cells), shown in the result view's info
    /// section and mirrored to the error console at info level.
    /// </summary>
    public ObservableCollection<string> Infos { get; } = new();

    /// <summary>True when <see cref="Infos"/> has entries (drives the info list visibility).</summary>
    public bool HasInfos => Infos.Count > 0;

    /// <summary>True when anything was mirrored to the error console (drives the hint's visibility).</summary>
    public bool HasResultNotes => HasWarnings || HasInfos;

    /// <summary>Invoked by <see cref="CancelCommand"/> when the dialog should close. Set by the view.</summary>
    public Action? OnClose { get; set; }

    /// <summary>
    /// Callback to zoom the canvas so the whole content fits the viewport, fired
    /// once after a successful import placement (executor completed, at least one
    /// component placed) — never on failure, cancellation or a zero-placement run.
    /// Mirrors <c>FileOperationsViewModel.ZoomToFitAfterLoad</c>: the fallback
    /// viewport size passed here is replaced by the real one in the view layer.
    /// Wired by <see cref="GdsImportButtonViewModel"/>.
    /// </summary>
    public Action<double, double>? ZoomToFitAfterImport { get; set; }

    /// <summary>
    /// Invoked with the chip size (µm) the executor auto-applied when the
    /// imported design was bigger than the playfield — wired to the chip-size
    /// settings ViewModel so the settings panel and user-visible tile counts
    /// stay in sync with what the canvas already shows.
    /// </summary>
    public Action<double, double>? ApplyChipSizeAfterImport { get; set; }

    /// <summary>True when the Import button can run (analysis done, top cell picked, not busy).</summary>
    public bool CanImport => AnalysisReady && !IsBusy && SelectedTopCell is not null;

    /// <summary>True while the options section (top cell, mode, layers) is shown.</summary>
    public bool ShowOptions => AnalysisReady && !ImportCompleted;

    /// <summary>Label of the dismiss button: Cancel before/during the flow, Close after completion.</summary>
    public string CloseButtonText => LocalizationService.Instance.Translate(
        ImportCompleted ? "Common.Close" : "Common.Cancel");

    /// <summary>Initializes a new <see cref="GdsImportDialogViewModel"/>.</summary>
    /// <param name="gdsFilePath">Absolute path of the .gds file to import.</param>
    /// <param name="importService">Import orchestrator (parse → register → persist).</param>
    /// <param name="placementExecutor">Canvas placement executor for the import outcome.</param>
    /// <param name="errorConsole">
    /// Optional error console: every result warning (warn level), info note (info
    /// level) and failure message (error level) is mirrored there as a distinct,
    /// copyable entry (same pattern as the export VMs).
    /// </param>
    public GdsImportDialogViewModel(
        string gdsFilePath,
        GdsImportService importService,
        GdsPlacementExecutor placementExecutor,
        ErrorConsoleService? errorConsole = null)
    {
        GdsFilePath = gdsFilePath ?? throw new ArgumentNullException(nameof(gdsFilePath));
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _placementExecutor = placementExecutor ?? throw new ArgumentNullException(nameof(placementExecutor));
        _errorConsole = errorConsole;
        Warnings.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(HasResultNotes));
        };
        Infos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInfos));
            OnPropertyChanged(nameof(HasResultNotes));
        };
    }

    /// <summary>
    /// Foreign (nazca-stamped) files must not inherit Lunima's own layer
    /// defaults: our port/waveguide/metal numbers are our export conventions,
    /// and on a foundry file they collide with its layer map (one foundry's
    /// metal number is another's core etch). Only untouched factory defaults
    /// are cleared — a field the user edited stays as-is.
    /// </summary>
    private void ClearFactoryDefaultsOnForeignFile(CAP_DataAccess.Import.Gds.GdsLibrary library)
    {
        if (!CAP_DataAccess.Import.Gds.GdsFileProvenance.HasForeignStamps(library))
            return;
        if (PortLayersText == DefaultPortLayersText)
            PortLayersText = "";
        if (WaveguideLayersText == DefaultWaveguideLayersText)
            WaveguideLayersText = "";
        if (MetalLayersText == DefaultMetalLayersText)
            MetalLayersText = "";
    }

    /// <summary>
    /// Runs the library analysis (top-cell candidates). Called by the view when the
    /// dialog opens, and again by the Retry button after a failure. Re-entrant-safe:
    /// a second call while busy is a no-op.
    /// </summary>
    public async Task StartAnalysisAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        HasError = false;
        ErrorText = "";
        AnalysisReady = false;
        ImportCompleted = false;
        Warnings.Clear();
        Infos.Clear();
        PopulateCensus(Array.Empty<CAP_DataAccess.Import.Gds.LayerCensus.GdsLayerCensusEntry>());
        StatusText = LocalizationService.Instance.Translate("GdsImport.StatusAnalyzing");
        // Capture the token BEFORE the first await: a window close mid-run
        // disposes the source, and CancellationTokenSource.Token throws
        // ObjectDisposedException afterwards — the token itself stays valid
        // and observes the cancel that preceded the dispose.
        var token = ResetCancellationSource().Token;

        try
        {
            var analysis = await GdsImportService.AnalyzeAsync(GdsFilePath, token);
            _analyzedLibrary = analysis.Library;
            ClearFactoryDefaultsOnForeignFile(analysis.Library);
            PopulateCensus(analysis.LayerCensus);
            TopCells.Clear();
            foreach (var topCell in analysis.TopCells)
                TopCells.Add(topCell);
            SelectedTopCell = TopCells.FirstOrDefault();
            AnalysisReady = true;
            StatusText = string.Format(
                LocalizationService.Instance.Translate("GdsImport.StatusAnalyzed"),
                analysis.CellCount, TopCells.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Instance.Translate("GdsImport.StatusCancelled");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            HasError = true;
            StatusText = LocalizationService.Instance.Translate("GdsImport.StatusAnalysisFailed");
            _errorConsole?.LogError("GDS import analysis failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs the import for the selected top cell with the configured options, then
    /// executes the placement plan on the canvas. On success the dialog switches to
    /// the result view; failures surface in the error panel with the options intact.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (SelectedTopCell is null) return;

        if (!TryBuildOptions(out var options, out var optionsError))
        {
            ErrorText = optionsError!;
            HasError = true;
            _errorConsole?.LogError($"GDS import: {ErrorText}");
            return;
        }

        IsBusy = true;
        HasError = false;
        ErrorText = "";
        Warnings.Clear();
        Infos.Clear();
        // Token captured before the first await — see StartAnalysisAsync: a close
        // mid-run disposes the source, and reading cts.Token after an await would
        // throw ObjectDisposedException instead of unwinding as a cancellation.
        var token = ResetCancellationSource().Token;

        // The dialog closes as soon as the import STARTS — the placement and
        // re-routing of a large import take minutes and the user should not
        // have to keep the window open for them (field report: "Connecting
        // Pins 116/116" and the dialog just waiting). Progress lands in the
        // status text, and every warning/info is mirrored to the error console
        // when the run completes. The window close must NOT cancel this run —
        // OnWindowClosed checks the flag.
        _continueRunAfterWindowClose = true;
        OnClose?.Invoke();

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var outcome = await _importService.ImportAsync(
                GdsFilePath, SelectedTopCell.CellName, options, progress, token,
                preParsedLibrary: _analyzedLibrary);
            ImportServiceCompletedTestHook?.Invoke();

            token.ThrowIfCancellationRequested();
            var plan = GdsPlacementPlan.FromOutcome(outcome);
            var report = await _placementExecutor.ExecuteAsync(
                plan, progress, token, RerouteConnectionsRequested);

            foreach (var warning in outcome.Warnings)
                Warnings.Add(warning);
            foreach (var info in outcome.Infos)
                Infos.Add(info);
            foreach (var warning in report.Warnings)
                Warnings.Add(warning);
            foreach (var skipped in report.SkippedPlacements)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.SkippedPlacementFormat"), skipped));
            foreach (var skipped in report.SkippedConnections)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.SkippedConnectionFormat"), skipped));
            foreach (var issue in report.ValidationWarnings)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.ValidationWarningFormat"), issue));

            ResultSummaryText = BuildSummary(report);
            ImportCompleted = true;
            StatusText = "";

            // Mirror every line of the result view into the error console as a
            // distinct entry — the dialog texts are not selectable, the console's
            // are (same pattern as the export warnings in FileOperationsViewModel).
            // Warnings stay warnings; informational notes log at info level.
            foreach (var warning in Warnings)
                _errorConsole?.LogWarning(warning);
            foreach (var info in Infos)
                _errorConsole?.LogInfo(info);

            // Imported content lands at its GDS coordinates, which can sit far
            // off-screen — zoom the canvas to the whole content so the user sees
            // what was placed (same semantics as ZoomToFitAfterLoad on the
            // design-load path). Only after a real placement: a failed/cancelled
            // run never reaches this point, and a 0-placement import has nothing
            // to show.
            if (report.ChipEnlargedToWidthUm is double chipW && report.ChipEnlargedToHeightUm is double chipH)
                ApplyChipSizeAfterImport?.Invoke(chipW, chipH);
            if (report.PlacedCount > 0)
                ZoomToFitAfterImport?.Invoke(900, 800);
        }
        catch (OperationCanceledException)
        {
            // Name the damage: placements made before the cancel stay on the
            // canvas, and a naive re-import would stack a second copy on top.
            StatusText = string.Format(
                LocalizationService.Instance.Translate("GdsImport.StatusCancelledAfterPlacement"),
                _placementExecutor.PlacedCountSoFar);
            _errorConsole?.LogWarning($"GDS import: {StatusText}");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            HasError = true;
            StatusText = LocalizationService.Instance.Translate("GdsImport.StatusImportFailed");
            _errorConsole?.LogError("GDS import failed", ex);
        }
        finally
        {
            IsBusy = false;
            // A run that outlived its window disposes the cancellation source
            // itself — OnWindowClosed deliberately left it alone.
            if (_continueRunAfterWindowClose)
            {
                _continueRunAfterWindowClose = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    /// <summary>Retries analysis after a failure (same file).</summary>
    [RelayCommand]
    private async Task RetryAnalysis()
    {
        await StartAnalysisAsync();
    }
}
