using System.Collections.ObjectModel;
using System.Globalization;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Import.Gds;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// ViewModel for the GDS import dialog (issue #808). The .gds file is chosen
/// before the dialog opens; the dialog analyzes it on open (top-cell candidates),
/// lets the user pick the top cell, hierarchy mode and pin-detection layers, then
/// runs the import and places the result on the canvas via
/// <see cref="GdsPlacementExecutor"/>. The outcome (placed/connected counts plus
/// all warnings) stays visible until the user closes the dialog.
/// </summary>
public partial class GdsImportDialogViewModel : ObservableObject
{
    private readonly GdsImportService _importService;
    private readonly GdsPlacementExecutor _placementExecutor;
    private CancellationTokenSource? _cts;

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

    /// <summary>Port-label layers as "layer,datatype" pairs, ';'-separated (gdsfactory default: 1,10).</summary>
    [ObservableProperty]
    private string _portLayersText = "1,10";

    /// <summary>Waveguide-core layers as "layer,datatype" pairs, ';'-separated (gdsfactory default: 1,0).</summary>
    [ObservableProperty]
    private string _waveguideLayersText = "1,0";

    /// <summary>
    /// Auto-connect free pins after placement (experimental). When set, the flag
    /// flows into <see cref="GdsPlacementExecutor.ExecuteAsync"/>: a pass pairs
    /// unconnected optical pins of the placed instances that face each other
    /// within <see cref="AutoConnectRadiusText"/> after the abutment connections,
    /// followed by a validation run whose issues land in the result warnings.
    /// </summary>
    [ObservableProperty]
    private bool _autoConnectRequested;

    /// <summary>
    /// Pairing radius (µm) for the auto-connect pass, as typed by the user;
    /// validated at import time (positive number, invariant culture).
    /// </summary>
    [ObservableProperty]
    private string _autoConnectRadiusText = "1000";

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

    /// <summary>Invoked by <see cref="CancelCommand"/> when the dialog should close. Set by the view.</summary>
    public Action? OnClose { get; set; }

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
    public GdsImportDialogViewModel(
        string gdsFilePath,
        GdsImportService importService,
        GdsPlacementExecutor placementExecutor)
    {
        GdsFilePath = gdsFilePath ?? throw new ArgumentNullException(nameof(gdsFilePath));
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _placementExecutor = placementExecutor ?? throw new ArgumentNullException(nameof(placementExecutor));
        Warnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWarnings));
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
        StatusText = LocalizationService.Instance.Translate("GdsImport.StatusAnalyzing");
        _cts = new CancellationTokenSource();

        try
        {
            var analysis = await GdsImportService.AnalyzeAsync(GdsFilePath, _cts.Token);
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
            return;
        }

        if (!TryParseAutoConnectRadius(out var autoConnectRadiusUm))
        {
            ErrorText = string.Format(
                LocalizationService.Instance.Translate("GdsImport.ErrorRadiusSyntax"), AutoConnectRadiusText);
            HasError = true;
            return;
        }

        IsBusy = true;
        HasError = false;
        ErrorText = "";
        Warnings.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var outcome = await _importService.ImportAsync(
                GdsFilePath, SelectedTopCell.CellName, options, progress, _cts.Token);

            var plan = GdsPlacementPlan.FromOutcome(outcome);
            var report = await _placementExecutor.ExecuteAsync(
                plan, progress, _cts.Token, AutoConnectRequested, autoConnectRadiusUm);

            foreach (var warning in outcome.Warnings)
                Warnings.Add(warning);
            foreach (var warning in report.Warnings)
                Warnings.Add(warning);
            foreach (var skipped in report.SkippedPlacements)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.SkippedPlacementFormat"), skipped));
            foreach (var skipped in report.SkippedConnections)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.SkippedConnectionFormat"), skipped));
            foreach (var pair in report.AutoConnectedPairs)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.AutoConnectedFormat"), pair));
            foreach (var skipped in report.SkippedAutoConnect)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.SkippedAutoConnectFormat"), skipped));
            foreach (var issue in report.ValidationWarnings)
                Warnings.Add(string.Format(
                    LocalizationService.Instance.Translate("GdsImport.ValidationWarningFormat"), issue));

            ResultSummaryText = BuildSummary(report, AutoConnectRequested);
            ImportCompleted = true;
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Instance.Translate("GdsImport.StatusCancelled");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            HasError = true;
            StatusText = LocalizationService.Instance.Translate("GdsImport.StatusImportFailed");
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
            _cts?.Cancel();
            return;
        }
        OnClose?.Invoke();
    }

    private static string BuildSummary(GdsPlacementReport report, bool autoConnectRequested)
    {
        var summary = string.Format(
            LocalizationService.Instance.Translate("GdsImport.ResultSummary"),
            report.PlacedCount, report.ConnectedCount);
        if (autoConnectRequested)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultAutoConnectSuffix"),
                report.AutoConnectedCount);
        }
        if (report.GroupCreated)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultGroupSuffix"), report.GroupName);
        }
        return summary;
    }

    /// <summary>
    /// Parses the auto-connect radius field (invariant culture, so the field behaves
    /// identically on every machine locale). Only consulted when auto-connect is on;
    /// an unchecked box always yields the default radius.
    /// </summary>
    private bool TryParseAutoConnectRadius(out double radiusUm)
    {
        radiusUm = GdsPlacementExecutor.DefaultAutoConnectRadiusUm;
        if (!AutoConnectRequested)
            return true;

        return double.TryParse(
                   AutoConnectRadiusText,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out radiusUm)
               && radiusUm > 0
               && !double.IsInfinity(radiusUm);
    }

    /// <summary>Builds the import options from the mode radio and the layer text fields.</summary>
    private bool TryBuildOptions(out GdsHierarchyImportOptions options, out string? error)
    {
        options = new GdsHierarchyImportOptions();
        error = null;

        var portLayers = ParseLayerPairs(PortLayersText);
        if (portLayers is null)
        {
            error = string.Format(
                LocalizationService.Instance.Translate("GdsImport.ErrorLayerSyntax"), PortLayersText);
            return false;
        }
        var waveguideLayers = ParseLayerPairs(WaveguideLayersText);
        if (waveguideLayers is null)
        {
            error = string.Format(
                LocalizationService.Instance.Translate("GdsImport.ErrorLayerSyntax"), WaveguideLayersText);
            return false;
        }

        options = options with
        {
            Mode = IsExplodeMode ? GdsHierarchyImportMode.ExplodeHierarchy : GdsHierarchyImportMode.BlackBox,
            PinDetection = new GdsPinDetectionOptions
            {
                PortLayers = portLayers,
                WaveguideLayers = waveguideLayers,
            },
        };
        return true;
    }

    /// <summary>
    /// Parses "layer,datatype" pairs separated by ';' (e.g. <c>1,10</c> or
    /// <c>1,10; 2,0</c>). Returns null when any segment is malformed.
    /// </summary>
    internal static List<(int Layer, int Datatype)>? ParseLayerPairs(string text)
    {
        var pairs = new List<(int, int)>();
        foreach (var segment in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var layer)
                || !int.TryParse(parts[1], out var datatype))
            {
                return null;
            }
            pairs.Add((layer, datatype));
        }
        return pairs.Count > 0 ? pairs : null;
    }
}
