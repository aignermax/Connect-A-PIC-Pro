using System.Collections.ObjectModel;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Import;
using CAP_DataAccess.Persistence.PIR;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Services.Notifications;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

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
    private string _displayName = string.Empty;
    private Action? _onChanged;
    private bool _isUserGlobalScope;
    private Dictionary<int, SMatrix>? _effectiveSMatrices;
    private IReadOnlyList<Pin>? _effectivePins;
    private IReadOnlyList<string>? _availablePinNames;
    private Func<ComponentSMatrixData, bool>? _propagateToTemplate;

    [ObservableProperty]
    private string _title = LocalizationService.Instance.Translate("CompSettings.DefaultTitle");

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
        IPortMappingDialogService? portMappingDialog = null,
        IFdtdSMatrixService? fdtdService = null,
        Func<Component, CancellationToken, Task<FdtdSMatrixRequest?>>? fdtdRequestFactory = null,
        INotificationService? notificationService = null,
        Services.Solvers.IDockerSetupDialogService? dockerSetupDialog = null,
        Solvers.FdtdBackendSelectionViewModel? backendSelection = null)
    {
        _fileDialogService = fileDialogService;
        _errorConsole = errorConsole;
        _portMappingDialog = portMappingDialog;
        _notificationService = notificationService;
        _fdtdService = fdtdService;
        _fdtdRequestFactory = fdtdRequestFactory;
        _dockerSetupDialog = dockerSetupDialog;
        SetBackendSelection(backendSelection);
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
        Func<string>? smatrixKeyResolver = null,
        Func<ComponentSMatrixData, bool>? propagateToTemplate = null)
    {
        _smatrixKey = smatrixKey;
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
            ? string.Format(LocalizationService.Instance.Translate("CompSettings.TitleGlobal"), displayName)
            : string.Format(LocalizationService.Instance.Translate("CompSettings.TitleScoped"), displayName);
        StatusText = string.Empty;

        SolverStatus = string.Empty;
        RefreshEntries(notifyChanged: false);
        RefreshEffectiveEntries();
        OnPropertyChanged(nameof(CanRecalculate));
        RecalculateSMatrixCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task LoadFromFile()
    {
        if (_storedSMatrices == null)
            return;

        var path = await _fileDialogService.ShowOpenFileDialogAsync(
            LocalizationService.Instance.Translate("CompSettings.SelectSParamFileTitle"),
            "S-Parameter Files|*.sparam;*.dat;*.txt;*.s1p;*.s2p;*.s3p;*.s4p;*.sNp|All Files|*.*");

        if (path == null)
            return;

        var importer = FindImporter(path);
        if (importer == null)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("CompSettings.UnsupportedFileType"), Path.GetExtension(path));
            return;
        }

        IsImporting = true;
        StatusText = LocalizationService.Instance.Translate("CompSettings.Importing");

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
            StatusText = string.Format(LocalizationService.Instance.Translate("CompSettings.ImportFailed"), ex.Message)
                + (_errorConsole != null ? LocalizationService.Instance.Translate("CompSettings.SeeErrorConsoleSuffix") : "");
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

        StatusText = string.Format(
            LocalizationService.Instance.Translate("CompSettings.RemovedWavelength"), entry.WavelengthKey);
        RefreshEntries(notifyChanged: true);
    }
}
