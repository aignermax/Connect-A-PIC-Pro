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

    private void TryReloadUserPdk(string path)
    {
        PdkDraft pdk;
        try
        {
            pdk = _pdkLoader.LoadFromFileForEditing(path);
        }
        catch (FileNotFoundException)
        {
            _preferencesService.RemoveUserPdkPath(path);
            return;
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Skipped user PDK '{Path.GetFileName(path)}' at startup: {ex.Message}", ex);
            return;
        }

        if (PdkManager.IsPdkNameLoaded(pdk.Name, null))
        {
            _errorConsole?.LogWarning($"User PDK '{pdk.Name}' at '{path}' duplicates an already-loaded PDK name; skipped at startup.");
            return;
        }

        _loadedPdkDrafts.Add(pdk);

        int addedCount = 0;
        foreach (var pdkComp in pdk.Components)
        {
            var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName, pdk.GdsFactoryRoutingCrossSection);
            template.IsCustom = true;
            AllTemplates.Add(template);
            if (!Categories.Contains(template.Category))
                Categories.Add(template.Category);
            addedCount++;
        }

        PdkManager.RegisterPdk(pdk.Name, path, false, addedCount);
    }
}
