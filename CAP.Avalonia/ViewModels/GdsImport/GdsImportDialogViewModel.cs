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

    /// <summary>
    /// Port-label layers as "layer,datatype" pairs, ';'-separated. The default mirrors
    /// <see cref="GdsPinDetectionOptions.PortLayers"/>: 1,10 (gdsfactory port labels)
    /// plus 501,1 (nazca demofab's bb_pin_text layer) — shown explicitly because the
    /// import service would union demofab's layer in invisibly otherwise.
    /// </summary>
    [ObservableProperty]
    private string _portLayersText = "1,10;501,1";

    /// <summary>Waveguide-core layers as "layer,datatype" pairs, ';'-separated (gdsfactory default: 1,0).</summary>
    [ObservableProperty]
    private string _waveguideLayersText = "1,0";

    /// <summary>
    /// Metal-route (electrical) layers as "layer,datatype" pairs, ';'-separated.
    /// Empty (default) = AUTO: Lunima's own exports get the exporter metal
    /// defaults (11,0)/(12,0), foreign files get none — foundry layer tables
    /// assign those numbers differently, and treating them as metal turned
    /// optical routes electrical. A non-empty value is an explicit
    /// mapping and feeds both the metal route matching and the electrical pin
    /// inference.
    /// </summary>
    [ObservableProperty]
    private string _metalLayersText = "";

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
        StatusText = LocalizationService.Instance.Translate("GdsImport.StatusAnalyzing");
        var cts = ResetCancellationSource();

        try
        {
            var analysis = await GdsImportService.AnalyzeAsync(GdsFilePath, cts.Token);
            _analyzedLibrary = analysis.Library;
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
        var cts = ResetCancellationSource();

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var outcome = await _importService.ImportAsync(
                GdsFilePath, SelectedTopCell.CellName, options, progress, cts.Token,
                preParsedLibrary: _analyzedLibrary);

            var plan = GdsPlacementPlan.FromOutcome(outcome);
            var report = await _placementExecutor.ExecuteAsync(
                plan, progress, cts.Token, RerouteConnectionsRequested);

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
        }
    }

    /// <summary>Retries analysis after a failure (same file).</summary>
    [RelayCommand]
    private async Task RetryAnalysis()
    {
        await StartAnalysisAsync();
    }

    /// <summary>Cancels the running operation; closes the dialog when idle or completed.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            CancelCurrentRun();
            return;
        }
        OnClose?.Invoke();
    }

    /// <summary>
    /// Called by the view when the dialog window closes: cancels the running
    /// operation (if any) and releases the per-run cancellation source. A close
    /// mid-import must not leave the background run mutating a canvas the user
    /// no longer sees.
    /// </summary>
    public void OnWindowClosed()
    {
        CancelCurrentRun();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Replaces the per-run cancellation source, disposing the previous run's, and
    /// returns the new source. Both entry points no-op while <see cref="IsBusy"/>,
    /// so the replaced source always belongs to a finished run — but a late
    /// progress callback, a queued await continuation or a FileStream read of the
    /// service's off-thread parse may still REFERENCE its token. Cancel BEFORE
    /// dispose: every .NET registration path (stream reads, task cancellation
    /// wiring, semaphore waits) short-circuits on an already-cancelled source,
    /// while registering on a disposed-not-cancelled source throws
    /// <see cref="ObjectDisposedException"/> ("The CancellationTokenSource has
    /// been disposed") — the import failure this ordering prevents.
    /// </summary>
    private CancellationTokenSource ResetCancellationSource()
    {
        CancelCurrentRun();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts;
    }

    /// <summary>
    /// Cancels the current run's source, tolerating one that a racing reset or
    /// window close already disposed: <see cref="CancellationTokenSource.Cancel"/>
    /// throws <see cref="ObjectDisposedException"/> on a disposed source, which
    /// must never escape as an import failure (the run is over either way).
    /// </summary>
    private void CancelCurrentRun()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The source was already released by a racing reset/close — nothing to cancel.
        }
    }

    /// <summary>Test seam (InternalsVisibleTo UnitTests): the current per-run cancellation source.</summary>
    internal CancellationTokenSource? CurrentCts => _cts;

    private static string BuildSummary(GdsPlacementReport report)
    {
        var summary = string.Format(
            LocalizationService.Instance.Translate("GdsImport.ResultSummary"),
            report.PlacedCount, report.ConnectedCount);
        if (report.RouteDerivedCount > 0 || report.FrozenRoutePathCount > 0)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultRouteReconstructionSuffix"),
                report.RouteDerivedCount, report.FrozenRoutePathCount);
        }
        if (report.ReroutedCount > 0)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultReroutedSuffix"),
                report.ReroutedCount);
        }
        if (report.GroupCreated)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultGroupSuffix"), report.GroupName);
        }
        return summary;
    }
}
