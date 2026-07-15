using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_DataAccess.Import;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// S-matrix display + file-import for the component editor. The S-matrix belongs to the
/// component definition (its geometry), so it lives here alongside Compute-with-Meep rather
/// than in a separate per-instance dialog.
/// </summary>
public partial class NewComponentViewModel
{
    private static readonly IReadOnlyList<ISParameterImporter> _sMatrixImporters =
        new ISParameterImporter[] { new LumericalSParameterImporter(), new TouchstoneImporter() };

    /// <summary>File picker returning an S-parameter file path, or null if cancelled.</summary>
    public Func<Task<string?>>? PickSMatrixFile { get; set; }

    /// <summary>Per-wavelength entries of the current S-matrix (imported or computed).</summary>
    public ObservableCollection<SMatrixEntryViewModel> SMatrixEntries { get; } = new();

    [ObservableProperty] private bool _hasSMatrix;

    /// <summary>
    /// Imports S-parameters (Lumerical .dat / Touchstone .sNp) as this component's S-matrix.
    /// Renders a preview first for the pin names, reconciles port names against them, and stores
    /// the result so a subsequent Save persists it instead of a black box.
    /// </summary>
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
            StatusText = $"Unsupported S-matrix file type: {ext}";
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
            StatusText = $"Loaded S-matrix: {resolved.PortCount} ports, " +
                         $"{resolved.SMatricesByWavelengthNm.Count} wavelength(s) from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"S-matrix import failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ImportedSParameters? ReconcilePorts(ImportedSParameters imported, IReadOnlyList<string> pinNames)
    {
        if (pinNames.Count == 0 || PortNameMapping.NamesAlignWithComponent(imported.PortNames, pinNames))
            return imported;

        if (imported.PortNames.Count != pinNames.Count)
        {
            StatusText = $"Cannot import: file has {imported.PortNames.Count} port(s) " +
                         $"but the component has {pinNames.Count} pin(s).";
            return null;
        }

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
