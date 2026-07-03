using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;

/// <summary>
/// Backend-selection concern of the per-instance override editor (issue #637):
/// the Nazca | gdsfactory toggle, the backend-specific UI texts, and the
/// starter-stub swap. Kept in its own partial so the editor's core file stays
/// within the architecture size limit.
/// </summary>
public partial class InstanceNazcaCodeEditorViewModel
{
    /// <summary>
    /// Self-contained starter shown in the (editable) override box. Editing the original
    /// PDK source in place is not possible — it is a decorated closure with non-standalone
    /// references — so the editor's honest model is "view the original (read-only) + write
    /// your own self-contained code here to override the geometry". Leaving this
    /// unchanged keeps the preview on the real component (rendered via module mode).
    /// </summary>
    private static string OverrideStub => OverrideBackendTexts.Nazca.Stub;

    /// <summary>
    /// True when the override editor targets the gdsfactory backend instead of
    /// Nazca (issue #637). Switching swaps the help texts / docs / starter stub
    /// and routes preview + apply through the gdsfactory preview service; the
    /// applied override records the backend so exports use the matching runner.
    /// </summary>
    [ObservableProperty]
    private bool _isGdsFactoryBackend;

    /// <summary>Backend-specific UI texts (title, hint, docs, examples) for the current selection.</summary>
    public OverrideBackendTexts BackendTexts => OverrideBackendTexts.For(IsGdsFactoryBackend);

    /// <summary>Swaps texts and (if untouched) the starter stub when the backend toggles.</summary>
    partial void OnIsGdsFactoryBackendChanged(bool value)
    {
        IsValid = false;
        OnPropertyChanged(nameof(BackendTexts));
        ApplyOverrideCommand.NotifyCanExecuteChanged();
        SwapStubForBackend(value);
    }

    /// <summary>
    /// Replaces the editor content with the new backend's starter stub — but only
    /// when the current content is still an untouched stub / unchanged original,
    /// so a user's own code is never discarded by toggling the backend.
    /// </summary>
    private void SwapStubForBackend(bool isGdsFactory)
    {
        var trimmed = (Code ?? string.Empty).Trim();
        bool isUntouched =
            trimmed.Length == 0
            || trimmed == OverrideBackendTexts.Nazca.Stub.Trim()
            || trimmed == OverrideBackendTexts.GdsFactory.Stub.Trim()
            || (_originalSourceCode != null && trimmed == _originalSourceCode.Trim());
        if (isUntouched)
            Code = OverrideBackendTexts.For(isGdsFactory).Stub;
    }

    /// <summary>Replaces the editor content with the selected backend's showcase example (from the help flyout).</summary>
    [RelayCommand]
    private void InsertStarter() => Code = BackendTexts.StarterExample;
}
