using System.Globalization;
using System.Linq;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Persistence.PIR;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// S-matrix list display half of the dialog: rebuilds the stored-entry list and the
/// "currently effective S-matrix" list (with per-wavelength override tags) from the
/// override store and the live component's resolved matrices.
/// </summary>
public partial class ComponentSettingsDialogViewModel
{
    /// <summary>Provenance line under the effective section ("Source: FDTD Tidy3D Cloud 2D"); empty for plain PDK originals.</summary>
    [ObservableProperty]
    private string _effectiveProvenanceText = string.Empty;

    /// <summary>True when the shown draft matrix is user-sourced and a bundled original can be restored.</summary>
    [ObservableProperty]
    private bool _canResetToPdkOriginal;

    private void RefreshEntries(bool notifyChanged)
    {
        SMatrixEntries.Clear();

        if (_storedSMatrices == null || !_storedSMatrices.TryGetValue(_smatrixKey, out var data))
        {
            HasSMatrices = false;
            if (notifyChanged)
            {
                RefreshEffectiveEntries();
                _onChanged?.Invoke();
            }
            return;
        }

        foreach (var kvp in data.Wavelengths.OrderBy(k => k.Key))
            SMatrixEntries.Add(new SMatrixEntryViewModel(kvp.Key, kvp.Value, data.SourceNote));

        HasSMatrices = SMatrixEntries.Count > 0;
        if (notifyChanged)
        {
            RefreshEffectiveEntries();
            _onChanged?.Invoke();
        }
    }

    private void RefreshEffectiveEntries()
    {
        EffectiveEntries.Clear();
        if (_effectiveSMatrices == null || _effectivePins == null)
        {
            HasEffectiveEntries = false;
            EffectiveProvenanceText = string.Empty;
            CanResetToPdkOriginal = false;
            return;
        }

        ComponentSMatrixData? overrideData = null;
        _storedSMatrices?.TryGetValue(_smatrixKey, out overrideData);
        foreach (var kvp in _effectiveSMatrices.OrderBy(k => k.Key))
        {
            // A wavelength is "overridden" iff the active store has an entry
            // with the same wavelength key — a wavelength present in the
            // PDK default but not in the override is still PDK-driven.
            bool isOverridden =
                overrideData != null &&
                overrideData.Wavelengths.ContainsKey(kvp.Key.ToString(CultureInfo.InvariantCulture));

            EffectiveEntries.Add(new EffectiveSMatrixEntryViewModel(
                kvp.Key, kvp.Value, _effectivePins, isOverridden,
                isOverridden ? overrideData!.SourceNote : null));
        }

        HasEffectiveEntries = EffectiveEntries.Count > 0;

        // The matrix is user-sourced when an override store entry carries a note
        // (per-instance/template override) or the underlying draft does (computed/
        // imported into a user PDK) — a bundled original has no note to show.
        var provenance = overrideData?.SourceNote ?? _draftSourceNote;
        EffectiveProvenanceText = HasEffectiveEntries && !string.IsNullOrWhiteSpace(provenance)
            ? string.Format(LocalizationService.Instance.Translate("CompSettings.EffectiveSource"), provenance)
            : string.Empty;
        CanResetToPdkOriginal = _resetToPdkOriginal != null && _draftSourceNote != null;
    }

    /// <summary>
    /// Restores the bundled foundry definition behind a user-PDK draft (same
    /// mechanism as the library's per-component restore) and refreshes the
    /// effective view from the restored template.
    /// </summary>
    [RelayCommand]
    private async Task ResetToPdkOriginal()
    {
        if (_resetToPdkOriginal == null)
            return;
        var fresh = await _resetToPdkOriginal();
        if (fresh == null)
            return;

        _draftSourceNote = fresh.SourceDraft?.SMatrix?.SourceNote;
        var tempInstance = ComponentTemplates.CreateFromTemplate(fresh, 0, 0);
        _effectiveSMatrices = tempInstance.WaveLengthToSMatrixMap;
        _effectivePins = tempInstance.PhysicalPins
            .Where(pp => pp.LogicalPin != null)
            .Select(pp => pp.LogicalPin!)
            .ToList();
        StatusText = LocalizationService.Instance.Translate("CompSettings.ResetToPdkOriginalDone");
        RefreshEntries(notifyChanged: true);
    }
}
