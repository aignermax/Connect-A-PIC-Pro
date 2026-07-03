using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;

namespace CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;

/// <summary>
/// ViewModel for the per-instance editable Nazca code editor (issue #556/#561).
/// Lets a power user see, edit and run a complete Nazca cell function for one
/// placed instance, preview the resulting geometry live, and — on Apply —
/// recompute the component's bounding-box size AND update its physical pins from
/// the rendered geometry.
/// </summary>
/// <remarks>
/// On Apply: both <see cref="Component.WidthMicrometers"/> /
/// <see cref="Component.HeightMicrometers"/> and
/// <see cref="Component.PhysicalPins"/> are replaced from the preview result.
/// When the new pin names differ from the PDK template's,
/// <see cref="HasNoSimulationModel"/> is set to warn the user that the
/// template S-matrix is no longer valid for simulation.
/// </remarks>
public partial class InstanceNazcaCodeEditorViewModel : ObservableObject
{
    private readonly Dictionary<string, NazcaCodeOverride> _storedOverrides;
    private readonly Component? _liveComponent;
    private readonly string _componentKey;
    private readonly NazcaComponentPreviewService? _previewService;
    private readonly GdsFactoryComponentPreviewService? _gdsFactoryPreviewService;
    private readonly Func<double, double, IReadOnlyList<string>>? _overlapCheck;
    private readonly Action? _onDimensionsChanged;
    private readonly Action? _onChanged;
    private readonly Action<IReadOnlyList<PhysicalPin>>? _onPinsChanged;
    private readonly string _templateCode;
    private readonly string? _moduleName;
    private readonly string _nazcaFunction;
    private readonly string? _nazcaParameters;

    private NazcaPreviewResult? _lastSuccessfulPreview;

    /// <summary>
    /// The original source the editor was seeded with via module-mode (the component's
    /// real source / note / fallback). Null when the editor was seeded from a stored
    /// raw-code override. Used to decide whether "Run Preview" renders the unchanged
    /// component via module mode (works for demo PDK and SiEPIC PCells alike) or runs
    /// the user's edited code via raw-code mode.
    /// </summary>
    private string? _originalSourceCode;

    /// <summary>The editable override code (your own self-contained Nazca cell).</summary>
    [ObservableProperty]
    private string _code = string.Empty;

    /// <summary>
    /// The component's original PDK source, shown READ-ONLY for reference. Real Python
    /// source for demo cells / SiEPIC PCells, or a "# ..." note when none is available.
    /// </summary>
    [ObservableProperty]
    private string _originalSource = string.Empty;

    /// <summary>
    /// True when <see cref="Code"/> has been edited away from the seeded original (or was
    /// seeded from a stored override) — i.e. it is the user's own runnable code, eligible
    /// to be run as raw code and persisted via Apply. False while it is the unchanged
    /// original (which is rendered via module mode and is not itself a persistable override).
    /// </summary>
    private bool IsCustomCode =>
        _originalSourceCode == null
        || !string.Equals((Code ?? string.Empty).Trim(), _originalSourceCode.Trim(), StringComparison.Ordinal);

    /// <summary>Editing the code invalidates the last run and re-evaluates Apply.</summary>
    partial void OnCodeChanged(string value)
    {
        IsValid = false;
        ApplyOverrideCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Error text from the last failed run; empty when the last run succeeded.</summary>
    [ObservableProperty]
    private string _previewError = string.Empty;

    /// <summary>True after a successful run; gates the Apply command.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyOverrideCommand))]
    private bool _isValid;

    /// <summary>True while a preview run is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyOverrideCommand))]
    private bool _isRunning;

    /// <summary>Free-form status / hint message shown beneath the editor.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>True when a raw-code override is currently stored for this component.</summary>
    [ObservableProperty]
    private bool _hasOverride;

    /// <summary>
    /// True when the most recent Apply produced a size that overlaps one or more
    /// neighbouring components. Non-blocking — the override is still applied.
    /// </summary>
    [ObservableProperty]
    private bool _hasOverlap;

    /// <summary>
    /// True when the most recently applied override changed the pin names relative
    /// to the PDK template, meaning the template S-matrix no longer maps to the
    /// new ports. Surfaced in the status text as a prominent warning.
    /// </summary>
    [ObservableProperty]
    private bool _hasNoSimulationModel;

    /// <summary>
    /// True when this component exposes editable Nazca source (demo PDK cell body
    /// via <c>inspect.getsource</c>, or a SiEPIC PCell Python file). False when the
    /// component is a fixed-cell GDS / KLayout PCell whose source can't be retrieved,
    /// or when source introspection failed. When false the editor still shows the
    /// rendered geometry and the user can paste their own Nazca code to override it.
    /// </summary>
    [ObservableProperty]
    private bool _hasEditableSource = true;

    /// <summary>
    /// The last successful preview's geometry. Null until a run succeeds.
    /// </summary>
    public NazcaPreviewResult? PreviewData => _lastSuccessfulPreview;

    /// <summary>
    /// Rasterised polygon preview of the last successful run, bound to the editor's
    /// preview Image. Null when there is no successful render (or no polygons —
    /// e.g. gdstk/gdspy not installed; the status text reports that case).
    /// </summary>
    [ObservableProperty]
    private Bitmap? _previewBitmap;

    /// <summary>
    /// Initializes a new instance of <see cref="InstanceNazcaCodeEditorViewModel"/>.
    /// </summary>
    /// <param name="componentKey">The component's Identifier, used as the override-store key.</param>
    /// <param name="storedOverrides">Shared per-instance Nazca override dictionary.</param>
    /// <param name="liveComponent">The live canvas component whose size is recomputed on Apply.</param>
    /// <param name="moduleName">PDK module name passed to <see cref="NazcaComponentPreviewService.RenderAsync"/> in module mode.</param>
    /// <param name="nazcaFunction">The component's Nazca function / cell name.</param>
    /// <param name="nazcaParameters">Optional keyword-argument string for the function, or null.</param>
    /// <param name="templateCode">Runnable code fallback; used as last-resort seed when no real source is available.</param>
    /// <param name="previewService">Nazca preview back-end. Null disables Run (e.g. headless tests).</param>
    /// <param name="gdsFactoryPreviewService">gdsfactory preview back-end (issue #637). Null disables Run in gdsfactory mode.</param>
    /// <param name="overlapCheck">Optional callback returning names of components the resized instance would overlap.</param>
    /// <param name="onDimensionsChanged">Invoked after the live component's size changes so the canvas can repaint.</param>
    /// <param name="onChanged">Invoked after every successful Apply or Reset so observers refresh badges.</param>
    /// <param name="onPinsChanged">Invoked after the live component's physical pins are replaced (on Apply or Reset).</param>
    public InstanceNazcaCodeEditorViewModel(
        string componentKey,
        Dictionary<string, NazcaCodeOverride> storedOverrides,
        Component? liveComponent,
        string? moduleName,
        string nazcaFunction,
        string? nazcaParameters,
        string templateCode,
        NazcaComponentPreviewService? previewService = null,
        GdsFactoryComponentPreviewService? gdsFactoryPreviewService = null,
        Func<double, double, IReadOnlyList<string>>? overlapCheck = null,
        Action? onDimensionsChanged = null,
        Action? onChanged = null,
        Action<IReadOnlyList<PhysicalPin>>? onPinsChanged = null)
    {
        _componentKey = componentKey;
        _storedOverrides = storedOverrides;
        _liveComponent = liveComponent;
        _moduleName = moduleName;
        _nazcaFunction = nazcaFunction ?? string.Empty;
        _nazcaParameters = nazcaParameters;
        _templateCode = templateCode ?? string.Empty;
        _previewService = previewService;
        _gdsFactoryPreviewService = gdsFactoryPreviewService;
        _overlapCheck = overlapCheck;
        _onDimensionsChanged = onDimensionsChanged;
        _onChanged = onChanged;
        _onPinsChanged = onPinsChanged;

        RefreshFromStore();
    }

    /// <summary>
    /// Loads the component's REAL Nazca source and an initial geometry preview via
    /// module-mode <see cref="NazcaComponentPreviewService.RenderAsync"/>, UNLESS a
    /// raw-code override is already stored. Crash-proof: never throws.
    /// </summary>
    public async Task InitializeAsync()
    {
        // A stored raw-code override wins — the constructor already seeded Code from it.
        // Leave _originalSourceCode null so Run treats the stored code as custom (raw-code).
        if (_storedOverrides.TryGetValue(_componentKey, out var stored) && stored.RawCode != null)
            return;

        await LoadOriginalSourceAsync();
        _originalSourceCode = Code;
        ApplyOverrideCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fetches the component's original source + geometry via module-mode render. Never throws.</summary>
    private async Task LoadOriginalSourceAsync()
    {
        // The editable box is always the override starter; the original source (if any)
        // is shown read-only for reference.
        Code = OverrideStub;

        if (_previewService == null)
        {
            // No back-end (e.g. headless): nothing to fetch.
            OriginalSource = string.Empty;
            HasEditableSource = false;
            return;
        }

        try
        {
            var result = await _previewService.RenderAsync(_moduleName, _nazcaFunction, _nazcaParameters);
            if (!result.Success)
            {
                OriginalSource = string.Empty;
                HasEditableSource = false;
                PreviewBitmap = null;
                _lastSuccessfulPreview = null;
                OnPropertyChanged(nameof(PreviewData));
                PreviewError = result.Error ?? "Could not render this component.";
                StatusText = "Could not render this component — paste your own Nazca code to override.";
                return;
            }

            // Render succeeded — show the geometry + the original source (read-only).
            _lastSuccessfulPreview = result;
            OnPropertyChanged(nameof(PreviewData));
            PreviewBitmap = PreviewBitmapFactory.FromResult(result);
            PreviewError = string.Empty;

            double w = result.XMax - result.XMin;
            double h = result.YMax - result.YMin;

            // HasEditableSource here means "real source is available to show as reference".
            HasEditableSource = IsRealSource(result.Source);
            OriginalSource = result.Source ?? string.Empty;
            StatusText = HasEditableSource
                ? $"Loaded — size {w:F2} × {h:F2} µm. Original source shown below (read-only)."
                : $"Loaded geometry — size {w:F2} × {h:F2} µm. No editable source; paste your own code to override.";
        }
        catch (Exception ex)
        {
            // InitializeAsync must never bring the dialog down.
            OriginalSource = string.Empty;
            HasEditableSource = false;
            PreviewBitmap = null;
            _lastSuccessfulPreview = null;
            OnPropertyChanged(nameof(PreviewData));
            PreviewError = ex.Message;
            StatusText = "Could not render this component — paste your own Nazca code to override.";
        }
    }

    /// <summary>
    /// True when <paramref name="source"/> is genuine Python source rather than a
    /// note. The preview script returns a "# ..."-prefixed comment (e.g.
    /// "# Source unavailable …") when no source can be retrieved.
    /// </summary>
    private static bool IsRealSource(string? source)
        => !string.IsNullOrWhiteSpace(source) && !source.TrimStart().StartsWith('#');

    /// <summary>
    /// Runs the editor's code through the preview service. Any failure sets
    /// <see cref="PreviewError"/> and leaves <see cref="IsValid"/> false. Never throws.
    /// </summary>
    [RelayCommand]
    private async Task RunPreviewAsync()
    {
        var service = IsGdsFactoryBackend
            ? (NazcaComponentPreviewService?)_gdsFactoryPreviewService
            : _previewService;
        if (service == null)
        {
            PreviewError = IsGdsFactoryBackend
                ? "gdsfactory preview service unavailable."
                : "Preview service unavailable.";
            IsValid = false;
            return;
        }

        IsRunning = true;
        StatusText = "Running preview…";
        try
        {
            // Nazca + unedited original → render the real component via module mode
            // (handles demo PDK and SiEPIC PCells, whose source is not standalone-
            // runnable). Otherwise run the user's own self-contained snippet via
            // raw-code mode; gdsfactory has no module-mode render of PDK cells.
            var result = !IsGdsFactoryBackend && !IsCustomCode
                ? await service.RenderAsync(_moduleName, _nazcaFunction, _nazcaParameters)
                : await service.RenderRawCodeAsync(Code);
            if (result.Success)
            {
                _lastSuccessfulPreview = result;
                OnPropertyChanged(nameof(PreviewData));
                PreviewBitmap = PreviewBitmapFactory.FromResult(result);
                PreviewError = string.Empty;
                IsValid = true;
                StatusText = BuildSuccessStatus(result);
            }
            else
            {
                _lastSuccessfulPreview = null;
                OnPropertyChanged(nameof(PreviewData));
                PreviewBitmap = null;
                PreviewError = result.Error ?? "Unknown error.";
                IsValid = false;
                StatusText = "Preview failed — see error above.";
            }
        }
        catch (Exception ex)
        {
            // Defensive: the service is designed never to throw, but a Run command
            // must never bring the dialog down regardless.
            _lastSuccessfulPreview = null;
            OnPropertyChanged(nameof(PreviewData));
            PreviewBitmap = null;
            PreviewError = ex.Message;
            IsValid = false;
            StatusText = "Preview failed — see error above.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private string BuildSuccessStatus(NazcaPreviewResult result)
    {
        double w = result.XMax - result.XMin;
        double h = result.YMax - result.YMin;
        var status = $"Preview OK — size {w:F2} × {h:F2} µm, {result.Pins.Count} port(s).";

        if (!string.IsNullOrEmpty(result.PolygonWarning))
            status += " " + result.PolygonWarning;

        return status;
    }

    /// <summary>
    /// Persists the edited code as a raw-code override, recomputes the live
    /// component's size and physical pins from the last successful preview's
    /// bounding box and pin list, runs a (non-blocking) overlap check, and
    /// fires the change callbacks. Enabled only after a successful
    /// <see cref="RunPreviewAsync"/>.
    /// When the new pin names differ from the PDK template's,
    /// <see cref="HasNoSimulationModel"/> is set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyOverride))]
    private void ApplyOverride()
    {
        if (_lastSuccessfulPreview == null)
            return;

        double width = _lastSuccessfulPreview.XMax - _lastSuccessfulPreview.XMin;
        double height = _lastSuccessfulPreview.YMax - _lastSuccessfulPreview.YMin;

        var overrideData = _storedOverrides.TryGetValue(_componentKey, out var existing)
            ? existing
            : new NazcaCodeOverride();

        // Capture template pins on first Apply (before any override pin has been written).
        if (overrideData.TemplatePins == null && _liveComponent != null)
            overrideData.TemplatePins = OverridePinMapper.CaptureAsPinData(_liveComponent.PhysicalPins);

        // Derive override pins from the preview pin stubs.
        var overridePins = OverridePinMapper.BuildOverridePins(_lastSuccessfulPreview);
        overrideData.RawCode = Code;
        // Record which backend the code was written for so the matching
        // export/preview honors it (issue #637). Null keeps the Nazca default
        // and existing overrides byte-identical.
        overrideData.Backend = IsGdsFactoryBackend ? OverrideBackend.GdsFactory : null;
        overrideData.SetOverrideGeometry(
            width, height, _lastSuccessfulPreview.XMin, _lastSuccessfulPreview.YMax);
        overrideData.OverridePins = overridePins;
        overrideData.HasNoSimulationModel = !OverridePinMapper.PinNamesMatch(overrideData.TemplatePins, overridePins);
        _storedOverrides[_componentKey] = overrideData;

        if (_liveComponent != null)
        {
            _liveComponent.WidthMicrometers = width;
            _liveComponent.HeightMicrometers = height;
            OverridePinMapper.ApplyPinsToComponent(_liveComponent, overridePins);
        }
        _onDimensionsChanged?.Invoke();
        if (_liveComponent != null)
            _onPinsChanged?.Invoke(_liveComponent.PhysicalPins);

        var overlapping = _overlapCheck?.Invoke(width, height) ?? Array.Empty<string>();
        HasOverlap = overlapping.Count > 0;
        HasNoSimulationModel = overrideData.HasNoSimulationModel;
        HasOverride = true;

        var baseStatus = HasOverlap
            ? $"Applied — geometry {width:F2} × {height:F2} µm, {overridePins.Count} pin(s). " +
              $"Warning: overlaps {string.Join(", ", overlapping)}."
            : $"Applied — geometry {width:F2} × {height:F2} µm, {overridePins.Count} pin(s).";

        StatusText = HasNoSimulationModel
            ? baseStatus + " \u26a0 No simulation model: pin names differ from template — " +
              "import an S-matrix via the S-matrix tab to restore simulation."
            : baseStatus;

        _onChanged?.Invoke();
    }

    // Apply only persists genuinely custom code — the unchanged original is the PDK
    // default, not an override (and its source may not be standalone-runnable on reload).
    private bool CanApplyOverride() => IsValid && !IsRunning && IsCustomCode;

    /// <summary>
    /// Clears the raw-code override for this instance and restores the editor to the
    /// component's ORIGINAL Nazca source (re-fetched via module-mode render), not a
    /// synthesized template. The live component's physical pins are restored to the
    /// PDK template snapshot captured on the first Apply (if available). Size is left
    /// as-is. Crash-proof — never throws.
    /// </summary>
    [RelayCommand]
    private async Task ResetToTemplate()
    {
        List<OverridePinData>? templatePinsToRestore = null;
        if (_storedOverrides.TryGetValue(_componentKey, out var existing))
        {
            templatePinsToRestore = existing.TemplatePins;
            existing.ClearRawCodeOverride();
            // Drop the whole record only if no parameter-override fields remain.
            if (existing.FunctionName == null && existing.FunctionParameters == null
                && existing.ModuleName == null)
                _storedOverrides.Remove(_componentKey);
        }

        if (_liveComponent != null && templatePinsToRestore?.Count > 0)
        {
            OverridePinMapper.ApplyPinsToComponent(_liveComponent, templatePinsToRestore);
            _onPinsChanged?.Invoke(_liveComponent.PhysicalPins);
        }
        _lastSuccessfulPreview = null;
        OnPropertyChanged(nameof(PreviewData));
        PreviewBitmap = null;
        PreviewError = string.Empty;
        IsValid = false;
        HasOverlap = false;
        HasOverride = false;
        HasNoSimulationModel = false;
        // The PDK template is Nazca-defined — reset returns the editor to the default backend.
        IsGdsFactoryBackend = false;
        await LoadOriginalSourceAsync();
        _originalSourceCode = Code;
        bool hasPins = templatePinsToRestore?.Count > 0;
        bool linkLost = hasPins && _liveComponent != null
            && _liveComponent.PhysicalPins.Any(p => p.LogicalPin == null);
        StatusText = !hasPins
            ? "Reset to original source. Run a preview before applying."
            : linkLost
                ? "Reset to original source — template pins restored. Reload the project to restore the simulation link."
                : "Reset to original source — template pins restored. Run a preview before applying.";
        _onChanged?.Invoke();
    }

    private void RefreshFromStore()
    {
        if (_storedOverrides.TryGetValue(_componentKey, out var stored) && stored.RawCode != null)
        {
            // A stored override is always editable code the user authored/saved.
            // Seed the backend toggle from the record so preview/apply keep
            // targeting the backend the code was written for (issue #637).
            IsGdsFactoryBackend = OverrideBackend.IsGdsFactory(stored.Backend);
            Code = stored.RawCode;
            HasOverride = true;
            HasEditableSource = true;
            HasNoSimulationModel = stored.HasNoSimulationModel;
        }
        else
        {
            // Seed with the runnable fallback until InitializeAsync fetches the real source.
            Code = _templateCode;
            HasOverride = false;
        }
    }

}
