using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Fork-shadow bookkeeping for bundled (read-only foundry) PDKs: a user PDK whose name matches
/// a bundled PDK is the user's editable fork and "shadows" the built-in original. This partial
/// remembers where each bundled original lives so the shadow can be applied (on save and at
/// startup) and reverted (delete fork = restore foundry truth). Bundled JSON files are never
/// written, moved, or deleted here.
/// </summary>
public partial class LeftPanelViewModel
{
    private readonly Dictionary<string, BundledPdkOrigin> _bundledPdkCatalog = new(StringComparer.OrdinalIgnoreCase);

    private sealed record BundledPdkOrigin(string FilePath, int ComponentCount);

    /// <summary>Remembers a loaded bundled PDK's file so forks can shadow and restore it.</summary>
    private void RecordBundledPdkOrigin(string pdkName, string filePath, int componentCount)
    {
        if (!_bundledPdkCatalog.ContainsKey(pdkName))
            _bundledPdkCatalog[pdkName] = new BundledPdkOrigin(filePath, componentCount);
    }

    /// <summary>
    /// The component count of the bundled original that <paramref name="pdkName"/> shadows, for
    /// the delete-confirm prompt — null when no bundled original is known under that name.
    /// </summary>
    internal int? GetBundledOriginalComponentCount(string pdkName) =>
        _bundledPdkCatalog.TryGetValue(pdkName, out var origin) ? origin.ComponentCount : null;

    /// <summary>
    /// Takes the bundled PDK's in-memory registration out of the library (templates, draft,
    /// PDK-manager row) so a user fork of the same name can take its place. Deliberately touches
    /// no files and no preferences — the bundled JSON stays on disk as the read-only original.
    /// </summary>
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

    /// <summary>Flags the freshly registered user PDK as a fork when a bundled original exists.</summary>
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
    /// After a save forked a bundled PDK (fork-on-save), swaps the library from the bundled
    /// entry to the user's copy: deregisters the bundled original in memory and registers the
    /// fork with all its components. The bundled entry is only displaced when the fork file
    /// actually loads and registers (verified) — a failed fork load leaves the built-in PDK
    /// fully registered and reports the problem (PR #742 review, finding 6). Returns false
    /// when <paramref name="pdkName"/> does not name a loaded bundled PDK, so the caller falls
    /// back to the normal registration.
    /// </summary>
    private bool TryShadowBundledPdkWithSavedFork(string pdkName, string forkFilePath)
    {
        var bundled = PdkManager.LoadedPdks.FirstOrDefault(p =>
            p.IsBundled && p.Name.Equals(pdkName, StringComparison.OrdinalIgnoreCase));
        if (bundled is null)
            return false;

        // TryReloadUserPdk parses the fork BEFORE deregistering the bundled entry, so a
        // failure here means nothing was changed.
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
    /// Re-registers the bundled original of <paramref name="pdkName"/> from its untouched JSON
    /// after the shadowing fork was removed — the in-session equivalent of the bundled load at
    /// startup. Returns false when no bundled original is known, the name is still occupied, or
    /// the file cannot be loaded.
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
    /// Loads the bundled original fresh (not from the read-only cache: the restored draft joins
    /// <see cref="_loadedPdkDrafts"/>, where it may be mutated later). Logs and returns null on
    /// failure.
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

    /// <summary>Puts a freshly loaded bundled original back into the library (in-memory only).</summary>
    private void RegisterRestoredBundledDraft(PdkDraft pdk, string filePath)
    {
        _loadedPdkDrafts.Add(pdk);
        int componentCount = 0;
        foreach (var pdkComp in pdk.Components)
        {
            var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName, pdk.GdsFactoryRoutingCrossSection);
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
    /// Delete on a fork = revert to the foundry truth: moves the user's copy to
    /// <c>user-pdks/.trash</c>, deregisters it, and restores the bundled original in place —
    /// same name, so placed components and the process snapshot stay valid. All-or-nothing:
    /// the bundled original is loaded BEFORE the fork is touched, so a failure (unreadable
    /// built-in JSON, locked fork file) leaves the fork registered and on disk — never a half
    /// state where both disappear (PR #742 review, finding 3).
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
    /// True when deleting <paramref name="template"/> reverts it to the bundled original
    /// instead of removing it: its PDK is a fork of a bundled PDK and the bundled original
    /// contains a component of the same name. The delete-confirm prompt announces the restore.
    /// </summary>
    public bool IsComponentRevertToBundled(ComponentTemplate template)
    {
        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);
        if (pdkInfo is not { IsBundled: false })
            return false;

        return FindBundledCounterpart(pdkInfo.Name, template.Name) is not null;
    }

    private readonly Dictionary<string, PdkDraft> _bundledOriginDraftCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The parsed bundled original of <paramref name="pdkName"/>, cached for the session —
    /// bundled JSONs are read-only, so one parse per file suffices and repeated lookups (layer
    /// checks, revert prompts) never re-hit the disk on the UI thread (PR #742 review,
    /// finding 7). Read failures are logged and NOT cached, so a transient lock can recover.
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

    /// <summary>Reads the bundled original's definition of a component, if both exist.</summary>
    private PdkComponentDraft? FindBundledCounterpart(string pdkName, string componentName) =>
        GetBundledOriginDraft(pdkName)?.Components
            .FirstOrDefault(c => string.Equals(c.Name, componentName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Delete on a forked component that still exists in the bundled original = revert that one
    /// component to the foundry definition inside the fork (with a full pre-edit backup of the
    /// fork file in <c>.trash</c>). Returns false when this is not a revert case — the caller
    /// then performs the plain component delete. Returns true (handled) even when the rewrite
    /// fails, so a failed revert never degrades into a delete.
    /// </summary>
    private bool TryRevertComponentToBundled(
        PdkInfoViewModel pdkInfo, CAP_DataAccess.Components.AddCustomComponent.UserPdkStore store, ComponentTemplate template)
    {
        if (pdkInfo.FilePath is null)
            return false;
        var counterpart = FindBundledCounterpart(pdkInfo.Name, template.Name);
        if (counterpart is null)
            return false;

        try
        {
            // Single load-modify-save (PR #742 review, finding 5): a failure leaves the fork
            // file unchanged instead of a half state with the component missing.
            if (!store.ReplaceComponent(pdkInfo.FilePath, counterpart))
            {
                _errorConsole?.LogError(
                    $"Failed to restore the built-in definition of '{template.Name}': the fork file '{pdkInfo.FilePath}' no longer exists.");
                return true;
            }
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError(
                $"Failed to restore the built-in definition of '{template.Name}' in '{pdkInfo.Name}': {ex.Message}", ex);
            return true;
        }

        ReplaceLibraryTemplateWithBundledDefinition(pdkInfo, template, counterpart);
        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        // A revert changes the template's physics exactly like an editor save does — placed
        // instances must adopt the RESTORED foundry definition, not keep the edited fork's
        // S-matrix until restart (PR #742 physics review).
        NotifyTemplateDefinitionSaved(pdkInfo.Name, template.Name);
        return true;
    }

    /// <summary>Swaps the customized library template and in-memory draft for the foundry definition.</summary>
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
            counterpart, pdkInfo.Name, forkDraft?.NazcaModuleName, forkDraft?.GdsFactoryRoutingCrossSection);
        restored.IsCustom = true;
        AllTemplates.Add(restored);
        if (!Categories.Contains(restored.Category))
            Categories.Add(restored.Category);
        if (!AllTemplates.Any(t => t.Category == customized.Category))
            Categories.Remove(customized.Category);
    }
}
