using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

public partial class LeftPanelViewModel
{
    internal Task ReloadUserPdksAtStartupAsync(string? userPdkRootOverride = null)
    {
        var root = userPdkRootOverride ?? UserPdkStore.DefaultRootDirectory;

        foreach (var path in CollectUserPdkCandidatePaths(root))
        {
            if (PdkManager.IsPdkLoaded(path))
                continue;

            TryReloadUserPdk(path);
        }

        ReapplyActiveProcessAfterPdkChange();
        // Re-apply the persisted enable selection so a deliberately-unchecked user PDK stays
        // unchecked across restarts; otherwise FilterComponents would persist it back to enabled.
        // Skipped under a process lock, where the enabled set is derived state.
        if (PdkManager.ManualTogglesEnabled && _preferencesService.GetEnabledPdks().Count > 0)
            RestorePdkFilterState();
        else
            FilterComponents();
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> CollectUserPdkCandidatePaths(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void AddCandidate(string rawPath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch (Exception ex)
            {
                _errorConsole?.LogWarning(
                    $"Skipped malformed user-PDK path '{rawPath}' at startup: {ex.Message}");
                return;
            }
            // Legacy GDS-import PDKs are design-scoped since #830: their components
            // load from the referencing .lun (after a one-time migration), so the
            // stale global files must not flood every session's library — nor spam
            // the error console when they no longer validate (#829). Skipped
            // silently, never deleted: an unmigrated old design may still need them.
            if (IsLegacyGdsImportPdkFile(fullPath))
                return;
            if (seen.Add(fullPath))
                result.Add(fullPath);
        }

        if (Directory.Exists(root))
        {
            foreach (var path in Directory.GetFiles(root, "*.json"))
                AddCandidate(path);
        }

        foreach (var path in _preferencesService.GetUserPdkPaths())
            AddCandidate(path);

        return result;
    }

    /// <summary>
    /// True for user-PDK files written by pre-#830 GDS imports: their names are
    /// the slug of "GDS Import - &lt;file stem&gt;". Name-based on purpose — the
    /// point is to skip these files WITHOUT parsing them (many are broken, #829).
    /// </summary>
    private static bool IsLegacyGdsImportPdkFile(string fullPath) =>
        Path.GetFileName(fullPath).StartsWith("gds-import-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads and registers one user PDK file; returns false when the file could not be loaded
    /// or its name duplicates a loaded non-bundled PDK. A bundled entry is only deregistered
    /// AFTER the fork file parsed successfully, so a broken fork never removes the built-in PDK.
    /// </summary>
    private bool TryReloadUserPdk(string path)
    {
        PdkDraft pdk;
        try
        {
            pdk = _pdkLoader.LoadFromFileForEditing(path);
        }
        catch (FileNotFoundException)
        {
            _preferencesService.RemoveUserPdkPath(path);
            return false;
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Skipped user PDK '{Path.GetFileName(path)}' at startup: {ex.Message}", ex);
            return false;
        }

        if (PdkManager.IsPdkNameLoaded(pdk.Name, null))
        {
            // A user PDK named like a BUNDLED one is the user's fork and shadows the built-in
            // original. Deliberately name-based at startup: the file in user-pdks is the only
            // truth about a fork's existence (the creating session is gone), and non-fork PDKs
            // under a bundled name are blocked in the UI. Any other collision is still skipped.
            var shadowedBundled = PdkManager.LoadedPdks.FirstOrDefault(p =>
                p.IsBundled && p.Name.Equals(pdk.Name, StringComparison.OrdinalIgnoreCase));
            if (shadowedBundled is null)
            {
                _errorConsole?.LogWarning($"User PDK '{pdk.Name}' at '{path}' duplicates an already-loaded PDK name; skipped at startup.");
                return false;
            }
            DeregisterBundledPdkForShadow(shadowedBundled);
        }

        _loadedPdkDrafts.Add(pdk);

        int addedCount = 0;
        foreach (var pdkComp in pdk.Components)
        {
            var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName, pdk.GdsFactoryRoutingCrossSection, pdk.Process);
            template.IsCustom = true;
            AllTemplates.Add(template);
            if (!Categories.Contains(template.Category))
                Categories.Add(template.Category);
            addedCount++;
        }

        PdkManager.RegisterPdk(pdk.Name, path, false, addedCount);
        MarkIfShadowsBundledPdk(pdk.Name);
        return true;
    }
}
