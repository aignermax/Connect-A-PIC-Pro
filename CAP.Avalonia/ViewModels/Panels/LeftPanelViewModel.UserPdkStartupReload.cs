using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Startup reload of user-authored PDKs (issue #700). <see cref="UserPreferencesService.GetUserPdkPaths"/>
/// only ever remembers paths the app itself imported — nothing replayed them at app start, so a
/// previously-imported custom PDK vanished from the PDK-management list on the next launch even
/// though the "New Component" dialog (which scans the user-pdks directory directly) still saw it.
/// This reload closes that gap by combining a direct directory scan of the user-pdks root (catching
/// PDKs placed there without ever going through <see cref="LeftPanelViewModel.LoadPdk"/>, e.g. a
/// process-less/component-less PDK created by a future "+" flow) with the remembered import paths
/// (catching PDKs stored outside the default root). Split into its own partial purely to keep
/// <c>LeftPanelViewModel.cs</c> under the project's line-count limit.
/// </summary>
public partial class LeftPanelViewModel
{
    /// <summary>
    /// Re-registers every user-authored PDK found on disk into the library, exactly once per
    /// resolved full path (issue #700). Intended to run once, right after the bundled PDKs are
    /// loaded in <see cref="Initialize"/>. Returns <see cref="Task"/> (not <c>async</c>, and with
    /// no <c>await</c> inside) purely so the call site can fire it the same way it would an actual
    /// async operation — all of the work is small, local file I/O over a handful of JSON files and
    /// runs synchronously to completion before the task is handed back.
    /// </summary>
    /// <param name="userPdkRootOverride">
    /// Directory to scan instead of <see cref="UserPdkStore.DefaultRootDirectory"/>. Exists so
    /// tests can point the scan at a temp directory instead of the real per-user app-data folder.
    /// </param>
    internal Task ReloadUserPdksAtStartupAsync(string? userPdkRootOverride = null)
    {
        var root = userPdkRootOverride ?? UserPdkStore.DefaultRootDirectory;

        foreach (var path in CollectUserPdkCandidatePaths(root))
        {
            if (PdkManager.IsPdkLoaded(path))
                continue;

            TryReloadUserPdk(path);
        }

        // One reapply/refilter for the whole batch, not one per file (mirrors
        // CustomComponentLibraryRegistrar.Register's per-call reapply, just batched here).
        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every candidate user-PDK path to consider, deduplicated by full path: the directory scan
    /// (<c>*.json</c> directly under <paramref name="root"/> — <see cref="Directory.GetFiles(string, string)"/>
    /// defaults to <c>TopDirectoryOnly</c>, which already excludes anything under a <c>.trash</c>
    /// subfolder without needing a special case) plus the remembered import paths from
    /// <see cref="UserPreferencesService.GetUserPdkPaths"/>. A path present in both sources is
    /// returned only once.
    /// </summary>
    private IReadOnlyList<string> CollectUserPdkCandidatePaths(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void AddCandidate(string rawPath)
        {
            var fullPath = Path.GetFullPath(rawPath);
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
    /// Loads and registers a single user PDK, tolerating failure the same way a manual import via
    /// <see cref="LeftPanelViewModel.LoadPdk"/> does: a missing file is dropped from remembered
    /// preferences (it can never reappear on its own, and the file may have been renamed or
    /// deleted outside the app), while any other failure (a corrupted or mid-write file) is skipped
    /// WITHOUT touching preferences, since that failure could be transient. A name collision with
    /// an already-loaded PDK is skipped as a tolerated duplicate rather than raised as an error.
    /// </summary>
    private void TryReloadUserPdk(string path)
    {
        PdkDraft pdk;
        try
        {
            // The edit-tolerant loader — the same one UserPdkStore and CustomComponentLibraryRegistrar
            // read user PDKs with — so a user PDK missing a Nazca origin offset still reloads.
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
            // User-loaded PDK — editable via the library's "Edit…" action.
            template.IsCustom = true;
            AllTemplates.Add(template);
            if (!Categories.Contains(template.Category))
                Categories.Add(template.Category);
            addedCount++;
        }

        PdkManager.RegisterPdk(pdk.Name, path, false, addedCount);
    }
}
