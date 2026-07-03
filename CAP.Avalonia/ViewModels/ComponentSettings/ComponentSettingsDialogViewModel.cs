using System.Collections.ObjectModel;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Import;
using CAP_DataAccess.Persistence.PIR;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Notifications;
using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NazcaCodeOverride = CAP_DataAccess.Persistence.PIR.NazcaCodeOverride;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// ViewModel for the Component Settings dialog.
/// Displays per-wavelength S-matrices for a component (PDK template or canvas instance),
/// and allows importing new S-matrices from Lumerical / Touchstone files.
/// </summary>
public partial class ComponentSettingsDialogViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialogService;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly IReadOnlyList<ISParameterImporter> _importers;
    private readonly IPortMappingDialogService? _portMappingDialog;
    private readonly INotificationService? _notificationService;

    private Dictionary<string, ComponentSMatrixData>? _storedSMatrices;
    private Component? _liveComponent;
    private string _smatrixKey = string.Empty;
    private Func<string>? _smatrixKeyResolver;
    private string _displayName = string.Empty;
    private Action? _onChanged;
    private bool _isUserGlobalScope;
    private Dictionary<int, SMatrix>? _effectiveSMatrices;
    private IReadOnlyList<Pin>? _effectivePins;
    private IReadOnlyList<string>? _availablePinNames;

    /// <summary>
    /// ViewModel for the per-instance Nazca parameter override section.
    /// Null when the dialog is opened in per-template (PDK library) mode, or when
    /// <see cref="Configure"/> was called without a <c>storedNazcaOverrides</c> dictionary.
    /// </summary>
    public InstanceNazcaOverrideViewModel? NazcaOverride { get; private set; }

    /// <summary>
    /// ViewModel for the per-instance editable Nazca code editor section (issue #556).
    /// Null in per-template mode or when no preview service / template code was supplied.
    /// </summary>
    public InstanceNazcaCodeEditorViewModel? NazcaCodeEditor { get; private set; }

    /// <summary>Dialog window title including the component name.</summary>
    [ObservableProperty]
    private string _title = "Component Settings";

    /// <summary>Status message shown after import attempts.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>True while an import is running.</summary>
    [ObservableProperty]
    private bool _isImporting;

    /// <summary>True when at least one wavelength entry is present.</summary>
    [ObservableProperty]
    private bool _hasSMatrices;

    /// <summary>True when the "Currently effective S-matrix" section has rows.</summary>
    [ObservableProperty]
    private bool _hasEffectiveEntries;

    /// <summary>Per-wavelength S-matrix entries shown in the dialog list.</summary>
    public ObservableCollection<SMatrixEntryViewModel> SMatrixEntries { get; } = new();

    /// <summary>
    /// Read-only "currently effective" S-matrix entries — what the simulator
    /// will use for each wavelength right now. Combines the PDK default with
    /// any active override and tags each row accordingly so the user can see
    /// the source without cross-referencing <see cref="SMatrixEntries"/>.
    /// </summary>
    public ObservableCollection<EffectiveSMatrixEntryViewModel> EffectiveEntries { get; } = new();

    /// <summary>
    /// Initialises a new instance with constructor-injected dependencies.
    /// </summary>
    /// <param name="fileDialogService">Service used to open the file picker for imports.</param>
    /// <param name="errorConsole">Optional error console for surfacing import failures and partial overrides.</param>
    /// <param name="importers">Optional importer set; defaults to Lumerical + Touchstone.</param>
    /// <param name="portMappingDialog">
    /// Optional dialog service used when imported port names don't match the
    /// component's pin names. Required for production use; tests can pass null
    /// to skip the interactive step (which causes the import to abort with a
    /// status-text explanation when names mismatch).
    /// </param>
    /// <param name="fdtdService">
    /// Optional FDTD solver used by the "Recalculate S-matrix" command. When null
    /// (e.g. in tests, or when no solver is configured) the command is disabled.
    /// </param>
    /// <param name="fdtdRequestFactory">
    /// Optional factory that turns the live component into an FDTD request
    /// (renders its geometry and ports). Required alongside <paramref name="fdtdService"/>
    /// for recompute to be available.
    /// </param>
    /// <param name="notificationService">
    /// Optional toast service for transient, non-error feedback (e.g. "FDTD
    /// recompute cancelled") that outlives the dialog window. When null, the
    /// feedback stays in the in-dialog status text only.
    /// </param>
    public ComponentSettingsDialogViewModel(
        IFileDialogService fileDialogService,
        ErrorConsoleService? errorConsole = null,
        IReadOnlyList<ISParameterImporter>? importers = null,
        IPortMappingDialogService? portMappingDialog = null,
        IFdtdSMatrixService? fdtdService = null,
        Func<Component, CancellationToken, Task<FdtdSMatrixRequest?>>? fdtdRequestFactory = null,
        INotificationService? notificationService = null)
    {
        _fileDialogService = fileDialogService;
        _errorConsole = errorConsole;
        _portMappingDialog = portMappingDialog;
        _notificationService = notificationService;
        _fdtdService = fdtdService;
        _fdtdRequestFactory = fdtdRequestFactory;
        _importers = importers ?? new ISParameterImporter[]
        {
            new LumericalSParameterImporter(),
            new TouchstoneImporter()
        };
    }

    /// <summary>
    /// Configures the dialog for a specific entity (PDK template or canvas instance).
    /// </summary>
    /// <param name="entityKey">
    /// Per-instance key used for the Nazca raw-code override side
    /// (<see cref="InstanceNazcaCodeEditorViewModel"/> / <c>storedNazcaOverrides</c>).
    /// For canvas instances this is <c>component.Identifier</c>;
    /// for PDK templates it is <c>"pdkSource::templateName"</c>.
    /// </param>
    /// <param name="smatrixKey">
    /// Key used to store and retrieve S-matrix data in <paramref name="storedSMatrices"/>.
    /// For canvas instances this is the geometry identity (so a copy inherits a
    /// recomputed/imported S-matrix); for PDK templates it is <c>"pdkSource::templateName"</c>.
    /// </param>
    /// <param name="displayName">Human-readable name shown in the title bar.</param>
    /// <param name="storedSMatrices">Shared dictionary from <c>FileOperationsViewModel</c>.</param>
    /// <param name="liveComponent">
    /// Optional live component instance. When set, imported S-matrices are applied
    /// immediately to the component's <see cref="Component.WaveLengthToSMatrixMap"/>
    /// so the next simulation run picks them up without reloading the design.
    /// </param>
    /// <param name="onChanged">
    /// Optional callback invoked after every successful import or delete so observers
    /// (e.g. the hierarchy panel) can refresh derived state such as override badges.
    /// </param>
    /// <param name="isUserGlobalScope">
    /// When true, the dialog title flags that the override applies to all projects
    /// — used when <paramref name="storedSMatrices"/> is the user-global store
    /// rather than the project's <c>.lun</c>-backed store. Purely a UX hint;
    /// persistence behaviour is determined by the store the caller passes in.
    /// </param>
    /// <param name="effectiveSMatrices">
    /// Optional per-wavelength S-matrix map representing what the simulator
    /// actually uses for this entity (PDK default merged with any active
    /// override). When supplied alongside <paramref name="effectivePins"/>,
    /// the dialog renders a read-only "Currently effective" section so the
    /// user can see the source of truth without inferring it from the
    /// override list.
    /// </param>
    /// <param name="effectivePins">
    /// Pin order matching <paramref name="effectiveSMatrices"/>'s S-matrix
    /// indexing. Required to read diagonal magnitudes from the SMatrix
    /// (which is keyed by <see cref="Pin.IDInFlow"/> / <see cref="Pin.IDOutFlow"/>).
    /// </param>
    /// <param name="storedNazcaOverrides">
    /// Optional per-instance Nazca override dictionary. When non-null and
    /// <paramref name="liveComponent"/> is also non-null, the dialog adds a
    /// Nazca parameter override section backed by an
    /// <see cref="InstanceNazcaOverrideViewModel"/>. Null in per-template mode
    /// (PDK library) where Nazca overrides are not supported.
    /// </param>
    /// <param name="templateFunctionName">
    /// Original PDK template function name. Used to seed the editable field
    /// before the user has saved any override, and as the target for "Reset to
    /// template". Pass null to skip the Nazca override section.
    /// </param>
    /// <param name="templateFunctionParameters">
    /// Original PDK template function parameters string. See <paramref name="templateFunctionName"/>.
    /// </param>
    /// <param name="templateModuleName">
    /// Original PDK template module name, or null if not set.
    /// </param>
    /// <param name="smatrixKeyResolver">
    /// Optional resolver that recomputes <paramref name="smatrixKey"/> from the live
    /// component's current geometry identity. Invoked when a Nazca geometry override is
    /// applied or reset (which changes the identity), so the S-matrix entry list stays in
    /// sync without reopening the dialog. Pass the same expression that produced
    /// <paramref name="smatrixKey"/>; null disables re-resolution (per-template mode).
    /// </param>
    public void Configure(
        string entityKey,
        string smatrixKey,
        string displayName,
        Dictionary<string, ComponentSMatrixData> storedSMatrices,
        Component? liveComponent = null,
        Action? onChanged = null,
        bool isUserGlobalScope = false,
        Dictionary<int, SMatrix>? effectiveSMatrices = null,
        IReadOnlyList<Pin>? effectivePins = null,
        IReadOnlyList<string>? availablePinNames = null,
        Dictionary<string, NazcaCodeOverride>? storedNazcaOverrides = null,
        string? templateFunctionName = null,
        string? templateFunctionParameters = null,
        string? templateModuleName = null,
        NazcaComponentPreviewService? nazcaPreviewService = null,
        string? nazcaTemplateCode = null,
        Func<double, double, IReadOnlyList<string>>? nazcaOverlapCheck = null,
        Action? nazcaDimensionsChanged = null,
        Action<IReadOnlyList<PhysicalPin>>? nazcaPinsChanged = null,
        Func<string>? smatrixKeyResolver = null)
    {
        _smatrixKey = smatrixKey;
        _smatrixKeyResolver = smatrixKeyResolver;
        _displayName = displayName;
        _storedSMatrices = storedSMatrices;
        _liveComponent = liveComponent;
        _onChanged = onChanged;
        _isUserGlobalScope = isUserGlobalScope;
        _effectiveSMatrices = effectiveSMatrices;
        _effectivePins = effectivePins;
        _availablePinNames = availablePinNames;
        Title = isUserGlobalScope
            ? $"Component Settings: {displayName} (applies to all projects)"
            : $"Component Settings: {displayName}";
        StatusText = string.Empty;

        // Only create the Nazca override VM for per-instance mode.
        // The Nazca VMs report through OnNazcaGeometryChanged (not the raw onChanged) so the
        // dialog re-resolves the S-matrix key first: a geometry override changes the component's
        // geometry identity, hence the key its S-matrix override is stored under.
        if (liveComponent != null && storedNazcaOverrides != null && templateFunctionName != null)
        {
            NazcaOverride = new InstanceNazcaOverrideViewModel(
                entityKey,
                storedNazcaOverrides,
                liveComponent,
                templateFunctionName,
                templateFunctionParameters ?? string.Empty,
                templateModuleName,
                OnNazcaGeometryChanged);
        }
        else
        {
            NazcaOverride = null;
        }
        OnPropertyChanged(nameof(NazcaOverride));

        // Per-instance raw Nazca code editor (issue #556). Only in per-instance mode
        // with a live component, an override store and a runnable seed template.
        if (liveComponent != null && storedNazcaOverrides != null && nazcaTemplateCode != null)
        {
            NazcaCodeEditor = new InstanceNazcaCodeEditorViewModel(
                entityKey,
                storedNazcaOverrides,
                liveComponent,
                templateModuleName,
                templateFunctionName ?? string.Empty,
                templateFunctionParameters,
                nazcaTemplateCode,
                nazcaPreviewService,
                nazcaOverlapCheck,
                nazcaDimensionsChanged,
                OnNazcaGeometryChanged,
                nazcaPinsChanged);
        }
        else
        {
            NazcaCodeEditor = null;
        }
        OnPropertyChanged(nameof(NazcaCodeEditor));

        SolverStatus = string.Empty;
        RefreshEntries(notifyChanged: false);
        RefreshEffectiveEntries();
        OnPropertyChanged(nameof(CanRecalculate));
        RecalculateSMatrixCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Invoked when a Nazca geometry override is applied or reset. The override changes the
    /// component's geometry identity, so its S-matrix override is now keyed differently —
    /// re-resolve the key and rebuild the entry lists so the dialog reflects the new geometry
    /// without needing to be closed and reopened. Then notify external observers.
    /// </summary>
    private void OnNazcaGeometryChanged()
    {
        if (_smatrixKeyResolver != null)
            _smatrixKey = _smatrixKeyResolver();
        RefreshEntries(notifyChanged: true);
    }

    /// <summary>
    /// Opens a file dialog and imports an S-matrix from a Lumerical or Touchstone file.
    /// The imported data is stored under the entity key and, if a live component is
    /// configured, applied immediately to its wavelength map.
    /// </summary>
    [RelayCommand]
    private async Task LoadFromFile()
    {
        if (_storedSMatrices == null)
            return;

        var path = await _fileDialogService.ShowOpenFileDialogAsync(
            "Select S-Parameter File",
            "S-Parameter Files|*.sparam;*.dat;*.txt;*.s1p;*.s2p;*.s3p;*.s4p;*.sNp|All Files|*.*");

        if (path == null)
            return;

        var importer = FindImporter(path);
        if (importer == null)
        {
            StatusText = $"Unsupported file type: {Path.GetExtension(path)}";
            return;
        }

        IsImporting = true;
        StatusText = "Importing…";

        try
        {
            var imported = await importer.ImportAsync(path);

            // Reconcile imported port names with the component's pin names.
            // If they don't already align and we have both the available pin
            // names and a mapping-dialog service, ask the user up-front. This
            // is much friendlier than letting SMatrixOverrideApplicator skip
            // every wavelength later with a "port name X not found" warning.
            var resolved = await ReconcilePortNamesAsync(imported);
            if (resolved == null)
                return; // user cancelled — StatusText already set

            var smatrixData = SParameterConverter.ToComponentSMatrixData(resolved);
            _storedSMatrices[_smatrixKey] = smatrixData;

            ApplyResult? applyResult = null;
            if (_liveComponent != null)
                applyResult = SMatrixOverrideApplicator.Apply(_liveComponent, smatrixData, _errorConsole);

            StatusText = BuildImportStatus(path, resolved, applyResult);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"S-parameter import failed for '{path}'", ex);
            StatusText = $"Import failed: {ex.Message}" + (_errorConsole != null ? " (see Error Console)" : "");
        }
        finally
        {
            IsImporting = false;
            RefreshEntries(notifyChanged: true);
        }
    }

    /// <summary>
    /// Returns <paramref name="imported"/> unchanged when port names already
    /// align, the result of <see cref="PortNameMapping.Remap"/> with a
    /// user-supplied mapping when they don't, or <c>null</c> when the user
    /// cancelled the mapping dialog (in which case <see cref="StatusText"/>
    /// is set so the caller can return without storing anything).
    /// </summary>
    private async Task<ImportedSParameters?> ReconcilePortNamesAsync(ImportedSParameters imported)
    {
        if (_availablePinNames == null || _availablePinNames.Count == 0)
            return imported; // caller didn't tell us the pin names — proceed and let Apply complain if anything's wrong

        if (PortNameMapping.NamesAlignWithComponent(imported.PortNames, _availablePinNames))
            return imported;

        if (imported.PortNames.Count != _availablePinNames.Count)
        {
            // Different port counts is structurally unmappable — bail out
            // loudly rather than open a dialog the user couldn't satisfy.
            StatusText = $"Cannot import: file has {imported.PortNames.Count} port(s), " +
                         $"but '{_displayName}' has {_availablePinNames.Count} pin(s).";
            return null;
        }

        if (_portMappingDialog == null)
        {
            // No interactive surface available (typically test or headless).
            StatusText = $"Imported port names don't match component pins on '{_displayName}'. " +
                         $"Re-run with a port-mapping dialog wired up to resolve this interactively.";
            return null;
        }

        var mapping = await _portMappingDialog.ShowAsync(_displayName, imported.PortNames, _availablePinNames);
        if (mapping == null)
        {
            StatusText = "Import cancelled — no port mapping was confirmed.";
            return null;
        }

        return PortNameMapping.Remap(imported, mapping);
    }

    private static string BuildImportStatus(
        string path,
        ImportedSParameters imported,
        ApplyResult? applyResult)
    {
        var fileName = Path.GetFileName(path);
        var portInfo = $"{imported.PortCount} ports, {imported.SMatricesByWavelengthNm.Count} wavelengths";

        if (applyResult == null)
            return $"Imported {portInfo} from '{fileName}'.";

        if (applyResult.IsTotalFailure)
            return $"Imported {portInfo} from '{fileName}', but no wavelength could be applied to the live component (see Error Console).";

        if (applyResult.IsPartial)
            return $"Imported {portInfo}; applied {applyResult.Applied} of {applyResult.Applied + applyResult.Skipped.Count} wavelength(s) — {applyResult.Skipped.Count} skipped (see Error Console).";

        var replacedNote = applyResult.Replaced > 0 ? $" ({applyResult.Replaced} replaced)" : "";
        return $"Imported {portInfo} from '{fileName}'; applied {applyResult.Applied} wavelength(s){replacedNote}.";
    }

    /// <summary>
    /// Removes a single wavelength entry from the stored S-matrix data and from the live
    /// component's wavelength map (if a live component is configured), so the next
    /// simulation run reflects the deletion immediately.
    /// </summary>
    [RelayCommand]
    private void DeleteEntry(SMatrixEntryViewModel entry)
    {
        if (_storedSMatrices == null || !_storedSMatrices.TryGetValue(_smatrixKey, out var data))
            return;

        data.Wavelengths.Remove(entry.WavelengthKey);
        if (data.Wavelengths.Count == 0)
            _storedSMatrices.Remove(_smatrixKey);

        if (_liveComponent != null && int.TryParse(entry.WavelengthKey, out int wavelengthNm))
            _liveComponent.WaveLengthToSMatrixMap.Remove(wavelengthNm);

        StatusText = $"Removed wavelength {entry.WavelengthKey} nm. Reload design to restore PDK default.";
        RefreshEntries(notifyChanged: true);
    }

    private ISParameterImporter? FindImporter(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".txt")
            return LooksLikeLumericalTxt(path) ? _importers.First(i => i is LumericalSParameterImporter) : null;
        return _importers.FirstOrDefault(i => i.SupportedExtensions.Contains(ext));
    }

    private bool LooksLikeLumericalTxt(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
                    continue;
                if (trimmed.StartsWith('('))
                    return true;
                var tokens = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return tokens.Length >= 9 &&
                       double.TryParse(tokens[0], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out _);
            }
        }
        catch (IOException ex)
        {
            _errorConsole?.LogWarning($"Could not probe '{path}' for Lumerical .txt format: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _errorConsole?.LogWarning($"Could not probe '{path}' for Lumerical .txt format: {ex.Message}");
        }
        return false;
    }
}
