using System.Collections.ObjectModel;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_DataAccess.Import;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public partial class NewComponentViewModel
{
    private static readonly IReadOnlyList<ISParameterImporter> _sMatrixImporters =
        new ISParameterImporter[] { new LumericalSParameterImporter(), new TouchstoneImporter() };

    public Func<Task<string?>>? PickSMatrixFile { get; set; }

    /// <summary>Opens the read-only stored-S-matrix viewer for (pdkName, componentName).</summary>
    public Func<string, string, Task>? ShowStoredSMatrices { get; set; }

    public ObservableCollection<SMatrixEntryViewModel> SMatrixEntries { get; } = new();

    [ObservableProperty] private bool _hasSMatrix;

    [RelayCommand]
    private async Task ShowSMatrices()
    {
        if (ShowStoredSMatrices is null || SelectedCustomPdk is null) return;
        var name = ComponentName?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await ShowStoredSMatrices(SelectedCustomPdk.Name, name);
    }

    [RelayCommand]
    private async Task LoadSMatrixFromFile()
    {
        if (IsBusy || PickSMatrixFile is null) return;
        var path = await PickSMatrixFile();
        if (path is null) return;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var importer = _sMatrixImporters.FirstOrDefault(i => i.SupportedExtensions.Contains(ext));
        if (importer is null)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("NewComp.UnsupportedSMatrixFileType"), ext);
            return;
        }

        IsBusy = true;
        try
        {
            if (!await EnsurePreviewAsync() || _lastPreview is not { Success: true } preview)
                return;

            var imported = await importer.ImportAsync(path);
            var resolved = ReconcilePorts(imported, preview.Pins.Select(p => p.Name).ToList());
            if (resolved is null) return;

            _computedModel = SParameterConverter.ToComponentSMatrixData(resolved);
            RefreshSMatrixEntries();
            var remapNote = _portsMappedByPosition
                ? LocalizationService.Instance.Translate("NewComp.PortsAutoMapped")
                : "";
            StatusText = string.Format(
                LocalizationService.Instance.Translate("NewComp.LoadedSMatrix"),
                resolved.PortCount, resolved.SMatricesByWavelengthNm.Count,
                Path.GetFileName(path), remapNote, SaveButtonLabel);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("NewComp.SMatrixImportFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool _portsMappedByPosition;

    private ImportedSParameters? ReconcilePorts(ImportedSParameters imported, IReadOnlyList<string> pinNames)
    {
        _portsMappedByPosition = false;
        if (pinNames.Count == 0 || PortNameMapping.NamesAlignWithComponent(imported.PortNames, pinNames))
            return imported;

        if (imported.PortNames.Count != pinNames.Count)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("NewComp.CannotImportPortCountMismatch"),
                imported.PortNames.Count, pinNames.Count);
            return null;
        }

        _portsMappedByPosition = true;
        return PortNameMapping.Remap(imported, PortNameMapping.BuildDefaultMapping(imported.PortNames, pinNames));
    }

    internal void RefreshSMatrixEntries()
    {
        SMatrixEntries.Clear();
        if (_computedModel is not null)
        {
            foreach (var kvp in _computedModel.Wavelengths.OrderBy(k => k.Key))
                SMatrixEntries.Add(new SMatrixEntryViewModel(kvp.Key, kvp.Value, _computedModel.SourceNote));
        }
        HasSMatrix = SMatrixEntries.Count > 0;
    }
}
