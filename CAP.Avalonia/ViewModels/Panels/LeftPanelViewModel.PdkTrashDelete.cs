using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Delete-to-trash for user-authored PDKs and their individual custom components (LC-T5): the
/// mirror image of <see cref="LeftPanelViewModel.RegisterCreatedPdk"/> and
/// <see cref="LeftPanelViewModel.RegisterSavedCustomComponent"/> — everywhere those register a
/// PDK/component INTO the library, this partial removes it again. Never touches bundled
/// (Foundry) PDKs; <c>MainWindow.axaml</c> already hides the "Delete…" button/menu item for a
/// bundled row (<c>PdkInfoViewModel.IsBundled</c> / <c>ComponentTemplate.IsCustom</c>), and both
/// methods below repeat that guard here as the authoritative check (never trust the UI alone).
/// The actual file move to <c>.trash</c> is the caller's job via
/// <see cref="CAP_DataAccess.Components.AddCustomComponent.UserPdkStore"/> — mirrors how
/// <see cref="RegisterCreatedPdk"/> only registers a file the caller already created; this
/// partial only updates in-memory/library state to match. Split into its own partial purely to
/// keep <c>LeftPanelViewModel.cs</c> under the project's line-count limit.
/// </summary>
public partial class LeftPanelViewModel
{
    /// <summary>
    /// Deregisters a user-loaded PDK (already moved to <c>.trash</c> by the caller,
    /// <c>UserPdkStore.MoveToTrash</c>) from the library: every
    /// <see cref="ComponentTemplate"/> whose <see cref="ComponentTemplate.PdkSource"/> is this
    /// PDK's name, any category that only those components used, the
    /// <see cref="PdkManagerViewModel.LoadedPdks"/> entry, the in-memory <see cref="PdkDraft"/>
    /// (matched by its loader-stamped <see cref="PdkDraft.FilePath"/>), and the remembered
    /// import path in <see cref="UserPreferencesService"/> — the exact reverse of what
    /// <see cref="TryReloadUserPdk"/>/<see cref="RegisterCreatedPdk"/> add. Re-applies the
    /// active process lock and re-filters afterward, same as every other library mutation
    /// (issue #570). No-op (returns false) for a bundled PDK or a path that isn't currently
    /// loaded.
    /// </summary>
    /// <param name="filePath">Full path of the (now-trashed) PDK file, as loaded (<see cref="PdkInfoViewModel.FilePath"/>).</param>
    internal bool UnregisterPdk(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p =>
            p.FilePath != null && Path.GetFullPath(p.FilePath) == normalizedPath);
        if (pdkInfo is null || pdkInfo.IsBundled)
            return false;

        RemoveTemplatesForPdk(pdkInfo.Name);

        var draft = _loadedPdkDrafts.FirstOrDefault(d =>
            d.FilePath != null && Path.GetFullPath(d.FilePath) == normalizedPath);
        if (draft != null)
            _loadedPdkDrafts.Remove(draft);

        PdkManager.UnloadPdkCommand.Execute(pdkInfo);
        _preferencesService.RemoveUserPdkPath(filePath);

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        return true;
    }

    /// <summary>
    /// Removes every <see cref="ComponentTemplate"/> sourced from PDK <paramref name="pdkName"/>
    /// from <see cref="AllTemplates"/>, then drops any category no remaining template uses —
    /// the reverse of the per-component category-add in <see cref="TryReloadUserPdk"/>/
    /// <see cref="CAP.Avalonia.Services.AddCustomComponent.CustomComponentLibraryRegistrar.Register"/>.
    /// </summary>
    private void RemoveTemplatesForPdk(string pdkName)
    {
        var templatesToRemove = AllTemplates.Where(t => t.PdkSource == pdkName).ToList();
        foreach (var template in templatesToRemove)
            AllTemplates.Remove(template);

        foreach (var category in templatesToRemove.Select(t => t.Category).Distinct())
        {
            if (!AllTemplates.Any(t => t.Category == category))
                Categories.Remove(category);
        }
    }

    /// <summary>
    /// Removes a single custom component from its user PDK file — backing the file up to
    /// <c>.trash</c> first, see <c>UserPdkStore.RemoveComponent</c> — and from the library: the
    /// template itself, its category if now unused, and the in-memory draft's component list.
    /// No-op for a bundled/not-loaded template (<see cref="CanEditTemplate"/> guard, the same
    /// double-guard rule as <see cref="UnregisterPdk"/>), a PDK entry with no known file path, or
    /// when the "add custom component" store dependency isn't wired (e.g. some test harnesses) —
    /// or when the store reports nothing was removed (component already gone).
    /// </summary>
    [RelayCommand]
    private void RemoveCustomComponent(ComponentTemplate? template)
    {
        if (template is null || !CanEditTemplate(template))
            return;

        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);
        var userPdkStore = _addCustomComponentDeps?.UserPdkStore;
        if (pdkInfo?.FilePath is null || userPdkStore is null)
            return;

        var result = userPdkStore.RemoveComponent(pdkInfo.FilePath, template.Name);
        if (result is null)
            return;

        AllTemplates.Remove(template);
        if (!AllTemplates.Any(t => t.Category == template.Category))
            Categories.Remove(template.Category);

        var normalizedPath = Path.GetFullPath(pdkInfo.FilePath);
        var draft = _loadedPdkDrafts.FirstOrDefault(d =>
            d.FilePath != null && Path.GetFullPath(d.FilePath) == normalizedPath);
        draft?.Components.RemoveAll(c => string.Equals(c.Name, template.Name, StringComparison.OrdinalIgnoreCase));

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }
}
