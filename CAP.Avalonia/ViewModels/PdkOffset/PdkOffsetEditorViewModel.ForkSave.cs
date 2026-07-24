using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.PdkOffset;

/// <summary>
/// PdkOffsetEditorViewModel partial — the save flow. Custom PDKs write back to
/// their source JSON directly; BUNDLED PDKs are read-only at runtime (repo
/// invariant), so their save forks the edited draft into the managed user-pdks
/// root and shadow-registers the fork, mirroring the component editor's
/// fork-on-save semantics. All state lives in the main partial file.
/// </summary>
public partial class PdkOffsetEditorViewModel
{
    /// <summary>
    /// Raised after a bundled PDK was saved as a user fork:
    /// (pdkName, forkFilePath). Wired by <see cref="MainViewModel"/> to
    /// <c>LeftPanelViewModel.RegisterSavedPdkFork</c>, which swaps the library
    /// from the bundled entry to the fork (shadow registration) — the same
    /// mechanism the component editor's fork-on-save uses.
    /// </summary>
    public Action<string, string>? BundledPdkForkSaved { get; set; }

    /// <summary>
    /// Raised after a direct (non-fork) save: (pdkName, filePath). After the
    /// first fork save the editor is retargeted at the fork file, so every
    /// LATER save takes the direct branch — without this notification the
    /// library would keep the first save's in-memory templates until restart
    /// and exports would silently use stale offsets (round-5 review [1b]).
    /// Wired to <c>LeftPanelViewModel.RefreshRegisteredPdkAfterExternalSave</c>.
    /// </summary>
    public Action<string, string>? UserPdkSaved { get; set; }

    /// <summary>
    /// Probe deciding whether a file path lies in the read-only bundled-PDK
    /// directory. Path-based on purpose: the library row check alone misses a
    /// bundled PDK whose registration was already replaced by a shadowing fork
    /// (component-editor fork in the same session, or a startup shadow) — in
    /// that state saving must STILL fork, never write the shipped JSON
    /// (round-5 review [1]). Injectable for tests.
    /// </summary>
    internal Func<string, bool> IsBundledPdkFilePath { get; set; } =
        static path => BundledPdkPaths.IsBundledPdkFile(path);

    /// <summary>
    /// Saves the current PDK draft — directly for custom PDKs, as a user fork
    /// for anything that originates from the bundled directory.
    /// </summary>
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
            // Bundled detection is registry-based AND path-based: once a fork shadows
            // the bundled entry, the IsBundled row is gone, but the loaded file still
            // lives in the read-only bundled directory. If a fork already exists,
            // SaveDraftAsFork retargets the write into THAT fork (backing it up first).
            if (FindLoadedBundledPdkFor(_loadedFilePath) is { } bundled)
            {
                SaveBundledPdkAsFork(bundled.Name);
                return;
            }
            if (IsBundledPdkFilePath(_loadedFilePath))
            {
                SaveBundledPdkAsFork(_loadedPdk.Name);
                return;
            }

            _pdkSaver.SaveToFile(_loadedPdk, _loadedFilePath);
            HasUnsavedChanges = false;
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.SavedTo"), Path.GetFileName(_loadedFilePath));
            UserPdkSaved?.Invoke(_loadedPdk.Name, _loadedFilePath);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.SaveFailed"), ex.Message);
        }
    }

    /// <summary>
    /// The loaded bundled-PDK library row matching <paramref name="filePath"/>,
    /// or null when no registered bundled entry points at the loaded file.
    /// </summary>
    private PdkInfoViewModel? FindLoadedBundledPdkFor(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        return _pdkManager.LoadedPdks.FirstOrDefault(p =>
            p.IsBundled && p.FilePath != null &&
            string.Equals(Path.GetFullPath(p.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fork-on-save for a bundled PDK: writes the edited draft to the user-pdks
    /// root (backing up any pre-existing fork to .trash), retargets the editor
    /// at the fork so subsequent saves go there directly, and notifies the
    /// library so the fork shadows the bundled entry (or, when the fork already
    /// shadows it, so the fork's templates are reloaded). The bundled JSON is
    /// never written.
    /// </summary>
    private void SaveBundledPdkAsFork(string pdkName)
    {
        if (_userPdkStore == null)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Status.BundledReadOnly"), pdkName);
            return;
        }

        var forkPath = _userPdkStore.SaveDraftAsFork(_loadedPdk!, pdkName);
        _loadedFilePath = forkPath;
        HasUnsavedChanges = false;
        StatusText = string.Format(
            LocalizationService.Instance.Translate("PdkOffset.Status.SavedForkTo"), Path.GetFileName(forkPath));
        BundledPdkForkSaved?.Invoke(pdkName, forkPath);
    }
}
