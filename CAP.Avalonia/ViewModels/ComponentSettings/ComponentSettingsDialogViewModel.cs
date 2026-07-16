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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NazcaCodeOverride = CAP_DataAccess.Persistence.PIR.NazcaCodeOverride;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

public partial class ComponentSettingsDialogViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialogService;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly IReadOnlyList<ISParameterImporter> _importers;
    private readonly IPortMappingDialogService? _portMappingDialog;

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
    private Func<ComponentSMatrixData, bool>? _propagateToTemplate;

    public InstanceNazcaOverrideViewModel? NazcaOverride { get; private set; }

    public InstanceNazcaCodeEditorViewModel? NazcaCodeEditor { get; private set; }

    [ObservableProperty]
    private string _title = "Component Settings";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private bool _hasSMatrices;

    [ObservableProperty]
    private bool _hasEffectiveEntries;

    public ObservableCollection<SMatrixEntryViewModel> SMatrixEntries { get; } = new();

    public ObservableCollection<EffectiveSMatrixEntryViewModel> EffectiveEntries { get; } = new();

    public ComponentSettingsDialogViewModel(
        IFileDialogService fileDialogService,
        ErrorConsoleService? errorConsole = null,
        IReadOnlyList<ISParameterImporter>? importers = null,
        IPortMappingDialogService? portMappingDialog = null)
    {
        _fileDialogService = fileDialogService;
        _errorConsole = errorConsole;
        _portMappingDialog = portMappingDialog;
        _importers = importers ?? new ISParameterImporter[]
        {
            new LumericalSParameterImporter(),
            new TouchstoneImporter()
        };
    }

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
        Func<string>? smatrixKeyResolver = null,
        Func<ComponentSMatrixData, bool>? propagateToTemplate = null,
        NazcaComponentPreviewService? gdsFactoryPreviewService = null)
    {
        _smatrixKey = smatrixKey;
        _smatrixKeyResolver = smatrixKeyResolver;
        _propagateToTemplate = propagateToTemplate;
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
                nazcaPinsChanged,
                gdsFactoryPreviewService);
        }
        else
        {
            NazcaCodeEditor = null;
        }
        OnPropertyChanged(nameof(NazcaCodeEditor));

        RefreshEntries(notifyChanged: false);
        RefreshEffectiveEntries();
    }

    private void OnNazcaGeometryChanged()
    {
        if (_smatrixKeyResolver != null)
            _smatrixKey = _smatrixKeyResolver();
        RefreshEntries(notifyChanged: true);
    }

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

            var resolved = await ReconcilePortNamesAsync(imported);
            if (resolved == null)
                return;

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
}
