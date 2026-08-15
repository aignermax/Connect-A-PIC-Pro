using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Fork-shadow bookkeeping for bundled (read-only foundry) PDKs: a user PDK whose name matches
/// a bundled PDK is the user's editable fork and shadows the built-in original. Bundled JSON
/// files are never written, moved, or deleted here.
/// </summary>
public partial class LeftPanelViewModel
{
    private readonly Dictionary<string, BundledPdkOrigin> _bundledPdkCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PdkDraft> _bundledOriginDraftCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record BundledPdkOrigin(string FilePath, int ComponentCount);

    private void RecordBundledPdkOrigin(string pdkName, string filePath, int componentCount)
    {
        if (!_bundledPdkCatalog.ContainsKey(pdkName))
            _bundledPdkCatalog[pdkName] = new BundledPdkOrigin(filePath, componentCount);
    }

    internal int? GetBundledOriginalComponentCount(string pdkName) =>
        _bundledPdkCatalog.TryGetValue(pdkName, out var origin) ? origin.ComponentCount : null;

    /// <summary>In-memory deregistration only — the bundled JSON stays on disk as the read-only original.</summary>
    private void DeregisterBundledPdkForShadow(PdkInfoViewModel bundled)
    {
        if (bundled.FilePath is not null)
            RecordBundledPdkOrigin(bundled.Name, bundled.FilePath, bundled.ComponentCount);

        RemoveTemplatesForPdk(bundled.Name);
        if (bundled.FilePath is { } bundledPath)
        {
            var normalized = Path.GetFullPath(bundledPath);
            _loadedPdkDrafts.RemoveAll(d => d.FilePath != null && Path.GetFullPath(d.FilePath) == normalized);
        }
        PdkManager.LoadedPdks.Remove(bundled);
    }

    private void MarkIfShadowsBundledPdk(string pdkName)
    {
        if (!_bundledPdkCatalog.ContainsKey(pdkName))
            return;

        var row = PdkManager.LoadedPdks.FirstOrDefault(p =>
            !p.IsBundled && p.Name.Equals(pdkName, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
            row.ShadowsBundledPdk = true;
    }

    /// <summary>
    /// Entry point for whole-PDK fork saves (PDK offset editor): the fork file
    /// already contains the complete edited PDK. When the fork ALREADY shadows
    /// the bundled entry (an earlier fork save in this session, or a startup
    /// shadow) the save is a refresh of the registered fork; otherwise the
    /// library swaps from the bundled entry to the fork. Logs when neither
    /// applies — the fork was saved but the library keeps its registration.
    /// </summary>
    public void RegisterSavedPdkFork(string pdkName, string forkFilePath)
    {
        if (TryRefreshRegisteredUserPdk(forkFilePath))
            return;
        if (TryShadowBundledPdkWithSavedFork(pdkName, forkFilePath))
            return;
        _errorConsole?.LogError(
            $"A fork of '{pdkName}' was saved to '{forkFilePath}', but no loaded bundled PDK " +
            "matches that name — the library was not switched to the fork.");
    }

    /// <summary>
    /// Refreshes the library after an external editor (PDK offset editor) saved a PDK
    /// file DIRECTLY — i.e. without the fork flow. Needed because the offset editor is
    /// retargeted at the fork after its first save: every later save writes the fork file
    /// directly, and without this reload the in-memory templates (and thus new placements
    /// and GDS exports) would keep the first save's values until restart. A file that is
    /// not registered in the library (loaded via file dialog) is a silent no-op.
    /// </summary>
    public void RefreshRegisteredPdkAfterExternalSave(string pdkName, string filePath) =>
        TryRefreshRegisteredUserPdk(filePath);

    /// <summary>
    /// Reloads the in-memory templates of an already-registered NON-bundled PDK from its
    /// file. All-or-nothing: the file is parsed BEFORE the current registration is
    /// dropped, so an unreadable file leaves the library unchanged (and logs). Returns
    /// false when no registered non-bundled row points at <paramref name="filePath"/>.
    /// </summary>
    internal bool TryRefreshRegisteredUserPdk(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        var row = PdkManager.LoadedPdks.FirstOrDefault(p =>
            !p.IsBundled && p.FilePath != null &&
            string.Equals(Path.GetFullPath(p.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return false;

        try
        {
            _pdkLoader.LoadFromFileForEditing(filePath);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError(
                $"'{row.Name}' was saved to '{filePath}', but the file could not be reloaded " +
                $"into the library — the previous in-memory state stays active: {ex.Message}", ex);
            return true;
        }

        RemoveTemplatesForPdk(row.Name);
        _loadedPdkDrafts.RemoveAll(d =>
            d.FilePath != null && Path.GetFullPath(d.FilePath) == normalized);
        PdkManager.LoadedPdks.Remove(row);

        if (!TryReloadUserPdk(filePath))
        {
            _errorConsole?.LogError(
                $"'{row.Name}' was saved to '{filePath}', but re-registering it in the library failed.");
            return true;
        }

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        return true;
    }

    /// <summary>
    /// Swaps the library from the bundled entry to the user's saved fork. Returns false when
    /// <paramref name="pdkName"/> is no loaded bundled PDK (caller registers normally). The
    /// bundled entry is only displaced once the fork actually loads — a failed load leaves the
    /// built-in PDK fully registered and reports the problem.
    /// </summary>
    private bool TryShadowBundledPdkWithSavedFork(string pdkName, string forkFilePath)
    {
        var bundled = PdkManager.LoadedPdks.FirstOrDefault(p =>
            p.IsBundled && p.Name.Equals(pdkName, StringComparison.OrdinalIgnoreCase));
        if (bundled is null)
            return false;

        // TryReloadUserPdk parses the fork BEFORE deregistering the bundled entry, so a failure here changes nothing.
        if (!TryReloadUserPdk(forkFilePath))
        {
            _errorConsole?.LogError(
                $"Your edit was saved to '{forkFilePath}', but the fork could not be loaded into the " +
                $"library — the built-in '{pdkName}' remains active. See previous errors for details.");
            return true;
        }

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        return true;
    }

    /// <summary>
    /// Re-registers the bundled original from its untouched JSON after the shadowing fork was
    /// removed. Returns false when no original is known, the name is still occupied, or the
    /// file cannot be loaded.
    /// </summary>
    internal bool RestoreBundledPdk(string pdkName)
    {
        if (!_bundledPdkCatalog.TryGetValue(pdkName, out var origin))
            return false;
        if (PdkManager.IsPdkNameLoaded(pdkName, null))
            return false;

        var pdk = LoadBundledOriginForRestore(pdkName, origin);
        if (pdk is null)
            return false;

        RegisterRestoredBundledDraft(pdk, origin.FilePath);
        return true;
    }

    /// <summary>
    /// Loads fresh, not from <see cref="_bundledOriginDraftCache"/>: the restored draft joins
    /// <see cref="_loadedPdkDrafts"/>, where it may be mutated later.
    /// </summary>
    private PdkDraft? LoadBundledOriginForRestore(string pdkName, BundledPdkOrigin origin)
    {
        try
        {
            return _pdkLoader.LoadFromFile(origin.FilePath);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Could not restore bundled PDK '{pdkName}': {ex.Message}", ex);
            return null;
        }
    }

    private void RegisterRestoredBundledDraft(PdkDraft pdk, string filePath)
    {
        _loadedPdkDrafts.Add(pdk);
        int componentCount = 0;
        foreach (var pdkComp in pdk.Components)
        {
            var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName, pdk.GdsFactoryRoutingCrossSection, pdk.Process);
            template.IsCustom = false;
            AllTemplates.Add(template);
            if (!Categories.Contains(template.Category))
                Categories.Add(template.Category);
            componentCount++;
        }
        PdkManager.RegisterPdk(pdk.Name, filePath, true, componentCount);

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }

    /// <summary>
    /// Delete on a fork = revert to foundry truth: trashes the user's copy and restores the
    /// bundled original under the same name, so placed components and the process snapshot stay
    /// valid. All-or-nothing: the original is loaded BEFORE the fork is touched, so a failure
    /// leaves the fork registered and on disk.
    /// </summary>
    internal bool RevertShadowForkToBundled(PdkInfoViewModel fork)
    {
        var store = _addCustomComponentDeps?.UserPdkStore;
        if (store is null || fork.IsBundled || fork.FilePath is null)
            return false;
        if (!_bundledPdkCatalog.TryGetValue(fork.Name, out var origin))
            return false;

        var bundledDraft = LoadBundledOriginForRestore(fork.Name, origin);
        if (bundledDraft is null)
            return false;

        try
        {
            store.MoveToTrash(fork.FilePath);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to move your copy of '{fork.Name}' to the trash: {ex.Message}", ex);
            return false;
        }

        UnregisterPdk(fork.FilePath);
        RegisterRestoredBundledDraft(bundledDraft, origin.FilePath);
        return true;
    }

    /// <summary>
    /// True when deleting <paramref name="template"/> reverts it to the bundled original instead
    /// of removing it, so the delete-confirm prompt can announce the restore.
    /// </summary>
    public bool IsComponentRevertToBundled(ComponentTemplate template)
    {
        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);
        if (pdkInfo is not { IsBundled: false })
            return false;

        return FindBundledCounterpart(pdkInfo.Name, template.Name) is not null;
    }

    /// <summary>
    /// Reverts a fork component to the bundled foundry definition (same mechanism as
    /// the library's delete-as-revert, but callable without the delete intent — e.g.
    /// the component-settings "Reset to PDK original"). Returns <see cref="BundledRevertResult.Reverted"/>
    /// only when the fork file was actually rewritten and the library swapped back —
    /// <see cref="BundledRevertResult.NotARevertCase"/> when there is nothing to revert
    /// and <see cref="BundledRevertResult.Failed"/> when the rewrite failed, so callers
    /// never report "restored" for a component that is still the user's edited copy.
    /// </summary>
    public BundledRevertResult RestoreTemplateToBundledOriginal(ComponentTemplate template)
    {
        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);
        var userPdkStore = _addCustomComponentDeps?.UserPdkStore;
        if (pdkInfo is null || pdkInfo.IsBundled || pdkInfo.FilePath is null || userPdkStore is null)
            return BundledRevertResult.NotARevertCase;

        return RevertComponentToBundled(pdkInfo, userPdkStore, template);
    }

    /// <summary>
    /// Cached per session — bundled JSONs are read-only. Read failures are logged and NOT
    /// cached, so a transient file lock can recover.
    /// </summary>
    private PdkDraft? GetBundledOriginDraft(string pdkName)
    {
        if (!_bundledPdkCatalog.TryGetValue(pdkName, out var origin))
            return null;
        if (_bundledOriginDraftCache.TryGetValue(pdkName, out var cached))
            return cached;

        try
        {
            var draft = _pdkLoader.LoadFromFile(origin.FilePath);
            _bundledOriginDraftCache[pdkName] = draft;
            return draft;
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Could not read bundled PDK '{pdkName}': {ex.Message}", ex);
            return null;
        }
    }

    private PdkComponentDraft? FindBundledCounterpart(string pdkName, string componentName) =>
        GetBundledOriginDraft(pdkName)?.Components
            .FirstOrDefault(c => string.Equals(c.Name, componentName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Delete on a forked component that still exists in the bundled original = revert that one
    /// component to the foundry definition inside the fork (with a full backup in .trash).
    /// Returns false ONLY when this is not a revert case (caller deletes normally). Returns true
    /// even when the rewrite fails, so a failed revert never degrades into a delete.
    /// </summary>
    private bool TryRevertComponentToBundled(
        PdkInfoViewModel pdkInfo, CAP_DataAccess.Components.AddCustomComponent.UserPdkStore store, ComponentTemplate template) =>
        RevertComponentToBundled(pdkInfo, store, template) != BundledRevertResult.NotARevertCase;

    /// <summary>
    /// The tri-state core of <see cref="TryRevertComponentToBundled"/>: distinguishes a
    /// genuinely rewritten fork (<see cref="BundledRevertResult.Reverted"/>) from a failed
    /// rewrite (<see cref="BundledRevertResult.Failed"/>), which the delete flow deliberately
    /// collapses into one "handled, do not delete" answer.
    /// </summary>
    private BundledRevertResult RevertComponentToBundled(
        PdkInfoViewModel pdkInfo, CAP_DataAccess.Components.AddCustomComponent.UserPdkStore store, ComponentTemplate template)
    {
        if (pdkInfo.FilePath is null)
            return BundledRevertResult.NotARevertCase;
        var counterpart = FindBundledCounterpart(pdkInfo.Name, template.Name);
        if (counterpart is null)
            return BundledRevertResult.NotARevertCase;

        try
        {
            // Single load-modify-save: a failure leaves the fork file unchanged, never half-rewritten.
            if (!store.ReplaceComponent(pdkInfo.FilePath, counterpart))
            {
                _errorConsole?.LogError(
                    $"Failed to restore the built-in definition of '{template.Name}': the fork file '{pdkInfo.FilePath}' no longer exists.");
                return BundledRevertResult.Failed;
            }
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError(
                $"Failed to restore the built-in definition of '{template.Name}' in '{pdkInfo.Name}': {ex.Message}", ex);
            return BundledRevertResult.Failed;
        }

        ReplaceLibraryTemplateWithBundledDefinition(pdkInfo, template, counterpart);
        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        // A revert changes physics like an editor save: placed instances must adopt the restored
        // foundry definition now, not keep the edited fork's S-matrix until restart.
        NotifyTemplateDefinitionSaved(pdkInfo.Name, template.Name);
        return BundledRevertResult.Reverted;
    }

    private void ReplaceLibraryTemplateWithBundledDefinition(
        PdkInfoViewModel pdkInfo, ComponentTemplate customized, PdkComponentDraft counterpart)
    {
        var normalized = Path.GetFullPath(pdkInfo.FilePath!);
        var forkDraft = _loadedPdkDrafts.FirstOrDefault(d =>
            d.FilePath != null && Path.GetFullPath(d.FilePath) == normalized);
        forkDraft?.Components.RemoveAll(c => string.Equals(c.Name, customized.Name, StringComparison.OrdinalIgnoreCase));
        forkDraft?.Components.Add(counterpart);

        AllTemplates.Remove(customized);
        var restored = ConvertPdkComponentToTemplate(
            counterpart, pdkInfo.Name, forkDraft?.NazcaModuleName, forkDraft?.GdsFactoryRoutingCrossSection, forkDraft?.Process);
        restored.IsCustom = true;
        AllTemplates.Add(restored);
        if (!Categories.Contains(restored.Category))
            Categories.Add(restored.Category);
        if (!AllTemplates.Any(t => t.Category == customized.Category))
            Categories.Remove(customized.Category);
    }
}
