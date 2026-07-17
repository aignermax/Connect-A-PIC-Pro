using System.Collections.ObjectModel;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
// PdkOffsetCalibration lives in CAP_DataAccess.Components.ComponentDraftMapper —
// the using directive above already pulls it into scope.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.PdkOffset;

/// <summary>
/// ViewModel for the PDK Component Offset Editor window.
/// Allows browsing all PDK components, inspecting NazcaOriginOffset status,
/// editing offset values with a live pin-position preview, and saving back to JSON.
/// Optionally displays a Nazca GDS overlay when a preview service is provided.
/// Split across three partial files to stay within the file-size limits:
/// this file owns state + load/save/select; <see cref="PdkOffsetEditorViewModel.Calibration"/>
/// owns the pin-alignment + Auto-Calibrate + Check-All / Try-Fix-All commands;
/// <see cref="PdkOffsetEditorViewModel.Overlay"/> owns the Nazca render + canvas math.
/// </summary>
public partial class PdkOffsetEditorViewModel : ObservableObject
{
    private readonly PdkLoader _pdkLoader;
    private readonly PdkJsonSaver _pdkSaver;
    private readonly PdkManagerViewModel _pdkManager;
    private readonly NazcaComponentPreviewService? _previewService;

    /// <summary>gdsfactory preview back-end for gdsfactory-native components (#570); null → no preview.
    /// Typed as the base so it can be mocked in tests; DI injects the
    /// <see cref="GdsFactoryComponentPreviewService"/> instance.</summary>
    private readonly NazcaComponentPreviewService? _gdsFactoryPreviewService;

    private PdkDraft? _loadedPdk;
    private string? _loadedFilePath;
    private double _nazcaCanvasRefX;
    private double _nazcaCanvasRefY;
    // Right / bottom extent of the rendered Nazca geometry in canvas pixels.
    // Used so CanvasTotalWidth/Height also covers SiEPIC PCells whose actual
    // bbox is wider than the Lunima JSON's WidthMicrometers/HeightMicrometers.
    private double _nazcaCanvasRight;
    private double _nazcaCanvasBottom;
    private CancellationTokenSource? _renderCts;
    // Cached so the Auto-Calibrate command can read the bbox + pin positions
    // of the most recent successful render without rerunning the Python helper.
    private NazcaPreviewResult? _lastNazcaResult;

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("PdkOffset.Status.LoadToBegin");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoCalibrateCommand))]
    private PdkComponentOffsetItemViewModel? _selectedComponent;

    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private double _componentWidth;
    [ObservableProperty] private double _componentHeight;
    [ObservableProperty] private bool _isNazcaRendering;
    [ObservableProperty] private string _nazcaOverlayStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoCalibrateCommand))]
    private bool _hasNazcaOverlay;

    /// <summary>
    /// User toggle for the Nazca GDS overlay. When false the overlay is hidden
    /// even if a successful render is cached — useful when the overlay clutters
    /// the visual or when comparing pin alignment without the polygon noise.
    /// </summary>
    [ObservableProperty] private bool _showNazcaOverlay = true;

    /// <summary>
    /// One-line summary of the pin-alignment check ("3/4 pins aligned" etc.)
    /// computed against the cached Nazca render. Empty until the first render.
    /// </summary>
    [ObservableProperty] private string _pinAlignmentSummary = "";

    /// <summary>
    /// Tolerance in micrometres below which a Lunima pin is considered to be
    /// aligned with its nearest Nazca pin. 0.5 µm is generous enough to ignore
    /// sub-grid rounding noise without masking real offset errors.
    /// </summary>
    public const double PinAlignmentToleranceMicrometers = 0.5;

    /// <summary>Per-pin alignment detail. Populated by <see cref="ComputePinAlignment"/>.</summary>
    public ObservableCollection<PinAlignmentInfo> PinAlignmentResults { get; } = new();

    /// <summary>One row per component from the most recent Check-All / Try-Fix-All run.</summary>
    public ObservableCollection<ComponentCheckResult> BatchCheckResults { get; } = new();

    /// <summary>True while a batch (Check-All or Try-Fix-All) is iterating components.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(TryFixAllCommand))]
    private bool _isBatchRunning;

    /// <summary>Status line shown above the batch report ("[3/14] ebeam_y_1550…").</summary>
    [ObservableProperty] private string _batchProgress = "";

    /// <summary>One-line summary shown after a batch completes.</summary>
    [ObservableProperty] private string _batchSummary = "";

    private CancellationTokenSource? _batchCts;

    /// <summary>
    /// Source snippet for the currently selected component — what the preview
    /// would tell Python to render. Empty until a component is selected.
    /// Populated by <see cref="OnSelectedComponentChanged"/>.
    /// </summary>
    [ObservableProperty] private string _previewSource = "";

    /// <summary>
    /// Callback for copying text to the OS clipboard. Wired by the window's
    /// code-behind via <c>TopLevel.GetTopLevel(this).Clipboard</c>.
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Copies the Nazca overlay status text to the clipboard.</summary>
    [RelayCommand]
    private async Task CopyStatus()
    {
        if (CopyToClipboard == null || string.IsNullOrEmpty(NazcaOverlayStatus)) return;
        await CopyToClipboard(NazcaOverlayStatus);
    }

    /// <summary>Window-side hook that resets the canvas zoom slider to 1.0.</summary>
    public Action? ResetZoomHook { get; set; }

    /// <summary>Resets the overlay zoom to 1:1 via the window's slider.</summary>
    [RelayCommand]
    private void ResetZoom() => ResetZoomHook?.Invoke();

    /// <summary>Currently selected PDK from the installed-PDK dropdown; triggers load on change.</summary>
    [ObservableProperty]
    private PdkInfoViewModel? _selectedInstalledPdk;

    /// <summary>All components from the loaded PDK, with offset status badges.</summary>
    public ObservableCollection<PdkComponentOffsetItemViewModel> Components { get; } = new();

    /// <summary>Pin positions for the currently selected component, recalculated on offset change.</summary>
    public ObservableCollection<PinPositionViewModel> PinPositions { get; } = new();

    /// <summary>PDKs currently registered in Lunima, exposed for the installed-PDK dropdown.</summary>
    public ObservableCollection<PdkInfoViewModel> AvailablePdks => _pdkManager.LoadedPdks;

    /// <summary>File dialog service for picking a PDK JSON file to load.</summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>Component bounding-box width in canvas pixels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasTotalWidth))]
    private double _canvasComponentWidth;

    /// <summary>Component bounding-box height in canvas pixels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasTotalHeight))]
    private double _canvasComponentHeight;

    /// <summary>Nazca origin X position in canvas pixels (for the crosshair).</summary>
    [ObservableProperty] private double _canvasOriginX;

    /// <summary>Nazca origin Y position in canvas pixels (for the crosshair).</summary>
    [ObservableProperty] private double _canvasOriginY;

    /// <summary>X offset of the component bounding box inside the canvas.</summary>
    [ObservableProperty] private double _canvasComponentLeft = CanvasPadding;

    /// <summary>Y offset of the component bounding box inside the canvas.</summary>
    [ObservableProperty] private double _canvasComponentTop = CanvasPadding;

    /// <summary>
    /// Total canvas width in pixels — sized to fit both the Lunima box and
    /// any Nazca geometry that extends past it. The user pans the
    /// surrounding ScrollViewer to bring off-screen geometry into view; the
    /// canvas itself stays at its natural size so the offset/alignment
    /// visualisation is geometrically meaningful and doesn't squish.
    /// </summary>
    public double CanvasTotalWidth =>
        Math.Max(CanvasComponentWidth + CanvasPadding * 2, _nazcaCanvasRight + CanvasPadding);

    /// <inheritdoc cref="CanvasTotalWidth"/>
    public double CanvasTotalHeight =>
        Math.Max(CanvasComponentHeight + CanvasPadding * 2, _nazcaCanvasBottom + CanvasPadding);

    /// <summary>Pin markers for visual canvas overlay (canvas-pixel coordinates).</summary>
    public ObservableCollection<PinMarker> PinMarkers { get; } = new();

    /// <summary>Nazca GDS polygon markers for the overlay (canvas-pixel coordinates).</summary>
    public ObservableCollection<NazcaPolygonMarker> NazcaPolygons { get; } = new();

    /// <summary>Nazca GDS pin stub markers for the overlay (canvas-pixel coordinates).</summary>
    public ObservableCollection<NazcaStubMarker> NazcaPinStubs { get; } = new();

    private const double CanvasScale = 2.0;
    private const double CanvasPadding = 20.0;

    /// <summary>
    /// Initializes the ViewModel with required services and optional preview service.
    /// </summary>
    /// <summary>
    /// UI-thread marshaller for async render results. Default uses Avalonia's
    /// dispatcher; tests can inject a synchronous executor so the unit-test
    /// runner doesn't have to host an Avalonia application just to wait for
    /// the render to apply.
    /// </summary>
    internal Func<Action, Task> UiThreadMarshaller { get; set; } =
        static async action => await Dispatcher.UIThread.InvokeAsync(action);

    public PdkOffsetEditorViewModel(
        PdkLoader pdkLoader,
        PdkJsonSaver pdkSaver,
        PdkManagerViewModel pdkManager,
        NazcaComponentPreviewService? previewService = null,
        NazcaComponentPreviewService? gdsFactoryPreviewService = null)
    {
        _pdkLoader = pdkLoader;
        _pdkSaver = pdkSaver;
        _pdkManager = pdkManager;
        _previewService = previewService;
        _gdsFactoryPreviewService = gdsFactoryPreviewService;
    }

    /// <summary>Opens a file dialog and loads the selected PDK JSON file.</summary>
    [RelayCommand]
    private async Task LoadPdkFile()
    {
        if (FileDialogService == null)
        {
            StatusText = LocalizationService.Instance.Translate("PdkOffset.Status.NoFileDialog");
            return;
        }

        var path = await FileDialogService.ShowOpenFileDialogAsync(
            "Open PDK JSON",
            "JSON Files|*.json|All Files|*.*");

        if (string.IsNullOrEmpty(path)) return;

        try
        {
            LoadPdkFromPath(path);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.LoadFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Applies the current OffsetX/OffsetY (and optional Width/Height) to the selected
    /// component draft and refreshes the pin position table.
    /// </summary>
    [RelayCommand]
    private void ApplyOffset()
    {
        if (SelectedComponent == null)
        {
            StatusText = LocalizationService.Instance.Translate("PdkOffset.Status.SelectComponentFirst");
            return;
        }

        if (!double.IsFinite(OffsetX) || !double.IsFinite(OffsetY))
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.OffsetNotFinite"), OffsetX, OffsetY);
            return;
        }

        SelectedComponent.Draft.NazcaOriginOffsetX = OffsetX;
        SelectedComponent.Draft.NazcaOriginOffsetY = OffsetY;
        SelectedComponent.Draft.WidthMicrometers = ComponentWidth;
        SelectedComponent.Draft.HeightMicrometers = ComponentHeight;
        SelectedComponent.RefreshStatus();

        RefreshPinPositions(SelectedComponent.Draft);
        RefreshCanvasMarkers(SelectedComponent.Draft);
        HasUnsavedChanges = true;
        StatusText = string.Format(
            LocalizationService.Instance.Translate("PdkOffset.Status.OffsetUpdated"), SelectedComponent.ComponentName);
    }

    /// <summary>Saves the current PDK draft back to its source JSON file.</summary>
    [RelayCommand]
    private void SavePdk()
    {
        if (_loadedPdk == null || string.IsNullOrEmpty(_loadedFilePath))
        {
            StatusText = LocalizationService.Instance.Translate("PdkOffset.Status.NothingToSave");
            return;
        }

        try
        {
            _pdkSaver.SaveToFile(_loadedPdk, _loadedFilePath);
            HasUnsavedChanges = false;
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.SavedTo"), Path.GetFileName(_loadedFilePath));
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.SaveFailed"), ex.Message);
        }
    }

    partial void OnSelectedInstalledPdkChanged(PdkInfoViewModel? value)
    {
        if (value == null) return;

        if (string.IsNullOrEmpty(value.FilePath))
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.NoFilePath"), value.Name);
            return;
        }

        try
        {
            LoadPdkFromPath(value.FilePath);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.LoadFailed"), ex.Message);
        }
    }

    partial void OnSelectedComponentChanged(PdkComponentOffsetItemViewModel? value)
    {
        if (value == null)
        {
            PinPositions.Clear();
            PinMarkers.Clear();
            NazcaPolygons.Clear();
            NazcaPinStubs.Clear();
            HasNazcaOverlay = false;
            return;
        }

        OffsetX = value.Draft.NazcaOriginOffsetX ?? 0;
        OffsetY = value.Draft.NazcaOriginOffsetY ?? 0;
        ComponentWidth = value.Draft.WidthMicrometers;
        ComponentHeight = value.Draft.HeightMicrometers;

        if (value.Status == OffsetStatus.Missing)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.NoOffsetInJson"), value.ComponentName);
        }

        // Reset overlay state for new component
        HasNazcaOverlay = false;
        _lastNazcaResult = null;
        NazcaPolygons.Clear();
        NazcaPinStubs.Clear();
        NazcaOverlayStatus = "";
        PinAlignmentSummary = "";
        PinAlignmentResults.Clear();
        _nazcaCanvasRight = 0;
        _nazcaCanvasBottom = 0;
        OnPropertyChanged(nameof(CanvasTotalWidth));
        OnPropertyChanged(nameof(CanvasTotalHeight));
        PreviewSource = BuildPreviewSource(value.Draft);

        RefreshPinPositions(value.Draft);
        RefreshCanvasMarkers(value.Draft);

        if (_previewService != null)
            _ = TriggerNazcaRenderAsync(value.Draft);
    }

    /// <summary>
    /// Loads a PDK from the given file path using the editing-tolerant loader
    /// and populates the component list. Shared by the file-dialog and
    /// installed-PDK-dropdown load paths.
    /// </summary>
    private void LoadPdkFromPath(string path)
    {
        // Use the editing-tolerant loader — this window's whole purpose
        // is to calibrate components whose offsets are still null. The
        // strict LoadFromFile path would reject exactly those PDKs.
        _loadedPdk = _pdkLoader.LoadFromFileForEditing(path);
        _loadedFilePath = path;
        HasUnsavedChanges = false;

        Components.Clear();
        foreach (var comp in _loadedPdk.Components)
            Components.Add(new PdkComponentOffsetItemViewModel(comp, _loadedPdk.Name));

        var missing = Components.Count(c => c.Status == OffsetStatus.Missing);
        var zero   = Components.Count(c => c.Status == OffsetStatus.ZeroOffset);
        StatusText = string.Format(
            LocalizationService.Instance.Translate("PdkOffset.Status.LoadedPdk"),
            _loadedPdk.Name, Components.Count, missing, zero);
        SelectedComponent = null;
        // Components.Count is not an ObservableProperty — manually nudge so the
        // batch buttons re-evaluate CanExecute now that the list has rows.
        CheckAllCommand.NotifyCanExecuteChanged();
        TryFixAllCommand.NotifyCanExecuteChanged();
        BatchCheckResults.Clear();
        BatchSummary = "";
    }

    private void RefreshPinPositions(PdkComponentDraft draft)
    {
        PinPositions.Clear();
        foreach (var pin in draft.Pins)
        {
            PinPositions.Add(new PinPositionViewModel(
                pin.Name,
                pin.OffsetXMicrometers,
                pin.OffsetYMicrometers,
                OffsetX,
                OffsetY));
        }
    }

}

/// <summary>
/// Per-pin alignment record produced by
/// <see cref="PdkOffsetEditorViewModel.ComputePinAlignment"/>.
/// </summary>
public record PinAlignmentInfo(
    string LunimaPinName,
    string NearestNazcaPinName,
    double DeltaXMicrometers,
    double DeltaYMicrometers,
    double DistanceMicrometers,
    bool IsAligned)
{
    /// <summary>Display-only label — '✓ aligned' or '✗ off' for the per-pin row.</summary>
    public string AlignedLabel => LocalizationService.Instance.Translate(
        IsAligned ? "PdkOffset.AlignedLabel.Aligned" : "PdkOffset.AlignedLabel.Off");
}
